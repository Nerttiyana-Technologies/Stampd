using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Stampd.ByoCertDemo.WebApi.Services;

/// <summary>
/// Typed HTTP wrapper around Stampd.WebApi. Everything a customer integration
/// needs in one place: mint a dev JWT, hit /health to verify the API is up,
/// and POST /api/sign to sign a PDF.
///
/// Real customer integrations should NOT use /api/auth/dev-token — that's
/// only registered when ASPNETCORE_ENVIRONMENT=Development. In staging /
/// production the customer's identity stack mints JWTs (OIDC, SAML, whatever
/// they already run) and Stampd.WebApi validates them via the configured
/// JwtBearer authority. The flow + headers shown here are otherwise identical.
/// </summary>
public sealed class StampdWebApiClient
{
    private readonly HttpClient _http;
    private readonly ILogger<StampdWebApiClient> _logger;
    private string? _bearerToken;

    public StampdWebApiClient(HttpClient http, ILogger<StampdWebApiClient> logger)
    {
        _http = http;
        _logger = logger;
    }

    public Uri BaseAddress => _http.BaseAddress ?? new Uri("http://localhost:5070");

    /// <summary>
    /// Hit /health. Returns a friendly status string + raw JSON. If the WebApi
    /// isn't reachable, throws — the caller surfaces the error in the UI.
    /// </summary>
    public async Task<HealthSnapshot> GetHealthAsync(CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync("/health", ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return new HealthSnapshot(
            IsHealthy: resp.IsSuccessStatusCode,
            StatusCode: (int)resp.StatusCode,
            RawBody: body);
    }

    /// <summary>
    /// Dev-only: ask Stampd.WebApi to mint a JWT we can use for subsequent
    /// requests. The request asks for the <c>Sender</c> + <c>Admin</c> roles
    /// because Stampd v2.0 enforces RBAC on /api/sign — a token with no role
    /// claim authenticates but isn't authorised (the WebApi returns 403). In
    /// production this would be replaced by the customer's own identity stack
    /// minting tokens with the appropriate role claims; the rest of this
    /// client works exactly the same.
    /// </summary>
    public async Task<string> EnsureBearerTokenAsync(CancellationToken ct = default)
    {
        if (!string.IsNullOrEmpty(_bearerToken)) return _bearerToken;

        var body = new
        {
            subject = "demo",
            tenantId = "demo-tenant",
            // Sender = can dispatch + sign. Admin = full access (covers the admin
            // dashboard + bulk endpoints too — handy for poking around).
            roles = new[] { "Sender", "Admin" },
        };
        using var resp = await _http.PostAsJsonAsync("/api/auth/dev-token", body, JsonOpts, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var parsed = await resp.Content.ReadFromJsonAsync<DevTokenResponse>(JsonOpts, ct).ConfigureAwait(false)
                     ?? throw new InvalidOperationException("Empty response from /api/auth/dev-token");

        _bearerToken = parsed.AccessToken;
        return _bearerToken;
    }

    /// <summary>
    /// Sign a PDF. Sends the raw PDF + the user's drawn signature image to
    /// /api/sign, returns the signed PDF bytes. This is the call a customer's
    /// own UI would make — same shape, same JSON, same auth header.
    /// </summary>
    /// <remarks>
    /// The PAdES profile (B-B / B-T / B-LT / B-LTA) is NOT a parameter — it's
    /// decided by Stampd.WebApi's configuration (which TSA and revocation
    /// providers are registered). The client just sends the bytes; the server
    /// picks the strongest profile its setup supports.
    /// </remarks>
    public async Task<SignSuccess> SignAsync(
        byte[] pdfBytes,
        byte[] signatureImagePng,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pdfBytes);
        ArgumentNullException.ThrowIfNull(signatureImagePng);

        var token = await EnsureBearerTokenAsync(ct).ConfigureAwait(false);

        // Wire shape matches Stampd.WebApi/Models/SignApiModels.cs exactly:
        //   { sourcePdfBase64, fields[], fieldValues: { "0": { imageBase64 }, ... }, metadata }
        // The dictionary is keyed by INT (field index) but JSON serialises ints
        // as strings here. Each value is an ApiFieldValue object with either
        // Text or ImageBase64 set, NOT a raw base64 string. The TSA / B-T vs
        // B-B choice is server-side configuration; the request has no Profile
        // field — the API picks based on whether a TimestampAuthorityProvider
        // is registered.
        var request = new SignRequest(
            SourcePdfBase64: Convert.ToBase64String(pdfBytes),
            Fields: new[]
            {
                new SignField(
                    PageNumber: 1,
                    Bounds: new PercentageRect(X: 10.0, Y: 75.0, Width: 35.0, Height: 8.0),
                    Kind: "Signature",
                    SignerId: "demo-signer"),
            },
            FieldValues: new Dictionary<int, FieldValuePayload>
            {
                [0] = new FieldValuePayload(ImageBase64: Convert.ToBase64String(signatureImagePng)),
            },
            Metadata: new SignMetadata(
                Reason: "Signed via Stampd ByoCertDemo.WebApi sample",
                Location: "Stampd.ByoCertDemo.WebApi",
                SignerName: "Demo Signer"));

        using var msg = new HttpRequestMessage(HttpMethod.Post, "/api/sign")
        {
            Content = JsonContent.Create(request, options: JsonOpts),
        };
        msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var started = DateTimeOffset.UtcNow;
        using var resp = await _http.SendAsync(msg, ct).ConfigureAwait(false);
        var elapsed = DateTimeOffset.UtcNow - started;

        if (!resp.IsSuccessStatusCode)
        {
            var errBody = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new HttpRequestException(
                $"Stampd.WebApi returned HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}: {Truncate(errBody, 500)}");
        }

        var signed = await resp.Content.ReadFromJsonAsync<SignResponse>(JsonOpts, ct).ConfigureAwait(false)
                     ?? throw new InvalidOperationException("Empty response from /api/sign");

        var signedBytes = Convert.FromBase64String(signed.SignedPdfBase64);
        return new SignSuccess(
            SignedPdfBytes: signedBytes,
            OriginalSizeBytes: pdfBytes.Length,
            SignedSizeBytes: signedBytes.LongLength,
            Sha256Hex: signed.DocumentHashSha256 ?? string.Empty,
            SignedAtUtc: signed.SignedAtUtc ?? DateTimeOffset.UtcNow,
            Profile: "Signed via Stampd.WebApi (profile decided server-side)",
            Duration: elapsed,
            RawRequestPreview: PreviewRequestJson(request),
            EndpointUrl: new Uri(_http.BaseAddress!, "/api/sign").ToString());
    }

    private static string PreviewRequestJson(SignRequest request)
    {
        // Trim the giant base64 fields so the preview reads at a glance during
        // the demo — customers want to see the request shape, not 50 KB of PDF.
        // SignRequest no longer carries Profile (the real API decides that
        // server-side based on which TSA / revocation providers are configured),
        // so the preview reflects exactly what hits the wire.
        var json = JsonSerializer.Serialize(request, JsonOpts);
        json = TrimBase64(json, "sourcePdfBase64", 32);
        json = TrimBase64(json, "imageBase64", 32);
        return json;
    }

    private static string TrimBase64(string json, string key, int keep)
    {
        var marker = $"\"{key}\":\"";
        var i = json.IndexOf(marker, StringComparison.Ordinal);
        if (i < 0) return json;
        var start = i + marker.Length;
        var end = json.IndexOf('"', start);
        if (end < 0 || end - start < keep + 5) return json;

        // CA1845: use AsSpan + string.Concat instead of Substring + '+'. Avoids
        // allocating intermediate slice strings just to throw them away.
        var middle = string.Create(
            CultureInfo.InvariantCulture,
            $"...(truncated {end - start - keep} more chars)");
        return string.Concat(
            json.AsSpan(0, start),
            json.AsSpan(start, keep),
            middle,
            json.AsSpan(end));
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : string.Concat(s.AsSpan(0, max), "…");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // ---- Wire models (match Stampd.WebApi public contracts) ----------------

    private sealed record DevTokenResponse(
        [property: JsonPropertyName("accessToken")] string AccessToken);

    private sealed record SignRequest(
        string SourcePdfBase64,
        SignField[] Fields,
        Dictionary<int, FieldValuePayload> FieldValues,
        SignMetadata Metadata);

    private sealed record SignField(int PageNumber, PercentageRect Bounds, string Kind, string SignerId);
    private sealed record PercentageRect(double X, double Y, double Width, double Height);

    // Mirrors Stampd.WebApi.Models.ApiFieldValue: exactly one of Text /
    // ImageBase64 should be set; the server picks based on the field's Kind.
    private sealed record FieldValuePayload(string? Text = null, string? ImageBase64 = null);

    private sealed record SignMetadata(string Reason, string Location, string SignerName);

    private sealed record SignResponse(
        [property: JsonPropertyName("signedPdfBase64")] string SignedPdfBase64,
        [property: JsonPropertyName("documentHashSha256")] string? DocumentHashSha256,
        [property: JsonPropertyName("signedAtUtc")] DateTimeOffset? SignedAtUtc);
}

/// <summary>What /health told us about the running WebApi.</summary>
public sealed record HealthSnapshot(bool IsHealthy, int StatusCode, string RawBody);

/// <summary>A successful sign roundtrip, plus the bits the demo UI surfaces.</summary>
public sealed record SignSuccess(
    byte[] SignedPdfBytes,
    int OriginalSizeBytes,
    long SignedSizeBytes,
    string Sha256Hex,
    DateTimeOffset SignedAtUtc,
    string Profile,
    TimeSpan Duration,
    string RawRequestPreview,
    string EndpointUrl);
