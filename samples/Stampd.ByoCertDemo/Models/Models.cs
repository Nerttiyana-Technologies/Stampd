using System.Security.Cryptography.X509Certificates;

namespace Stampd.ByoCertDemo.Models;

/// <summary>
/// Snapshot of cert metadata shown after a successful PFX upload. Cert itself
/// is held by the vault; only this projection goes to the UI.
/// </summary>
public sealed record CertSummary(
    string Subject,
    string Issuer,
    string SerialNumber,
    string Thumbprint,
    DateTime NotBeforeUtc,
    DateTime NotAfterUtc,
    string KeyAlgorithm,
    int KeySize,
    bool IsSelfSigned)
{
    public double DaysUntilExpiry => (NotAfterUtc - DateTime.UtcNow).TotalDays;
    public bool IsExpired => NotAfterUtc < DateTime.UtcNow;
    public bool ExpiresSoon => DaysUntilExpiry < 30 && !IsExpired;

    public static CertSummary From(X509Certificate2 cert)
    {
        string keyAlgo = "Unknown";
        int keySize = 0;
        using (var rsa = cert.GetRSAPublicKey())
        {
            if (rsa is not null) { keyAlgo = "RSA"; keySize = rsa.KeySize; }
        }
        if (keySize == 0)
        {
            using var ecdsa = cert.GetECDsaPublicKey();
            if (ecdsa is not null) { keyAlgo = "ECDSA"; keySize = ecdsa.KeySize; }
        }

        return new CertSummary(
            Subject: cert.Subject,
            Issuer: cert.Issuer,
            SerialNumber: cert.SerialNumber,
            Thumbprint: cert.Thumbprint,
            NotBeforeUtc: cert.NotBefore.ToUniversalTime(),
            NotAfterUtc: cert.NotAfter.ToUniversalTime(),
            KeyAlgorithm: keyAlgo,
            KeySize: keySize,
            IsSelfSigned: string.Equals(cert.Subject, cert.Issuer, StringComparison.Ordinal));
    }
}

/// <summary>
/// Result of a sign operation. Carries the signed PDF bytes so the page can
/// hand them to the browser via a data: URL or download stream.
/// </summary>
public sealed record SignResult(
    byte[] SignedPdfBytes,
    long SignedPdfSizeBytes,
    int OriginalPdfSizeBytes,
    string Sha256Hex,
    DateTimeOffset SignedAtUtc,
    string Profile,
    TimeSpan Duration);
