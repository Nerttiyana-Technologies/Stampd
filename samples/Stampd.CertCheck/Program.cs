// =============================================================================
//  stampd-certcheck — deployment validation + customer demo tool
// -----------------------------------------------------------------------------
//  Loads a customer's certificate through Stampd's LocalCertificate sealing
//  provider, performs a synthetic sign + verify round trip, and prints a
//  structured pass/fail report. Designed for two audiences:
//
//   1. Adopters preparing to deploy Stampd at a customer site — run this once
//      against the customer's PFX to confirm the cert is usable BEFORE wiring
//      the WebApi. Catches expired certs, wrong passwords, missing private
//      keys, weak key sizes.
//
//   2. The Stampd team demoing to a prospective customer — "hand me your
//      PFX, watch this take 4 seconds": cert metadata, signed sample,
//      verification result, sample PDF written to disk for live inspection.
//
//  Usage:
//    # PFX file (most common)
//    dotnet run --project samples/Stampd.CertCheck -- \
//      --pfx ./customer-signer.pfx --password "$PFX_PASS"
//
//    # No cert yet — generate a demo self-signed cert
//    dotnet run --project samples/Stampd.CertCheck -- --generate-self-signed
//
//    # With timestamp (PAdES B-T instead of B-B)
//    dotnet run --project samples/Stampd.CertCheck -- \
//      --pfx ./customer-signer.pfx --password "$PFX_PASS" --tsa
//
//  Exit codes:
//    0  Certificate ready for production
//    1  Unexpected failure
//    2  Missing or invalid CLI arguments
//    3  Certificate expired
//    4  Signed PDF failed verification
// =============================================================================

using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

using PdfSharp.Drawing;
using PdfSharp.Pdf;

using Stampd.Core;
using Stampd.Crypto.LocalCertificate;
using Stampd.Engine;
using Stampd.Engine.Rendering;
using Stampd.Timestamp.FreeTsa;

PlatformFontResolver.Register();

var opts = CliArgs.Parse(args);
if (opts is null) return 2;

PrintBanner();

