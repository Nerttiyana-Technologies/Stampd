using MailKit.Net.Smtp;

using MimeKit;

using Stampd.Core.Notifications;

namespace Stampd.Email.Smtp;

/// <summary>
/// MailKit-backed SMTP <see cref="IEmailSender"/>. Suitable for any RFC 5321 server,
/// including the in-process dev tool Hermex (which exposes a localhost SMTP listener).
/// </summary>
public sealed class SmtpEmailSender : IEmailSender
{
    private readonly SmtpEmailSenderOptions _options;

    public SmtpEmailSender(SmtpEmailSenderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options;
    }

    /// <inheritdoc />
    public string Name => $"Smtp({_options.Host}:{_options.Port})";

    /// <inheritdoc />
    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress(message.FromDisplayName ?? message.FromAddress, message.FromAddress));
        foreach (var to in message.To)
        {
            mime.To.Add(new MailboxAddress(to.DisplayName ?? to.Address, to.Address));
        }

        mime.Subject = message.Subject;

        var body = new BodyBuilder
        {
            TextBody = message.PlainTextBody,
            HtmlBody = message.HtmlBody,
        };

        if (message.Attachments is { Count: > 0 })
        {
            foreach (var att in message.Attachments)
            {
                body.Attachments.Add(att.FileName, att.Content.ToArray(), ContentType.Parse(att.ContentType));
            }
        }

        mime.Body = body.ToMessageBody();

        using var client = new SmtpClient();
        await client
            .ConnectAsync(_options.Host!, _options.Port, _options.Security, cancellationToken)
            .ConfigureAwait(false);

        if (!string.IsNullOrEmpty(_options.Username))
        {
            await client
                .AuthenticateAsync(_options.Username, _options.Password ?? string.Empty, cancellationToken)
                .ConfigureAwait(false);
        }

        await client.SendAsync(mime, cancellationToken).ConfigureAwait(false);
        await client.DisconnectAsync(quit: true, cancellationToken).ConfigureAwait(false);
    }
}
