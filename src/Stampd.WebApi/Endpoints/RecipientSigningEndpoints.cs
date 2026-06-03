using Microsoft.AspNetCore.Mvc;

using Stampd.Core.Entities;
using Stampd.WebApi.Models;
using Stampd.WebApi.Services;

namespace Stampd.WebApi.Endpoints;

internal static class RecipientSigningEndpoints
{
    public static IEndpointRouteBuilder MapRecipientSigning(this IEndpointRouteBuilder builder)
    {
        var group = builder.MapGroup("/api/sign").WithTags("Recipient");

        group.MapGet("/{accessToken}", GetAsync)
            .WithName("RecipientGetSigningView")
            .WithSummary("Recipient-facing: returns the fields this signer needs to fill. Token is single-use; rotated on resend.");

        group.MapPost("/{accessToken}", SubmitAsync)
            .WithName("RecipientSubmitSignature")
            .WithSummary("Recipient-facing: submit field values. When all required recipients have signed, the document is finalized in the same call.");

        return builder;
    }

    private static async Task<IResult> GetAsync(
        string accessToken,
        [FromServices] SigningWorkflowService workflow,
        CancellationToken ct)
    {
        var pair = await workflow.ResolveByAccessTokenAsync(accessToken, ct).ConfigureAwait(false);
        if (pair is null)
        {
            return Results.NotFound();
        }

        var (request, recipient) = pair.Value;

        if (recipient.Status is RecipientStatus.Signed or RecipientStatus.Declined or RecipientStatus.Expired)
        {
            return Results.Ok(BuildView(request, recipient, fields: []));
        }

        if (recipient.Status == RecipientStatus.Pending)
        {
            return Results.Problem(
                "It's not your turn to sign yet — earlier recipients in the routing order must finish first.",
                statusCode: 409);
        }

        await workflow.MarkViewedAsync(recipient, ct).ConfigureAwait(false);

        // Surface only the fields assigned to this recipient's role (plus any unassigned/global fields).
        var template = request.DocumentTemplate!;
        var ordered = template.Fields
            .OrderBy(f => f.PageNumber)
            .ThenBy(f => f.BoundsY)
            .ThenBy(f => f.BoundsX)
            .ToList();

        var fields = ordered
            .Select((f, idx) => new RecipientFieldView(
                Index: idx,
                PageNumber: f.PageNumber,
                Bounds: new ApiPercentageRect(f.BoundsX, f.BoundsY, f.BoundsWidth, f.BoundsHeight),
                Kind: f.Kind,
                Label: f.Label,
                IsRequired: f.IsRequired))
            .Where(f => IsForRecipient(ordered[f.Index], recipient))
            .ToList();

        return Results.Ok(BuildView(request, recipient, fields));
    }

    private static async Task<IResult> SubmitAsync(
        string accessToken,
        [FromBody] SubmitRecipientSignatureRequest body,
        [FromServices] SigningWorkflowService workflow,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);

        var pair = await workflow.ResolveByAccessTokenAsync(accessToken, ct).ConfigureAwait(false);
        if (pair is null)
        {
            return Results.NotFound();
        }

        var (request, recipient) = pair.Value;

        if (recipient.Status == RecipientStatus.Signed)
        {
            return Results.Problem("You have already signed this document.", statusCode: 409);
        }

        if (recipient.Status == RecipientStatus.Pending)
        {
            return Results.Problem("It's not your turn to sign yet.", statusCode: 409);
        }

        if (request.Status is SigningRequestStatus.Declined or SigningRequestStatus.Voided or SigningRequestStatus.Expired)
        {
            return Results.Problem(
                $"This signing request is in terminal state {request.Status} and can no longer accept signatures.",
                statusCode: 409);
        }

        var (recipientStatus, workflowStatus, signedDocumentId, signedHash) =
            await workflow.SubmitAsync(recipient, body.FieldValues, ct).ConfigureAwait(false);

        return Results.Ok(new RecipientSubmitResponse(
            Status: recipientStatus,
            WorkflowStatus: workflowStatus,
            SignedDocumentId: signedDocumentId,
            SignedDocumentHashSha256: signedHash));
    }

    private static bool IsForRecipient(TemplateField field, Recipient recipient)
    {
        // Unassigned fields are global (date stamps, company logos etc.) — show them too so
        // the recipient can see context, even though they shouldn't be filled.
        if (field.AssignedRoleId is null)
        {
            return true;
        }

        return field.AssignedRoleId == recipient.RoleId;
    }

    private static RecipientSigningView BuildView(
        SigningRequest request,
        Recipient recipient,
        IReadOnlyList<RecipientFieldView> fields)
        => new(
            request.Id,
            request.Subject,
            request.Message,
            recipient.Name,
            recipient.Email,
            recipient.Status,
            fields);
}