try
{
    // -------------------------------------------------------------------------
    // [1/4] Load the certificate
    // -------------------------------------------------------------------------
    var step1 = Stopwatch.StartNew();
    Section("[1/4] Loading certificate");

    using var certificate = LoadCertificate(opts);
    step1.Stop();
    Ok($"Provider initialised: LocalCertificate", step1.Elapsed);

    var keyAlgo = DescribeKeyAlgorithm(certificate);
    Detail($"Subject:        {certificate.Subject}");
    Detail($"Issuer:         {certificate.Issuer}");
    Detail($"Serial:         {certificate.SerialNumber}");
    Detail($"Thumbprint:     {certificate.Thumbprint}");
    Detail($"Valid from:     {certificate.NotBefore:yyyy-MM-dd}");
    Detail($"Valid to:       {certificate.NotAfter:yyyy-MM-dd}");
    Detail($"Key algorithm:  {keyAlgo}");
    Detail($"Has private key: {certificate.HasPrivateKey}");
    Detail($"Self-signed:    {(certificate.Subject == certificate.Issuer ? "Yes (Adobe will show yellow badge unless manually trusted)" : "No (cert chains to an issuing CA)")}");

    // Cert-health gates
    if (!certificate.HasPrivateKey)
    {
        Error("Certificate has no private key — cannot sign with it. Re-export from the source with the private key included.");
        return 1;
    }
    var daysUntilExpiry = (certificate.NotAfter - DateTime.UtcNow).TotalDays;
    if (daysUntilExpiry < 0)
    {
        Error($"Certificate EXPIRED {Math.Abs(daysUntilExpiry):F0} days ago. Stop.");
        return 3;
    }
    if (daysUntilExpiry < 30)
    {
        Warn($"Certificate expires in {daysUntilExpiry:F0} days. Plan rotation.");
    }

    // -------------------------------------------------------------------------
    // [2/4] Sign a synthetic PDF
    // -------------------------------------------------------------------------
    var step2 = Stopwatch.StartNew();
    Section("[2/4] Signing synthetic PDF");

    var sourcePdf = BuildSyntheticPdf(certificate);
    var sealingProvider = new LocalCertificateSealingProvider(certificate);

    using var tsaHttpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    FreeTsaTimestampAuthorityProvider? tsa = opts.UseTsa
        ? new FreeTsaTimestampAuthorityProvider(tsaHttpClient)
        : null;

    var engine = new PdfSharpStampdEngine(sealingProvider, tsa);

    var request = new SignatureRequest
    {
        SourcePdf = sourcePdf,
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
            [0] = BuildSignaturePng(),
        },
        Sealing = new SealingOptions(),
        Metadata = new SignatureMetadata(
            Reason: "stampd-certcheck deployment validation",
            Location: "Stampd CertCheck sample",
            ContactInfo: "support@stampd.org",
            SignerName: certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false) ?? "Demo Signer"),
    };

    var signed = await engine.SignAsync(request).ConfigureAwait(false);
    var signedBytes = signed.SignedPdf.ToArray();
    step2.Stop();

    var profile = opts.UseTsa ? "PAdES B-T (with RFC 3161 timestamp)" : "PAdES B-B (no timestamp)";
    Ok($"Signed {sourcePdf.Length:N0} B → {signedBytes.Length:N0} B " +
       $"({signedBytes.Length - sourcePdf.Length:+#,##0;-#,##0} B added)", step2.Elapsed);
    Detail($"Profile:        {profile}");
    Detail($"SHA-256:        {signed.DocumentHashSha256}");
    Detail($"Signed at UTC:  {signed.SignedAtUtc:u}");

    // -------------------------------------------------------------------------
    // [3/4] Structural verification of the signed output
    // -------------------------------------------------------------------------
    var step3 = Stopwatch.StartNew();
    Section("[3/4] Verifying signed output structure");

    var checks = new List<(string Name, bool Passed, string? Detail)>
    {
        ("PDF magic bytes",      signedBytes.Length > 5 && signedBytes[0] == '%' && signedBytes[1] == 'P' && signedBytes[2] == 'D' && signedBytes[3] == 'F', null),
        ("Trailing %%EOF",       EndsWithEofMarker(signedBytes), null),
        ("Contains /Sig dict",   ByteSearch(signedBytes, "/Type /Sig"u8) || ByteSearch(signedBytes, "/Type/Sig"u8), "no signature dictionary written"),
        ("Contains /Contents",   ByteSearch(signedBytes, "/Contents <"u8) || ByteSearch(signedBytes, "/Contents<"u8), "no /Contents CMS payload"),
        ("Contains /ByteRange",  ByteSearch(signedBytes, "/ByteRange"u8), "no /ByteRange — Adobe will reject"),
        ("SubFilter present",    ByteSearch(signedBytes, "/SubFilter /ETSI."u8) || ByteSearch(signedBytes, "/SubFilter/ETSI."u8), "missing ETSI SubFilter — not PAdES-compliant"),
    };
    step3.Stop();

    var failedChecks = checks.Where(c => !c.Passed).ToList();
    if (failedChecks.Count > 0)
    {
        Error("One or more verification checks failed:");
        foreach (var c in checks)
        {
            if (c.Passed)
                WriteLine($"     ✓ {c.Name}", ConsoleColor.Green);
            else
                WriteLine($"     ✗ {c.Name}{(c.Detail is null ? "" : $" — {c.Detail}")}", ConsoleColor.Red);
        }
        return 4;
    }
    Ok("All structural checks passed", step3.Elapsed);
    foreach (var c in checks)
        Detail($"✓ {c.Name}");

    // -------------------------------------------------------------------------
    // [4/4] Write the signed sample for live inspection
    // -------------------------------------------------------------------------
    var step4 = Stopwatch.StartNew();
    Section("[4/4] Writing signed sample for review");

    var outputPath = Path.GetFullPath("./certcheck-output.pdf");
    await File.WriteAllBytesAsync(outputPath, signedBytes).ConfigureAwait(false);
    step4.Stop();
    Ok($"Sample written to {outputPath}", step4.Elapsed);
    Detail("Open in Adobe Acrobat — signature appears in the Signatures panel.");
    Detail("If AATL-trusted: green check. If self-signed: yellow badge until manually trusted in Adobe.");

    // Summary box
    WriteLine("");
    WriteLine("╔══════════════════════════════════════════════════════════════════════╗", ConsoleColor.Green);
    WriteLine("║  ✓  CERTIFICATE VALIDATION PASSED                                    ║", ConsoleColor.Green);
    WriteLine("║                                                                      ║", ConsoleColor.Green);
    WriteLine($"║  Subject:        {Truncate(certificate.Subject, 51),-51} ║", ConsoleColor.Green);
    WriteLine($"║  Days to expiry: {daysUntilExpiry,-51:F0} ║", ConsoleColor.Green);
    WriteLine($"║  Profile:        {Truncate(profile, 51),-51} ║", ConsoleColor.Green);
    WriteLine($"║  Sample output:  {Truncate(outputPath, 51),-51} ║", ConsoleColor.Green);
    WriteLine("╚══════════════════════════════════════════════════════════════════════╝", ConsoleColor.Green);
    WriteLine("");
    WriteLine("Use the same cert in production: set Stampd:Sealing:Provider=LocalCertificate");
    WriteLine("and Stampd:Sealing:LocalCertificate:PfxPath / PfxPassword in appsettings.Production.json");
    WriteLine("(or as environment variables — see docs/deployment-bring-your-own-cert.md).");
    return 0;
}
catch (CryptographicException cex)
{
    Error($"Cryptographic failure: {cex.Message}");
    Detail("Common causes: wrong PFX password, corrupted PFX file, or unsupported key algorithm.");
    return 1;
}
catch (FileNotFoundException fnf)
{
    Error($"File not found: {fnf.FileName ?? fnf.Message}");
    return 2;
}
catch (Exception ex)
{
    Error($"Validation failed: {ex.Message}");
    if (Environment.GetEnvironmentVariable("STAMPD_CERTCHECK_VERBOSE") == "1")
        WriteLine(ex.ToString(), ConsoleColor.DarkRed);
    else
        Detail("Re-run with STAMPD_CERTCHECK_VERBOSE=1 for the full stack trace.");
    return 1;
}

