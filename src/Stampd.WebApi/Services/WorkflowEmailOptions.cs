namespace Stampd.WebApi.Services;

/// <summary>
/// Configuration for the invitation emails the workflow service sends to recipients on
/// <see cref="SigningWorkflowService.DispatchAsync"/>. Optional — when the workflow service
/// is constructed without these options or without an <see cref="Stampd.Core.Notifications.IEmailSender"/>,
/// recipients still get their access tokens via the API response and dispatch is otherwise
/// unchanged.
/// </summary>
public sealed class WorkflowEmailOptions
{
    /// <summary>Envelope From address for invitation emails.</summary>
    public string FromAddress { get; init; } = "noreply@stampd.local";

    /// <summary>Envelope From display name.</summary>
    public string? FromDisplayName { get; init; } = "Stampd";

    /// <summary>
    /// URL template for the recipient signing page. The literal substring
    /// <c>{accessToken}</c> is replaced with each recipient's per-invitation token.
    /// Required for emails to actually be sent — when null, dispatch skips emailing.
    /// </summary>
    /// <example><c>https://signing.example.com/sign/{accessToken}</c></example>
    public string? SigningUrlTemplate { get; init; }

    /// <summary>
    /// URL template for the sender completion notification email (v1.3 #134). The literal
    /// substrings <c>{signedDocumentId}</c> and <c>{signingRequestId}</c> are replaced with
    /// the relevant GUIDs. When null, the completion notifier omits the call-to-action link
    /// and the email body invites the sender to log in to their dashboard instead. The
    /// completion email itself still fires 
    /// parses as a mailbox).
    /// </summary>
    /// <example><c>https://app.example.com/sender/signed/{signedDocumentId}</c></example>
    public string? SignedDocumentUrlTemplate { get; init; }

    /// <summary>Optional product name used in subject lines and body templates.</summary>
    public string ProductName { get; set; } = "Stampd";
}
