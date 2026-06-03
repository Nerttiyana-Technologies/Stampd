using MailKit.Security;

namespace Stampd.Email.Smtp;

public sealed class SmtpEmailSenderOptions
{
    public string? Host { get; set; }
    public int Port { get; set; } = 587;
    public string? Username { get; set; }
    public string? Password { get; set; }
    public SecureSocketOptions Security { get; set; } = SecureSocketOptions.StartTls;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Host))
        {
            throw new InvalidOperationException(
                $"{nameof(SmtpEmailSenderOptions)}.{nameof(Host)} is required.");
        }
    }
}