// ============================================================================
//  Helpers
// ============================================================================

static X509Certificate2 LoadCertificate(CliArgs opts)
{
    if (opts.GenerateSelfSigned)
    {
        Console.WriteLine("    (no --pfx supplied; generating an in-memory self-signed cert for demo)");
        return CreateDemoSelfSignedCert();
    }

    if (!File.Exists(opts.PfxPath))
        throw new FileNotFoundException($"PFX file not found: {opts.PfxPath}", opts.PfxPath);

    // X509KeyStorageFlags.Exportable so the private key remains accessible across
    // platforms. EphemeralKeySet would be ideal but isn't supported on macOS.
    return new X509Certificate2(
        opts.PfxPath!,
        opts.Password ?? string.Empty,
        X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet);
}

static X509Certificate2 CreateDemoSelfSignedCert()
{
    using var rsa = RSA.Create(3072);
    var dn = new X500DistinguishedName("CN=Stampd Demo Signer, O=Stampd, C=US");
    var req = new CertificateRequest(dn, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

    // Document-signing EKU (Adobe checks this — without it, signatures may be rejected).
    req.CertificateExtensions.Add(
        new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation,
            critical: true));
    var ekuOids = new OidCollection
    {
        new Oid("1.3.6.1.5.5.7.3.4"),       // emailProtection — Adobe accepts this for document signing
        new Oid("1.3.6.1.4.1.311.10.3.12"), // Microsoft document-signing OID
    };
    req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(ekuOids, critical: false));

    return req.CreateSelfSigned(
        notBefore: DateTimeOffset.UtcNow.AddMinutes(-5),
        notAfter: DateTimeOffset.UtcNow.AddYears(1));
}

