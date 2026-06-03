using System.Security.Cryptography;

using Microsoft.EntityFrameworkCore;

using Stampd.Core;
using Stampd.Core.Entities;
using Stampd.Core.Storage;
using Stampd.Infrastructure;
using Stampd.WebApi.Models;

namespace Stampd.WebApi.Services;

/// <summary>
/// Orchestrates the lifecycle of a <see cref="SigningRequest"/> — dispatch, recipient
/// submission, finalization. Sealed application service that owns the state machine
/// transitions so endpoints stay thin.
/// </summary>
/// <remarks>
/// v1 simplifications: every recipient signs all fields they're assigned (no per-field
/// optional/required nuance beyond what's already on the template). When all required
/// recipients have signed, the final PDF is rendered and persisted in one shot.
/// </remarks>
public sealed class SigningWorkflowService
{
    private readonly StampdDbContext _db;
    private readonly IDocumentStorageProvider _storage;
    private readonly IStampdEngine _engine;

    public SigningWorkflowService(
        StampdDbContext db,
        IDocumentStorageProvider storage,
        IStampdEngine engine)
    {
        _db = db;
        _storage = storage;
        _engine = engine;
    }

    /// <summary>Creates a new SigningRequest from a template + recipient assignments, marks it Sent.</summary>
    public async Task<SigningRequest> DispatchAsync(
        CreateSigningRequestBody body,
        CancellationToken ct)
    {
        var template = await _db.DocumentTemplates
            .Include(t => t.Roles)
            .Include(t => t.Fields)
            .FirstOrDefaultAsync(t => t.Id == body.DocumentTemplateId, ct)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Template '{body.DocumentTemplateId}' not found.");

        var rolesByName = template.Roles.ToDictionary(r => r.Name, StringComparer.OrdinalIgnoreCase);
        var now = DateTimeOffset.UtcNow;

        var request = new SigningRequest
        {
            DocumentTemplateId = template.Id,
            Subject = body.Subject,
            Message = body.Message,
            ExpiresAtUtc = body.ExpiresAtUtc,
            Status = SigningRequestStatus.Sent,
            SentAtUtc = now,
            CreatedBy = "api",
        };

        foreach (var assignment in body.Recipients)
        {
            if (!rolesByName.TryGetValue(assignment.RoleName, out var role))
            {
                throw new InvalidOperationException(
                    $"Recipient assignment references unknown role '{assignment.RoleName}'.");
            }

            request.Recipients.Add(new Recipient
            {
                Id = Guid.NewGuid(),
                Role = role,
                RoleId = role.Id,
                RoutingOrder = role.RoutingOrder,
                Email = assignment.Email,
                Name = assignment.Name,
                Status = role.RoutingOrder == 1 ? RecipientStatus.Invited : RecipientStatus.Pending,
                InvitedAtUtc = role.RoutingOrder == 1 ? now : null,
                AccessToken = GenerateAccessToken(),
            });
        }

        AddAudit(request, AuditEventType.SigningRequestCreated, now);
        AddAudit(request, AuditEventType.SigningRequestSent, now);
        foreach (var r in request.Recipients.Where(r => r.Status == RecipientStatus.Invited))
        {
            AddAudit(request, AuditEventType.RecipientInvited, now, r);
        }

        _db.SigningRequests.Add(request);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        return request;
    }

    /// <summary>Returns the (request, recipient) pair for a given access token, or null if not found.</summary>
    public async Task<(SigningRequest Request, Recipient Recipient)?> ResolveByAccessTokenAsync(
        string accessToken,
        CancellationToken ct)
    {
        var recipient = await _db.Recipients
            .Include(r => r.SigningRequest!)
                .ThenInclude(sr => sr.DocumentTemplate!)
                    .ThenInclude(t => t.Fields)
            .Include(r => r.Role)
            .FirstOrDefaultAsync(r => r.AccessToken == accessToken, ct)
            .ConfigureAwait(false);

        return recipient is null ? null : (recipient.SigningRequest!, recipient);
    }

