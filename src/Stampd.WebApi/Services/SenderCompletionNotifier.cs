using System.Net.Mail;
using Microsoft.Extensions.Logging.Abstractions;

using Stampd.Core.Entities;
using Stampd.Core.Notifications;

namespace Stampd.WebApi.Services;

/// <summary>
/// v1.3 #134 — emails the sender (the user who initiated the SigningRequest) once the
/// last recipient has signed. Best-effort: a missing/unparseable sender address or a
/// transient SMTP error is logged and swallowed, never propagated back to the workflow.
/// The completion transaction is already committed before this fires.
/// </summary>
/// <remarks>
/// <para>
/// The sender is identified by <see cref="SigningRequest.CreatedBy"/>. v1 single-tenant /
/// no-auth callers leave this as <c>"api"</c>; v1.3 send-for-signature callers populate it
/// from <c>CreateSigningRequestBody.SenderEmail</c>. When CreatedBy isn't a parseable
/// mailbox (the legacy sentinel, or a future principal-name we can't route) the notifier
/// skips silently.
/// </para>
/// <para>
/// Mirrors the OTP email's executive HTML style (deep-navy gradient header, tables-only
/// layout) so signers and senders see a consistent brand. The optional
/// <see cref="WorkflowEmailOptions.SignedDocumentUrlTemplate"/> drives the call-to-action
/// link — without it the body still ships but tells the sender to head to their dashboard.
/// </para>
/// </remarks>
public sealed class SenderCompletionNotifier
{
    private readonly IEmailSender? _emailSender;
    private readonly WorkflowEmailOptions? _options;
    private readonly ILogger<SenderCompletionNotifier> _logger;

    public SenderCompletionNotifier(
        IEmailSender? emailSender = null,
        WorkflowEmailOptions? options = null,
        ILogger<SenderCompletionNotifier>? logger = null)
    {
        _emailSender = emailSender;
        _options = options;
        _logger = logger ?? NullLogger<SenderCompletionNotifier>.Instance;
    }

    /// <summary>
    /// Sends the completion email. Always returns successfully — exceptions are logged at
    /// warning and absorbed, so the caller never sees email failures.
    /// </summary>
    public async Task NotifyAsync(
        SigningRequest request,
        SignedDocumentRecord signed,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(signed);

        if (_emailSender is null || _options is null)
        {
            return;
        }

        // CreatedBy may be the legacy "api" sentinel for pre-1.3 callers, or any string a
        // future auth layer chooses to stamp. Only proceed when it round-trips as a
        // mailbox address — defensive against principal names like "designer-user".
        if (!TryParseMailbox(request.CreatedBy, out var senderAddress))
        {
            _logger.LogDebug(
                "Sender completion email skipped for request {RequestId}: CreatedBy '{CreatedBy}' is not a routable mailbox.",
                request.Id,
                request.CreatedBy);
            return;
        }

        try
        {
            var downloadUrl = BuildDownloadUrl(signed.Id, request.Id);

            var subject = $"{_options.ProductName}: All recipients signed — {request.Subject}";
            var plain = BuildPlainTextBody(request, signed, downloadUrl);
            var html = BuildHtmlBody(request, signed, downloadUrl);

            await _emailSender.SendAsync(new EmailMessage(
                FromAddress: _options.FromAddress,
                FromDisplayName: _options.FromDisplayName,
                To: [new EmailAddress(senderAddress, request.CreatedBy)],
                Subject: subject,
                PlainTextBody: plain,
                HtmlBody: html),
                ct).ConfigureAwait(false);

            _logger.LogInformation(
                "Sender completion email dispatched for request {RequestId} to {SenderEmail}.",
                request.Id,
                senderAddress);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Treat exactly like invitation email failures — log and move on. The signed
            // document is already persisted; the sender can still download it from their
            // dashboard.
            _logger.LogWarning(
                ex,
                "Sender completion email failed for request {RequestId} → {SenderEmail}; document is still available via the API.",
                request.Id,
                senderAddress);
        }
    }

    private string? BuildDownloadUrl(Guid signedDocumentId, Guid signingRequestId)
    {
        var template = _options?.SignedDocumentUrlTemplate;
        if (string.IsNullOrWhiteSpace(template))
        {
            return null;
        }

        return template
            .Replace("{signedDocumentId}", signedDocumentId.ToString(), StringComparison.Ordinal)
            .Replace("{signingRequestId}", signingRequestId.ToString(), StringComparison.Ordinal);
    }

