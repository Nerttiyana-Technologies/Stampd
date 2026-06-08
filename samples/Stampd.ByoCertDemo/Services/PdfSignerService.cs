using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

using Stampd.ByoCertDemo.Models;
using Stampd.Core;
using Stampd.Crypto.LocalCertificate;
using Stampd.Engine;
using Stampd.Timestamp.FreeTsa;

namespace Stampd.ByoCertDemo.Services;

/// <summary>
/// Wraps the cert-load + engine-sign pipeline behind a single async method.
/// Keeps the Razor page free of any direct PdfSharp / BouncyCastle references —
/// the page just calls TrySignAsync and gets back a SignResult or a failure
/// message.
/// </summary>
public sealed class PdfSignerService
{
    private readonly FreeTsaTimestampAuthorityProvider _tsa;
    private readonly ILogger<PdfSignerService> _logger;

    public PdfSignerService(FreeTsaTimestampAuthorityProvider tsa, ILogger<PdfSignerService> logger)
    {
        _tsa = tsa;
        _logger = logger;
    }

    /// <summary>
    /// Load a PFX from memory bytes + password. Returns the cert on success,
    /// or null with a human-readable error message on failure. The PFX bytes
    /// stay in process memory only — never written to disk.
    /// </summary>
    public (X509Certificate2? Certificate, string? Error) TryLoadCertificate(byte[] pfxBytes, string password)
    {
        ArgumentNullException.ThrowIfNull(pfxBytes);

        try
        {
            // .NET 9+ X509CertificateLoader replaces the deprecated
            // X509Certificate2(byte[], string) constructor (SYSLIB0057).
            var cert = X509CertificateLoader.LoadPkcs12(
                pfxBytes,
                password ?? string.Empty,
                X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet);

            if (!cert.HasPrivateKey)
            {
                cert.Dispose();
                return (null, "The uploaded file is a certificate but contains no private key. " +
                              "Re-export from your source with the private key included (PFX / P12 format).");
            }
            return (cert, null);
        }
        catch (CryptographicException cex) when (cex.Message.Contains("password", StringComparison.OrdinalIgnoreCase))
        {
            return (null, "The password is incorrect or the PFX is not encrypted with that password.");
        }
        catch (CryptographicException)
        {
            return (null, "The uploaded file could not be parsed as a PFX/P12. Make sure it isn't a raw .cer or .crt — those don't include the private key.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unexpected error parsing uploaded PFX");
            return (null, $"Couldn't load the certificate: {ex.Message}");
        }
    }

    /// <summary>
    /// Sign a PDF using the given certificate + FreeTSA timestamp (PAdES B-T).
    /// If <paramref name="signatureImagePng"/> is supplied, the engine renders
    /// it into the signature field rectangle on page 1 — that's what makes
    /// Adobe report a visible signature instead of an invisible one. If null,
    /// a 1×1 transparent placeholder is used (technically valid PAdES, but
    /// Adobe labels the result as "invisible signature").
    /// </summary>
    public async Task<SignResult> SignAsync(
        X509Certificate2 cert,
        byte[] pdfBytes,
        bool useTsa,
        byte[]? signatureImagePng = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(cert);
        ArgumentNullException.ThrowIfNull(pdfBytes);

        var sw = Stopwatch.StartNew();
        var sealingProvider = new LocalCertificateSealingProvider(cert);
        var engine = new PdfSharpStampdEngine(sealingProvider, useTsa ? _tsa : null);

        var signerName = cert.GetNameInfo(X509NameType.SimpleName, forIssuer: false) ?? "Demo Signer";
        var imageBytes = signatureImagePng is { Length: > 0 } ? signatureImagePng : OnePixelPngBytes;
        var request = new SignatureRequest
        {
            SourcePdf = pdfBytes,
            // Single signature field, placed near the bottom-left of page 1.
            Fields = new[]
            {
                new SignatureField(
                    PageNumber: 1,
                    Bounds: new PercentageRect(X: 10.0, Y: 75.0, Width: 35.0, Height: 8.0),
                    Kind: SignatureFieldKind.Signature,
                    SignerId: "demo-signer"),
            },
            FieldValues = new Dictionary<int, ReadOnlyMemory<byte>>
            {
                [0] = imageBytes,
            },
            Sealing = new SealingOptions(),
            Metadata = new SignatureMetadata(
                Reason: "Signed via Stampd bring-your-own-cert demo",
                Location: "Stampd ByoCertDemo",
                ContactInfo: "demo@stampd.local",
                SignerName: signerName),
        };

        var signed = await engine.SignAsync(request, ct).ConfigureAwait(false);
        sw.Stop();

        var signedBytes = signed.SignedPdf.ToArray();
        return new SignResult(
            SignedPdfBytes: signedBytes,
            SignedPdfSizeBytes: signedBytes.LongLength,
            OriginalPdfSizeBytes: pdfBytes.Length,
            Sha256Hex: signed.DocumentHashSha256,
            SignedAtUtc: signed.SignedAtUtc,
            Profile: useTsa ? "PAdES B-T (with RFC 3161 timestamp)" : "PAdES B-B",
            Duration: sw.Elapsed);
    }

    /// <summary>
    /// Compute a quick SHA-256 of the uploaded PDF — purely for display before
    /// the user clicks Sign, so they can see the engine is acting on their bytes.
    /// </summary>
    public static string Sha256Of(byte[] bytes)
    {
        var hash = SHA256.HashData(bytes);
        var sb = new StringBuilder(hash.Length * 2);
        // InvariantCulture pin closes CA1305 — byte.ToString("x2") is
        // culture-invariant in practice (only ASCII hex digits), but the
        // analyzer requires the explicit IFormatProvider parameter.
        foreach (var b in hash) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    private static readonly byte[] OnePixelPngBytes = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNgAAIAAAUAAen63NgAAAAASUVORK5CYII=");
}