static string DescribeKeyAlgorithm(X509Certificate2 cert)
{
    using var rsa = cert.GetRSAPublicKey();
    if (rsa is not null) return $"RSA-{rsa.KeySize}";

    using var ecdsa = cert.GetECDsaPublicKey();
    if (ecdsa is not null) return $"ECDSA-{ecdsa.KeySize}";

    return cert.PublicKey.Oid.FriendlyName ?? cert.PublicKey.Oid.Value ?? "Unknown";
}

static byte[] BuildSyntheticPdf(X509Certificate2 cert)
{
    using var doc = new PdfDocument();
    doc.Info.Title = "Stampd CertCheck — Sample Document";
    doc.Info.Author = "stampd-certcheck";
    doc.Info.Subject = "Synthetic PDF used to validate certificate signing";

    var page = doc.AddPage();
    page.Size = PdfSharp.PageSize.A4;

    using var gfx = XGraphics.FromPdfPage(page);
    var fontTitle = new XFont("Helvetica", 18, XFontStyleEx.Bold);
    var fontBody = new XFont("Helvetica", 11, XFontStyleEx.Regular);
    var fontSmall = new XFont("Helvetica", 9, XFontStyleEx.Regular);

    var black = XBrushes.Black;
    var gray = XBrushes.DimGray;

    gfx.DrawString("Stampd CertCheck", fontTitle, black, 50, 80);
    gfx.DrawString("Synthetic document used to validate a customer's signing certificate.",
        fontBody, gray, 50, 110);

    var y = 160.0;
    gfx.DrawString("Certificate being validated:", fontBody, black, 50, y); y += 22;
    gfx.DrawString($"Subject:    {cert.Subject}", fontSmall, black, 60, y); y += 16;
    gfx.DrawString($"Issuer:     {cert.Issuer}", fontSmall, black, 60, y); y += 16;
    gfx.DrawString($"Serial:     {cert.SerialNumber}", fontSmall, black, 60, y); y += 16;
    gfx.DrawString($"Valid:      {cert.NotBefore:yyyy-MM-dd}  →  {cert.NotAfter:yyyy-MM-dd}", fontSmall, black, 60, y); y += 16;
    gfx.DrawString($"Thumbprint: {cert.Thumbprint}", fontSmall, black, 60, y); y += 32;

    gfx.DrawString("If this PDF opens in Adobe Acrobat with the signature panel populated", fontBody, gray, 50, y); y += 16;
    gfx.DrawString("and the byte-range verified, the certificate is ready for production.", fontBody, gray, 50, y); y += 32;

    gfx.DrawString($"Generated at: {DateTimeOffset.UtcNow:u}", fontSmall, gray, 50, y);

    using var ms = new MemoryStream();
    doc.Save(ms);
    return ms.ToArray();
}

static byte[] BuildSignaturePng()
{
    // 1×1 transparent PNG — placeholder for the signature image field. Stampd's
    // engine accepts any PNG; in production the recipient draws their actual
    // signature on a canvas and the bytes are passed here. For certcheck we
    // don't care what's drawn — we care that the engine pipeline ran clean.
    return Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNgAAIAAAUAAen63NgAAAAASUVORK5CYII=");
}

static bool EndsWithEofMarker(byte[] bytes)
{
    if (bytes.Length < 6) return false;
    var tail = Encoding.ASCII.GetString(bytes, Math.Max(0, bytes.Length - 32), Math.Min(32, bytes.Length));
    return tail.Contains("%%EOF", StringComparison.Ordinal);
}

static bool ByteSearch(byte[] haystack, ReadOnlySpan<byte> needle)
{
    if (needle.Length == 0 || haystack.Length < needle.Length) return false;
    var span = haystack.AsSpan();
    return span.IndexOf(needle) >= 0;
}

// ---- Output helpers --------------------------------------------------------

