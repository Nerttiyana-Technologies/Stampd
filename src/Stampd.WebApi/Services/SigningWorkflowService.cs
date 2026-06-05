using System.Security.Cryptography;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Stampd.Core;
using Stampd.Core.Entities;
using Stampd.Core.Notifications;
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
    private readonly PadesDefaults _padesDefaults;
    private readonly IEmailSender? _emailSender;
    private readonly WorkflowEmailOptions? _emailOptions;
    private readonly WebhookDispatcher? _webhookDispatcher;
    private readonly SenderCompletionNotifier? _senderCompletionNotifier;
    private readonly ILogger<SigningWorkflowService> _logger;

    public SigningWorkflowService(
        StampdDbContext db,
        IDocumentStorageProvider storage,
        IStampdEngine engine,
        PadesDefaults padesDefaults,
        IEmailSender? emailSender = null,
        WorkflowEmailOptions? emailOptions = null,
        WebhookDispatcher? webhookDispatcher = null,
        SenderCompletionNotifier? senderCompletionNotifier = null,
        ILogger<SigningWorkflowService>? logger = null)
    {
        _db = db;
        _storage = storage;
        _engine = engine;
        _padesDefaults = padesDefaults;
        _emailSender = emailSender;
        _emailOptions = emailOptions;
        _webhookDispatcher = webhookDispatcher;
        _senderCompletionNotifier = senderCompletionNotifier;
        _logger = logger ?? NullLogger<SigningWorkflowService>.Instance;
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

        // CreatedBy doubles as the sender's contact address for the v1.3 completion
        // notification email. Until real auth lands, we accept SenderEmail off the request
        // body; the legacy "api" sentinel preserves behavior for pre-1.3 API callers and
        // suppresses the completion email (the notifier checks for a parseable mailbox).
        var senderEmail = body.SenderEmail?.Trim();
        var request = new SigningRequest
        {
            DocumentTemplateId = template.Id,
            Subject = body.Subject,
            Message = body.Message,
            ExpiresAtUtc = body.ExpiresAtUtc,
            Status = SigningRequestStatus.Sent,
            SentAtUtc = now,
            CreatedBy = string.IsNullOrEmpty(senderEmail) ? "api" : senderEmail,
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

        // Emit a RecipientInvited webhook for each initial-routing recipient. The dispatcher
        // queues outbox rows on the SAME DbContext as the workflow state change, so both
        // commit atomically — no risk of a webhook firing for a state change that was rolled
        // back.
        if (_webhookDispatcher is not null)
        {
            foreach (var r in request.Recipients.Where(r => r.Status == RecipientStatus.Invited))
            {
                await _webhookDispatcher.EnqueueAsync(
                    WebhookEventType.RecipientInvited,
                    new { SigningRequestId = request.Id, RecipientId = r.Id, r.Email, r.Name },
                    ct).ConfigureAwait(false);
            }
        }

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        // Best-effort: send invitation emails to recipients we just promoted to Invited.
        // Failures are logged but do NOT roll back the dispatch — the API response still
        // carries access tokens so the caller has a fallback notification channel.
        await TrySendInvitationEmailsAsync(request, ct).ConfigureAwait(false);

        return request;
    }

    private async Task TrySendInvitationEmailsAsync(SigningRequest request, CancellationToken ct)
    {
        if (_emailSender is null || _emailOptions is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_emailOptions.SigningUrlTemplate))
        {
            _logger.LogDebug(
                "Workflow email skipped for request {RequestId}: SigningUrlTemplate not configured.",
                request.Id);
            return;
        }

        foreach (var recipient in request.Recipients.Where(r => r.Status == RecipientStatus.Invited))
        {
            try
            {
                var url = _emailOptions.SigningUrlTemplate.Replace(
                    "{accessToken}",
                    recipient.AccessToken,
                    StringComparison.Ordinal);

                var subject = $"{_emailOptions.ProductName}: Action required — {request.Subject}";
                var plain = BuildInvitationPlainTextBody(recipient, request, url, _emailOptions.ProductName);
                var html = BuildInvitationHtmlBody(recipient, request, url, _emailOptions.ProductName);

                await _emailSender.SendAsync(new EmailMessage(
                    FromAddress: _emailOptions.FromAddress,
                    FromDisplayName: _emailOptions.FromDisplayName,
                    To: [new EmailAddress(recipient.Email, recipient.Name)],
                    Subject: subject,
                    PlainTextBody: plain,
                    HtmlBody: html),
                    ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Invitation email failed for recipient {RecipientId} on request {RequestId}; recipient access URL is still returned via the API response.",
                    recipient.Id,
                    request.Id);
            }
        }
    }

    private static string BuildInvitationPlainTextBody(
        Recipient recipient,
        SigningRequest request,
        string url,
        string productName)
    {
        var lines = new List<string>
        {
            $"Hi {recipient.Name},",
            string.Empty,
            $"You've been asked to sign \"{request.Subject}\" via {productName}.",
        };

        if (!string.IsNullOrWhiteSpace(request.Message))
        {
            lines.Add(string.Empty);
            lines.Add(request.Message);
        }

        lines.Add(string.Empty);
        lines.Add($"Review and sign your document: {url}");
        lines.Add(string.Empty);
        lines.Add($"— {productName}");
        lines.Add("This is an automated message. Do not reply.");

        return string.Join("\n", lines);
    }

    /// <summary>
    /// Executive-grade HTML body for recipient invitations. Matches the OTP and sender-
    /// completion templates (deep-navy → blue gradient header, white card body, dark
    /// footer, tables-only layout) so signers see consistent brand styling end-to-end.
    /// </summary>
    private static string BuildInvitationHtmlBody(
        Recipient recipient,
        SigningRequest request,
        string url,
        string productName)
    {
        var safeName = System.Net.WebUtility.HtmlEncode(recipient.Name);
        var safeProduct = System.Net.WebUtility.HtmlEncode(productName);
        var safeSubject = System.Net.WebUtility.HtmlEncode(request.Subject);
        var safeUrl = System.Net.WebUtility.HtmlEncode(url);
        var safeRoleName = System.Net.WebUtility.HtmlEncode(recipient.Role?.Name ?? "Signer");

        // Optional message block — only render the panel when the sender included a note,
        // otherwise the card looks padded with empty whitespace.
        var messageBlock = string.IsNullOrWhiteSpace(request.Message)
            ? string.Empty
            : $@"<table role=""presentation"" width=""100%"" cellpadding=""0"" cellspacing=""0"" border=""0"" style=""margin:0 0 24px 0;"">
                <tr>
                  <td style=""background:#f8fafc;border:1px solid #e2e8f0;border-left:3px solid #2b7fce;border-radius:8px;padding:16px 20px;font-size:14px;line-height:1.55;color:#334155;"">
                    {System.Net.WebUtility.HtmlEncode(request.Message)}
                  </td>
                </tr>
              </table>";

        return $@"<!DOCTYPE html>
<html lang=""en"">
<head>
<meta charset=""utf-8""/>
<meta name=""viewport"" content=""width=device-width,initial-scale=1""/>
<title>{safeProduct}: action required</title>
</head>
<body style=""margin:0;padding:0;background:#f1f5f9;font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,Helvetica,Arial,sans-serif;color:#0f172a;"">
  <table role=""presentation"" width=""100%"" cellpadding=""0"" cellspacing=""0"" border=""0"" style=""background:#f1f5f9;padding:32px 16px;"">
    <tr>
      <td align=""center"">
        <table role=""presentation"" width=""560"" cellpadding=""0"" cellspacing=""0"" border=""0"" style=""max-width:560px;width:100%;background:#ffffff;border-radius:12px;overflow:hidden;box-shadow:0 4px 14px rgba(11,60,110,0.08);"">
          <!-- Header -->
          <tr>
            <td style=""background:linear-gradient(135deg,#0b3c6e 0%,#1a5698 50%,#2b7fce 100%);padding:28px 36px;color:#ffffff;"">
              <table role=""presentation"" width=""100%"" cellpadding=""0"" cellspacing=""0"" border=""0"">
                <tr>
                  <td style=""font-size:14px;font-weight:600;letter-spacing:0.12em;text-transform:uppercase;opacity:0.85;"">{safeProduct}</td>
                  <td align=""right"" style=""font-size:12px;letter-spacing:0.08em;text-transform:uppercase;opacity:0.7;"">Action required</td>
                </tr>
              </table>
              <div style=""font-size:24px;font-weight:700;letter-spacing:-0.01em;margin-top:14px;"">Your signature is requested</div>
            </td>
          </tr>

          <!-- Body -->
          <tr>
            <td style=""padding:36px;"">
              <p style=""margin:0 0 18px 0;font-size:15px;line-height:1.55;color:#334155;"">
                Hi {safeName},
              </p>
              <p style=""margin:0 0 24px 0;font-size:15px;line-height:1.55;color:#334155;"">
                You've been asked to sign a document as <strong style=""color:#0f172a;"">{safeRoleName}</strong> via {safeProduct}.
              </p>

              <!-- Document summary card -->
              <table role=""presentation"" width=""100%"" cellpadding=""0"" cellspacing=""0"" border=""0"" style=""margin:0 0 24px 0;"">
                <tr>
                  <td style=""background:#f8fafc;border:1px solid #e2e8f0;border-radius:10px;padding:20px 22px;"">
                    <div style=""font-size:11px;letter-spacing:0.12em;text-transform:uppercase;color:#64748b;margin-bottom:6px;"">Document</div>
                    <div style=""font-size:16px;font-weight:600;color:#0f172a;"">{safeSubject}</div>
                  </td>
                </tr>
              </table>

              {messageBlock}

              <!-- CTA -->
              <table role=""presentation"" cellpadding=""0"" cellspacing=""0"" border=""0"" style=""margin:8px 0 8px 0;"">
                <tr>
                  <td align=""center"" style=""background:#0b3c6e;border-radius:8px;"">
                    <a href=""{safeUrl}"" style=""display:inline-block;padding:13px 28px;color:#ffffff;text-decoration:none;font-weight:600;font-size:14px;letter-spacing:0.02em;"">Review and sign your document</a>
                  </td>
                </tr>
              </table>

              <p style=""margin:18px 0 0 0;font-size:13px;line-height:1.55;color:#64748b;"">
                The link above is unique to you. Don't share it — it's how {safeProduct} keeps your signing session secure and audit-ready.
              </p>
            </td>
          </tr>

          <!-- Footer -->
          <tr>
            <td style=""background:#0f172a;padding:18px 36px;color:#94a3b8;font-size:11px;line-height:1.6;text-align:center;"">
              Sent automatically by {safeProduct}. Please do not reply to this message.
            </td>
          </tr>
        </table>
      </td>
    </tr>
  </table>
</body>
</html>";
    }

    /// <summary>Returns the (request, recipient) pair for a given access token, or null if not found.</summary>
    /// <remarks>
    /// Eagerly loads the SigningRequest's sibling Recipients collection — SubmitAsync
    /// relies on it for routing-order promotion, the all-done check, and (v1.3 #134)
    /// the sender completion email's recipient count. Without this Include the
    /// <c>request.Recipients.All(...)</c> check would vacuously succeed after the first
    /// signature and finalize early. Cost is one extra round-trip and a few rows; this
    /// is the canonical entry point for the recipient signing flow.
    /// </remarks>
    public async Task<(SigningRequest Request, Recipient Recipient)?> ResolveByAccessTokenAsync(
        string accessToken,
        CancellationToken ct)
    {
        var recipient = await _db.Recipients
            .Include(r => r.SigningRequest!)
                .ThenInclude(sr => sr.DocumentTemplate!)
                    .ThenInclude(t => t.Fields)
            .Include(r => r.SigningRequest!)
                .ThenInclude(sr => sr.Recipients)
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

        if (_webhookDispatcher is not null)
        {
            await _webhookDispatcher.EnqueueAsync(
                WebhookEventType.RecipientViewed,
                new { SigningRequestId = recipient.SigningRequestId, RecipientId = recipient.Id },
                ct).ConfigureAwait(false);
        }

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Records a successful identity-verification challenge for the recipient. Stamps
    /// IdentityVerifiedAtUtc + IdentityVerificationMethod, writes a RecipientIdentityVerified
    /// audit event, and persists. Idempotent — re-marks return the existing timestamp.
    /// </summary>
    public async Task<DateTimeOffset> MarkIdentityVerifiedAsync(
        Recipient recipient,
        string verificationMethod,
        CancellationToken ct)
    {
        if (recipient.IdentityVerifiedAtUtc is { } already)
        {
            return already;
        }

        var now = DateTimeOffset.UtcNow;
        recipient.IdentityVerifiedAtUtc = now;
        recipient.IdentityVerificationMethod = verificationMethod;

        AddAudit(recipient.SigningRequest!, AuditEventType.RecipientIdentityVerified, now, recipient);

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        return now;
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
        // Persist this recipient's submission for later aggregation. Each recipient owns
        // the fields whose template AssignedRoleId matches this recipient's RoleId; we
        // honor that mapping at finalization rather than trusting the indexes blindly.
        recipient.SubmittedFieldValuesJson = System.Text.Json.JsonSerializer.Serialize(fieldValues);
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
        // Hold a reference so we can pass the completed record to the notifier after
        // SaveChanges — firing the email before commit would risk notifying on a row
        // the workflow ultimately rolls back.
        SignedDocumentRecord? completedSignedDocument = null;

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

            // Aggregate per-recipient submissions. For each template field we accept
            // the value from the recipient whose RoleId matches the field's AssignedRoleId.
            // If a field is unassigned (AssignedRoleId == null) we fall back to the
            // current submitter's values, then to any recipient's, in routing order.
            var engineFieldValues = new Dictionary<int, ReadOnlyMemory<byte>>(orderedFields.Count);
            for (var fieldIndex = 0; fieldIndex < orderedFields.Count; fieldIndex++)
            {
                var templateField = orderedFields[fieldIndex];
                var source = ResolveFieldOwner(
                    request,
                    currentSubmitter: recipient,
                    fieldValues,
                    templateField.AssignedRoleId);

                if (source is null)
                {
                    continue;
                }

                if (!source.TryGetValue(fieldIndex, out var val))
                {
                    continue;
                }

                if (val.ImageBase64 is { Length: > 0 } img)
                {
                    engineFieldValues[fieldIndex] = Convert.FromBase64String(img);
                }
                else if (val.Text is { } text)
                {
                    engineFieldValues[fieldIndex] = System.Text.Encoding.UTF8.GetBytes(text);
                }
            }

            var signRequest = new SignatureRequest
            {
                SourcePdf = sourcePdf,
                Fields = signatureFields,
                FieldValues = engineFieldValues,
                Sealing = _padesDefaults.BuildSealingOptions(),
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
            completedSignedDocument = signedDocument;
        }
        else
        {
            request.Status = SigningRequestStatus.InProgress;
        }

        // Emit lifecycle events. RecipientSigned always fires; SigningRequestCompleted
        // fires only when the workflow finalizes.
        if (_webhookDispatcher is not null)
        {
            await _webhookDispatcher.EnqueueAsync(
                WebhookEventType.RecipientSigned,
                new { SigningRequestId = request.Id, RecipientId = recipient.Id },
                ct).ConfigureAwait(false);

            if (allDone)
            {
                await _webhookDispatcher.EnqueueAsync(
                    WebhookEventType.SigningRequestCompleted,
                    new
                    {
                        SigningRequestId = request.Id,
                        SignedDocumentId = signedDocumentId,
                        DocumentHashSha256 = signedHash,
                    },
                    ct).ConfigureAwait(false);
            }
        }

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        // v1.3 #134 — sender completion notification. Fires only on the transition to
        // Completed and only AFTER commit, so a failed email can never leave the workflow
        // in an inconsistent state. The notifier itself swallows exceptions.
        if (allDone && completedSignedDocument is not null && _senderCompletionNotifier is not null)
        {
            await _senderCompletionNotifier
                .NotifyAsync(request, completedSignedDocument, ct)
                .ConfigureAwait(false);
        }

        return (recipient.Status, request.Status, signedDocumentId, signedHash);
    }

    /// <summary>
    /// Picks the field-value dictionary that "owns" a given template field for the
    /// multi-recipient finalize step. Resolution order:
    /// <list type="number">
    ///   <item>The recipient whose RoleId equals <paramref name="assignedRoleId"/>.</item>
    ///   <item>The current submitter (covers single-recipient and pre-filled fields).</item>
    ///   <item>The earliest-routing recipient with any submitted values (fallback).</item>
    /// </list>
    /// Returns null when nobody has submitted anything for this field.
    /// </summary>
    private static IReadOnlyDictionary<int, ApiFieldValue>? ResolveFieldOwner(
        SigningRequest request,
        Recipient currentSubmitter,
        IReadOnlyDictionary<int, ApiFieldValue> currentSubmitterValues,
        Guid? assignedRoleId)
    {
        if (assignedRoleId is not null)
        {
            var matched = request.Recipients
                .FirstOrDefault(r => r.RoleId == assignedRoleId);
            if (matched is not null)
            {
                if (matched.Id == currentSubmitter.Id)
                {
                    return currentSubmitterValues;
                }

                if (matched.SubmittedFieldValuesJson is not null)
                {
                    return System.Text.Json.JsonSerializer
                        .Deserialize<Dictionary<int, ApiFieldValue>>(matched.SubmittedFieldValuesJson);
                }
            }
        }

        // Pre-filled or unassigned: use the current submitter's values.
        if (currentSubmitterValues.Count > 0)
        {
            return currentSubmitterValues;
        }

        // Last-resort: the earliest-routing recipient who has submitted anything.
        var fallback = request.Recipients
            .Where(r => r.SubmittedFieldValuesJson is not null)
            .OrderBy(r => r.RoutingOrder)
            .FirstOrDefault();
        return fallback?.SubmittedFieldValuesJson is null
            ? null
            : System.Text.Json.JsonSerializer
                .Deserialize<Dictionary<int, ApiFieldValue>>(fallback.SubmittedFieldValuesJson);
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
