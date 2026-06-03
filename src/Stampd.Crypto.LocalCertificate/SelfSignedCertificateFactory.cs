using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Stampd.Crypto.LocalCertificate;

/// <summary>
/// Stable details a freshly-created persistent cert is written to disk with. Useful for
/// the smoke test, which prints the location so the developer can import the <c>.cer</c>
/// file into Adobe Acrobat's trusted certificates list once and have every subsequent
/// signed PDF show as fully green.
/// </summary>
/// <param name="Pkcs12Path">Path to the PKCS#12 file (private key + cert).</param>
/// <param name="PublicCertPath">Path to the <c>.cer</c> file (public part only).</param>
/// <param name="WasCreatedNow">True if the cert was generated on this call; false if loaded from disk.</param>
public sealed record PersistentCertificateLocation(string Pkcs12Path, string PublicCertPath, bool WasCreatedNow);

/// <summary>
/// Generates throwaway self-signed certificates for development, the engine smoke test,
/// and unit tests.
/// </summary>
/// <remarks>
/// This is strictly for development and the smoke test. Production signing must come from
/// a CA-issued certificate served via <see cref="LocalCertificateSealingProvider"/>, or
/// from one of the HSM-backed providers. A self-signed certificate will sign a PDF
/// successfully but Adobe Acrobat will show the signature as "valid but signer's identity
/// is unknown" until the certificate is added to a trusted root store.
/// </remarks>
public static class SelfSignedCertificateFactory
{
    /// <summary>
    /// Creates a new RSA 2048 self-signed certificate suitable for document signing.
    /// </summary>
    /// <param name="subjectCommonName">CN to use in the certificate subject.</param>
    /// <param name="validity">How long the certificate should remain valid. Defaults to 1 year.</param>
    public static X509Certificate2 Create(
        string subjectCommonName = "Stampd Spike Signer",
        TimeSpan? validity = null)
    {
        validity ??= TimeSpan.FromDays(365);

        using var rsa = RSA.Create(2048);

        var request = new CertificateRequest(
            $"CN={subjectCommonName}, O=Stampd, OU=Engine Spike",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation,
            critical: true));

        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection
            {
                // 1.3.6.1.5.5.7.3.36 = id-kp-documentSigning (RFC 9336).
                new Oid("1.3.6.1.5.5.7.3.36"),
            },
            critical: false));

        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(
            certificateAuthority: false,
            hasPathLengthConstraint: false,
            pathLengthConstraint: 0,
            critical: true));

        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(
            request.PublicKey,
            critical: false));

        var notBefore = DateTimeOffset.UtcNow.AddMinutes(-5);
        var notAfter = notBefore.Add(validity.Value);

        return request.CreateSelfSigned(notBefore, notAfter);
    }

    /// <summary>
    /// Loads an existing PKCS#12 cert from <paramref name="pkcs12Path"/>, or generates a
    /// new self-signed cert and persists it there (plus a sibling <c>.cer</c> file with
    /// the public part only). Subsequent calls reuse the same cert, so the thumbprint is
    /// stable across runs — which means importing the <c>.cer</c> into Adobe Acrobat's
    /// trusted certificates list once will keep working for every subsequent signed PDF.
    /// </summary>
    /// <param name="pkcs12Path">Path to the PKCS#12 file. Created if missing.</param>
    /// <param name="password">Password protecting the PKCS#12 file.</param>
    /// <param name="subjectCommonName">CN used when creating a new cert. Ignored when loading.</param>
    /// <param name="validity">Validity period when creating a new cert. Defaults to 5 years (so it survives demo cycles).</param>
    /// <returns>The loaded-or-created certificate and metadata about where it landed.</returns>
    public static (X509Certificate2 Certificate, PersistentCertificateLocation Location) CreateOrLoad(
        string pkcs12Path,
        string password = "stampd-spike",
        string subjectCommonName = "Stampd Spike Signer",
        TimeSpan? validity = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(pkcs12Path);
        ArgumentNullException.ThrowIfNull(password);

        var cerPath = Path.ChangeExtension(pkcs12Path, ".cer");

        if (File.Exists(pkcs12Path))
        {
            var loaded = X509CertificateLoader.LoadPkcs12FromFile(
                pkcs12Path,
                password,
                X509KeyStorageFlags.Exportable);
            return (loaded, new PersistentCertificateLocation(pkcs12Path, cerPath, WasCreatedNow: false));
        }

        var fresh = Create(subjectCommonName, validity ?? TimeSpan.FromDays(365 * 5));

        var directory = Path.GetDirectoryName(pkcs12Path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllBytes(pkcs12Path, fresh.Export(X509ContentType.Pkcs12, password));
        File.WriteAllBytes(cerPath, fresh.Export(X509ContentType.Cert));

        return (fresh, new PersistentCertificateLocation(pkcs12Path, cerPath, WasCreatedNow: true));
    }
}