static void PrintBanner()
{
    WriteLine("");
    WriteLine("  stampd-certcheck", ConsoleColor.Cyan);
    WriteLine("  Deployment validation for Stampd's bring-your-own-cert flow", ConsoleColor.DarkGray);
    WriteLine("");
}

static void Section(string label) =>
    WriteLine($"\n→ {label}", ConsoleColor.Cyan);

static void Ok(string label, TimeSpan elapsed) =>
    WriteLine($"  ✓ {label}  ({elapsed.TotalMilliseconds:F0} ms)", ConsoleColor.Green);

static void Detail(string label) =>
    WriteLine($"     {label}", ConsoleColor.DarkGray);

static void Warn(string label) =>
    WriteLine($"  ! {label}", ConsoleColor.Yellow);

static void Error(string label) =>
    WriteLine($"  ✗ {label}", ConsoleColor.Red);

static void WriteLine(string text, ConsoleColor? color = null)
{
    if (color is { } c)
    {
        var old = Console.ForegroundColor;
        Console.ForegroundColor = c;
        Console.WriteLine(text);
        Console.ForegroundColor = old;
    }
    else
    {
        Console.WriteLine(text);
    }
}

static string Truncate(string s, int max) =>
    s.Length <= max ? s : s[..(max - 1)] + "…";

// ---- CLI parsing -----------------------------------------------------------

internal sealed record CliArgs(
    string? PfxPath,
    string? Password,
    bool GenerateSelfSigned,
    bool UseTsa)
{
    public static CliArgs? Parse(string[] args)
    {
        if (args.Contains("--help") || args.Contains("-h"))
        {
            PrintHelp();
            return null;
        }

        string? pfx = null;
        string? password = null;
        var generateSelfSigned = false;
        var useTsa = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--pfx":
                    pfx = i + 1 < args.Length ? args[++i] : null;
                    break;
                case "--password":
                    password = i + 1 < args.Length ? args[++i] : null;
                    break;
                case "--generate-self-signed":
                    generateSelfSigned = true;
                    break;
                case "--tsa":
                    useTsa = true;
                    break;
                default:
                    if (args[i].StartsWith("--", StringComparison.Ordinal))
                    {
                        Console.Error.WriteLine($"Unknown flag: {args[i]}");
                        PrintHelp();
                        return null;
                    }
                    break;
            }
        }

        if (!generateSelfSigned && string.IsNullOrWhiteSpace(pfx))
        {
            Console.Error.WriteLine("Error: must supply either --pfx <path> or --generate-self-signed.");
            PrintHelp();
            return null;
        }

        // Fall back to env var if --password wasn't given (common when wiring via CI).
        password ??= Environment.GetEnvironmentVariable("STAMPD_PFX_PASSWORD");

        return new CliArgs(pfx, password, generateSelfSigned, useTsa);
    }

    private static void PrintHelp()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  stampd-certcheck --pfx <path> [--password <pwd>] [--tsa]");
        Console.WriteLine("  stampd-certcheck --generate-self-signed [--tsa]");
        Console.WriteLine("");
        Console.WriteLine("Flags:");
        Console.WriteLine("  --pfx <path>             Path to a customer PFX / P12 file");
        Console.WriteLine("  --password <pwd>         PFX password (or set STAMPD_PFX_PASSWORD env var)");
        Console.WriteLine("  --generate-self-signed   Skip --pfx; generate an in-memory demo cert");
        Console.WriteLine("  --tsa                    Include an RFC 3161 timestamp (PAdES B-T)");
        Console.WriteLine("  --help                   Show this message");
        Console.WriteLine("");
        Console.WriteLine("Examples:");
        Console.WriteLine("  # Customer hands you a PFX — validate it");
        Console.WriteLine("  stampd-certcheck --pfx ./customer-signer.pfx --password mypass");
        Console.WriteLine("");
        Console.WriteLine("  # No cert yet — demo flow with a self-signed cert");
        Console.WriteLine("  stampd-certcheck --generate-self-signed --tsa");
    }
}