    private static bool TryParseMailbox(string? value, out string mailbox)
    {
        mailbox = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        // MailAddress is the cheapest builtin RFC 5321-ish check we have without taking
        // a dep on MailKit here. It tolerates display-name wrapping ("Alice <a@x>") and
        // rejects bare identifiers like "designer-user".
        try
        {
            var addr = new MailAddress(value);
            mailbox = addr.Address;
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private string BuildPlainTextBody(SigningRequest request, SignedDocumentRecord signed, string? downloadUrl)
    {
        var product = _options?.ProductName ?? "Stampd";
        var lines = new List<string>
        {
            $"Your {product} document has been fully signed.",
            string.Empty,
            $"Document: {request.Subject}",
            $"Recipients: {request.Recipients.Count} signer(s)",
            $"Completed: {signed.SignedAtUtc:u}",
            $"Document hash (SHA-256): {signed.ContentSha256}",
            string.Empty,
        };

        if (!string.IsNullOrWhiteSpace(downloadUrl))
        {
            lines.Add($"Download the signed PDF: {downloadUrl}");
        }
        else
        {
            lines.Add($"Sign in to your {product} dashboard to download the signed PDF.");
        }

        lines.Add(string.Empty);
        lines.Add($"— {product}");
        lines.Add("This is an automated notification. Do not reply.");

        return string.Join("\n", lines);
    }

    private string BuildHtmlBody(SigningRequest request, SignedDocumentRecord signed, string? downloadUrl)
    {
        var product = _options?.ProductName ?? "Stampd";
        var safeProduct = System.Net.WebUtility.HtmlEncode(product);
        var safeSubject = System.Net.WebUtility.HtmlEncode(request.Subject);
        var safeHash = System.Net.WebUtility.HtmlEncode(signed.ContentSha256);
        var safeCompleted = signed.SignedAtUtc.ToString("yyyy-MM-dd HH:mm:ss 'UTC'zzz", System.Globalization.CultureInfo.InvariantCulture);
        var recipientCount = request.Recipients.Count;

        // CTA block: render an actual button when a URL is configured; otherwise a soft
        // pointer to the dashboard so the email still feels finished.
        var ctaBlock = string.IsNullOrWhiteSpace(downloadUrl)
            ? $@"<p style=""margin:0 0 8px 0;font-size:15px;line-height:1.55;color:#334155;"">
                Sign in to your {safeProduct} dashboard to download the signed PDF.
              </p>"
            : $@"<table role=""presentation"" cellpadding=""0"" cellspacing=""0"" border=""0"" style=""margin:8px 0 8px 0;"">
                <tr>
                  <td align=""center"" style=""background:#0b3c6e;border-radius:8px;"">
                    <a href=""{System.Net.WebUtility.HtmlEncode(downloadUrl)}"" style=""display:inline-block;padding:13px 28px;color:#ffffff;text-decoration:none;font-weight:600;font-size:14px;letter-spacing:0.02em;"">Download the signed PDF</a>
                  </td>
                </tr>
              </table>";

        return $@"<!DOCTYPE html>
<html lang=""en"">
<head>
<meta charset=""utf-8""/>
<meta name=""viewport"" content=""width=device-width,initial-scale=1""/>
<title>{safeProduct}: all recipients signed</title>
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
                  <td align=""right"" style=""font-size:12px;letter-spacing:0.08em;text-transform:uppercase;opacity:0.7;"">Signing complete</td>
                </tr>
              </table>
              <div style=""font-size:24px;font-weight:700;letter-spacing:-0.01em;margin-top:14px;"">All recipients signed</div>
            </td>
          </tr>

          <!-- Body -->
          <tr>
            <td style=""padding:36px;"">
              <p style=""margin:0 0 18px 0;font-size:15px;line-height:1.55;color:#334155;"">
                Every signer on your document has completed their portion. The final, sealed PDF is ready.
              </p>

              <!-- Document summary card -->
              <table role=""presentation"" width=""100%"" cellpadding=""0"" cellspacing=""0"" border=""0"" style=""margin:0 0 24px 0;"">
                <tr>
                  <td style=""background:#f8fafc;border:1px solid #e2e8f0;border-radius:10px;padding:20px 22px;"">
                    <div style=""font-size:11px;letter-spacing:0.12em;text-transform:uppercase;color:#64748b;margin-bottom:6px;"">Document</div>
                    <div style=""font-size:16px;font-weight:600;color:#0f172a;margin-bottom:14px;"">{safeSubject}</div>
                    <table role=""presentation"" width=""100%"" cellpadding=""0"" cellspacing=""0"" border=""0"" style=""font-size:13px;color:#475569;line-height:1.6;"">
                      <tr><td style=""padding:2px 0;"">Recipients</td><td align=""right"" style=""color:#0f172a;font-weight:600;"">{recipientCount}</td></tr>
                      <tr><td style=""padding:2px 0;"">Completed</td><td align=""right"" style=""color:#0f172a;font-weight:600;"">{safeCompleted}</td></tr>
                      <tr><td style=""padding:2px 0;vertical-align:top;"">SHA-256</td><td align=""right"" style=""color:#0f172a;font-family:ui-monospace,SFMono-Regular,Menlo,Monaco,Consolas,monospace;font-size:11px;word-break:break-all;"">{safeHash}</td></tr>
                    </table>
                  </td>
                </tr>
              </table>

              {ctaBlock}

              <p style=""margin:18px 0 0 0;font-size:13px;line-height:1.55;color:#64748b;"">
                <strong style=""color:#334155;"">Audit-grade.</strong> The document is sealed with a PAdES B-LTA signature — long-term verifiable without {safeProduct} being online. Hang onto the SHA-256 above; it's your tamper-evidence fingerprint.
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
}