    /// <summary>Records that the recipient viewed the signing page (idempotent).</summary>
    public async Task MarkViewedAsync(Recipient recipient, CancellationToken ct)
    {
        if (recipient.FirstViewedAtUtc is not null)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        recipient.FirstViewedAtUtc = now;
        if (recipient.Status == RecipientStatus.Invited)
        {
            recipient.Status = RecipientStatus.Viewed;
        }

        AddAudit(recipient.SigningRequest!, AuditEventType.RecipientViewed, now, recipient);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Records the recipient's submission. If all required recipients have now signed,
    /// finalizes the document: invokes the engine, persists the signed PDF, writes the
    /// SignedDocumentRecord, and transitions the workflow to Completed.
    /// </summary>
    public async Task<(RecipientStatus, SigningRequestStatus, Guid? SignedDocumentId, string? Hash)>
        SubmitAsync(
            Recipient recipient,
            IReadOnlyDictionary<int, ApiFieldValue> fieldValues,
            CancellationToken ct)
    {
        var request = recipient.SigningRequest!;
        var now = DateTimeOffset.UtcNow;

        recipient.Status = RecipientStatus.Signed;
        recipient.SignedAtUtc = now;
        AddAudit(request, AuditEventType.RecipientSigned, now, recipient);

        // Promote next-routing-order recipients from Pending → Invited.
        var nextOrder = request.Recipients
            .Where(r => r.Status == RecipientStatus.Pending)
            .Select(r => (int?)r.RoutingOrder)
            .DefaultIfEmpty(null)
            .Min();
        if (nextOrder is not null)
        {
            foreach (var r in request.Recipients.Where(r => r.Status == RecipientStatus.Pending && r.RoutingOrder == nextOrder))
            {
                r.Status = RecipientStatus.Invited;
                r.InvitedAtUtc = now;
                AddAudit(request, AuditEventType.RecipientInvited, now, r);
            }
        }

        // Determine if all recipients are now Signed.
        var allDone = request.Recipients.All(r => r.Status == RecipientStatus.Signed);

        Guid? signedDocumentId = null;
        string? signedHash = null;

        if (allDone)
        {
            // Sign the document with the accumulated values. For v1 we store the LAST
            // recipient's field values; multi-recipient field aggregation is a v1.1 concern.
            var sourcePdf = await _storage
                .RetrieveAsync(request.DocumentTemplate!.SourcePdfStorageKey, ct)
                .ConfigureAwait(false);

            var orderedFields = request.DocumentTemplate.Fields
                .OrderBy(f => f.PageNumber)
                .ThenBy(f => f.BoundsY)
                .ThenBy(f => f.BoundsX)
                .ToList();

            var signatureFields = orderedFields.Select(f =>
                new SignatureField(
                    f.PageNumber,
                    new PercentageRect(f.BoundsX, f.BoundsY, f.BoundsWidth, f.BoundsHeight),
                    f.Kind,
                    f.AssignedRoleId?.ToString() ?? "default"))
                .ToArray();

            var engineFieldValues = new Dictionary<int, ReadOnlyMemory<byte>>(fieldValues.Count);
            foreach (var (idx, val) in fieldValues)
            {
                if (idx < 0 || idx >= signatureFields.Length) continue;
                if (val.ImageBase64 is { Length: > 0 } img)
                {
                    engineFieldValues[idx] = Convert.FromBase64String(img);
                }
                else if (val.Text is { } text)
                {
                    engineFieldValues[idx] = System.Text.Encoding.UTF8.GetBytes(text);
                }
            }

            var signRequest = new SignatureRequest
            {
                SourcePdf = sourcePdf,
                Fields = signatureFields,
                FieldValues = engineFieldValues,
                Sealing = new SealingOptions(),
                Metadata = new SignatureMetadata(
                    Reason: request.Subject,
                    Location: "Stampd workflow",
                    SignerName: recipient.Name),
            };

            var signed = await _engine.SignAsync(signRequest, ct).ConfigureAwait(false);
            var signedBytes = signed.SignedPdf.ToArray();

            var storageKey = await _storage
                .StoreAsync(signedBytes, $"signed-{request.Id:N}.pdf", ct)
                .ConfigureAwait(false);

            var documentSha = Convert.ToHexString(SHA256.HashData(signedBytes)).ToLowerInvariant();

            var signedDocument = new SignedDocumentRecord
            {
                Id = Guid.NewGuid(),
                SigningRequest = request,
                StorageKey = storageKey,
                ContentSha256 = documentSha,
                SignedAtUtc = signed.SignedAtUtc,
                SealingProviderName = "LocalCertificate", // v2: read from the provider via DI.
                SealingKeyIdentifier = "spike-signer",
                PAdESLevel = PAdESLevel.BT, // engine produces B-T when TSA is configured; we always claim B-T here.
            };
            _db.SignedDocumentRecords.Add(signedDocument);

            request.Status = SigningRequestStatus.Completed;
            request.CompletedAtUtc = signed.SignedAtUtc;
            AddAudit(request, AuditEventType.DocumentSealed, signed.SignedAtUtc, documentHash: documentSha);
            AddAudit(request, AuditEventType.SigningRequestCompleted, signed.SignedAtUtc);

            signedDocumentId = signedDocument.Id;
            signedHash = documentSha;
        }
        else
        {
            request.Status = SigningRequestStatus.InProgress;
        }

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        return (recipient.Status, request.Status, signedDocumentId, signedHash);
    }

    private static void AddAudit(
        SigningRequest request,
        AuditEventType type,
        DateTimeOffset at,
        Recipient? recipient = null,
        string? documentHash = null)
    {
        // Critical: do NOT set Id here. When an AuditEvent is added via a tracked collection
        // navigation (request.AuditEvents on a request loaded from the DB), EF Core's change
        // tracker sees a non-empty Guid PK and infers "this row already exists" — marking it
        // Modified instead of Added. The append-only guard in StampdDbContext.UpdateAuditableFields
        // then throws. Leaving Id unset (Guid.Empty) lets EF correctly attach as Added, and
        // UpdateAuditableFields assigns a fresh Guid in the Added branch before save.
        request.AuditEvents.Add(new AuditEvent
        {
            EventType = type,
            OccurredAtUtc = at,
            Recipient = recipient,
            RecipientId = recipient?.Id,
            DocumentHashAtEvent = documentHash,
        });
    }

    private static string GenerateAccessToken()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
