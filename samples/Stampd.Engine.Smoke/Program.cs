using System.Diagnostics;
using System.Text;

using Stampd.Core;
using Stampd.Crypto.LocalCertificate;
using Stampd.Engine;
using Stampd.Engine.Rendering;
using Stampd.Engine.Smoke;
using Stampd.Timestamp.FreeTsa;

// PDFsharp 6.x has no default font resolver outside Windows; register ours up front so
// every subsequent XFont construction can find a backing TTF on this OS.
PlatformFontResolver.Register();

// =============================================================================
//  Stampd engine spike — smoke test
// -----------------------------------------------------------------------------
//  Goal: prove that the Stampd.Engine pipeline can take a PDF, stamp signer
//  collateral at percentage coordinates, and apply an X.509 signature whose
//  output Adobe Acrobat opens cleanly.
//
//  Run:    dotnet run --project samples/Stampd.Engine.Smoke
//  Output: ./out/stampd-spike-signed.pdf
//
//  What "success" looks like:
//   - Console reports each phase completing.
//   - The output PDF opens in Adobe Acrobat / Preview.
//   - Acrobat shows the signature panel with a signature present.
//   - Because we use a self-signed cert, Acrobat will mark identity as
//     "unknown" — that is expected and not a failure of the engine.
// =============================================================================

var outDir = Path.Combine(AppContext.BaseDirectory, "out");
Directory.CreateDirectory(outDir);

var stopwatch = Stopwatch.StartNew();
Log("Stampd Engine Spike");
Log("===================");
Log($"Output directory: {outDir}");
Log(string.Empty);

// ---- 1. Build a synthetic source PDF ----
Log("1/5  Generating sample contract PDF...");
var sourcePdf = SampleAssetFactory.BuildSourcePdf();
var sourcePath = Path.Combine(outDir, "stampd-spike-source.pdf");
await File.WriteAllBytesAsync(sourcePath, sourcePdf);
Log($"     wrote {sourcePdf.Length:N0} bytes  ->  {sourcePath}");

// ---- 2. Build a synthetic signature image ----
Log("2/5  Generating signature image (PNG)...");
var signaturePng = SampleAssetFactory.BuildSignaturePng();
var sigPath = Path.Combine(outDir, "stampd-spike-signature.png");
await File.WriteAllBytesAsync(sigPath, signaturePng);
Log($"     wrote {signaturePng.Length:N0} bytes  ->  {sigPath}");

// ---- 3. Load-or-create the self-signed cert and wrap it in the local sealing provider ----
Log("3/5  Loading or generating self-signed signing certificate...");
var certStorePath = Environment.GetEnvironmentVariable("STAMPD_SPIKE_CERT_PATH")
    ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".stampd",
        "spike-signer.pfx");
var certLoadResult = SelfSignedCertificateFactory.CreateOrLoad(certStorePath);
using var certificate = certLoadResult.Certificate;
var certLocation = certLoadResult.Location;
var sealingProvider = new LocalCertificateSealingProvider(certificate);
Log($"     provider:   {sealingProvider.Name}");
Log($"     subject:    {certificate.Subject}");
Log($"     thumbprint: {certificate.Thumbprint}");
Log($"     valid:      {certificate.NotBefore:u}  ->  {certificate.NotAfter:u}");
Log($"     pkcs12:     {certLocation.Pkcs12Path}");
Log($"     public cer: {certLocation.PublicCertPath}");
if (certLocation.WasCreatedNow)
{
    Log("     >> Fresh cert created. Import the .cer above into Adobe's Trusted Certificates");
    Log("        (Trust -> \"Use this certificate as a trusted root\") to get a full green check.");
}
else
{
    Log("     (reusing existing cert -- trust setup in Adobe persists across runs)");
}

// Configure FreeTSA for PAdES B-T (trusted timestamp embedded as unsigned attribute).
// Pass --no-tsa on the command line to skip the network round-trip and stay at B-B.
var useTsa = !args.Contains("--no-tsa", StringComparer.OrdinalIgnoreCase);
using var tsaHttpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
FreeTsaTimestampAuthorityProvider? timestampAuthority = useTsa
    ? new FreeTsaTimestampAuthorityProvider(tsaHttpClient)
    : null;
Log($"     TSA:        {(timestampAuthority is null ? "(none — PAdES B-B)" : $"{timestampAuthority.Name} (PAdES B-T)")}");

// ---- 4. Build the signature request ----
Log("4/5  Building signature request...");
var fields = new[]
{
    new SignatureField(
        PageNumber: 1,
        Bounds: new PercentageRect(X: 10.0, Y: 80.0, Width: 30.0, Height: 8.0),
        Kind: SignatureFieldKind.Signature,
        SignerId: "signer-1"),

    new SignatureField(
        PageNumber: 1,
        Bounds: new PercentageRect(X: 60.0, Y: 80.0, Width: 25.0, Height: 4.0),
        Kind: SignatureFieldKind.Date,
        SignerId: "signer-1"),
};

var fieldValues = new Dictionary<int, ReadOnlyMemory<byte>>
{
    [0] = signaturePng,
    [1] = Encoding.UTF8.GetBytes(DateTimeOffset.UtcNow.ToString("u")),
};

var request = new SignatureRequest
{
    SourcePdf = sourcePdf,
    Fields = fields,
    FieldValues = fieldValues,
    Sealing = new SealingOptions(),
    Metadata = new SignatureMetadata(
        Reason: "Engine spike — proving the pipeline.",
        Location: "Stampd test harness",
        ContactInfo: "spike@stampd.local",
        SignerName: "Stampd Spike Signer"),
};

// ---- 5. Run the engine ----
Log("5/5  Running PdfSharpStampdEngine.SignAsync...");
var engine = new PdfSharpStampdEngine(sealingProvider, timestampAuthority);
var signed = await engine.SignAsync(request);

var outputPath = Path.Combine(outDir, "stampd-spike-signed.pdf");
await File.WriteAllBytesAsync(outputPath, signed.SignedPdf.ToArray());

stopwatch.Stop();
Log(string.Empty);
Log("Result");
Log("------");
Log($"Signed PDF:      {outputPath}");
Log($"Size:            {signed.SignedPdf.Length:N0} bytes");
Log($"SHA-256:         {signed.DocumentHashSha256}");
Log($"Signed at (UTC): {signed.SignedAtUtc:u}");
Log($"Total time:      {stopwatch.ElapsedMilliseconds} ms");
Log(string.Empty);
Log("Next: open the signed PDF in Adobe Acrobat and confirm the signature panel.");

return 0;

static void Log(string message) => Console.WriteLine(message);
