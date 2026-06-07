using System.Text.Json;
using System.Text.Json.Serialization;

namespace Stampd.UI.Services;

/// <summary>
/// Holds a long-lived JWT for the UI to talk to the WebApi as a "designer-user". Mints
/// the token via <c>POST /api/auth/dev-token</c> on first call and caches it for the
/// remainder of the token lifetime; the UI is single-tenant in Development so a single
/// token works for every authenticated outbound call.
/// </summary>
/// <remarks>
/// <para>
/// Registered only when <c>builder.Environment.IsDevelopment()</c>. In any non-Development
/// environment the auth bar remains in place and the user supplies their own token, so
/// this provider is not in the DI container at all.
/// </para>
/// <para>
/// Concurrency: a <see cref="SemaphoreSlim"/> serializes the mint call so a burst of
/// requests on startup only triggers one round-trip to <c>/api/auth/dev-token</c>.
/// </para>
/// </remarks>
public sealed class DevTokenProvider : IDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory _httpFactory;
    private readonly DevTokenOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private string? _cachedToken;
    private DateTimeOffset _expiresAtUtc = DateTimeOffset.MinValue;

    public DevTokenProvider(IHttpClientFactory httpFactory, DevTokenOptions options)
    {
        ArgumentNullException.ThrowIfNull(httpFactory);
        ArgumentNullException.ThrowIfNull(options);
        _httpFactory = httpFactory;
        _options = options;
    }

    /// <summary>
    /// Returns a valid bearer token, minting a new one if the cache is empty or the
    /// existing token expires within the next minute.
    /// </summary>
    public async Task<string> GetTokenAsync(CancellationToken ct)
    {
        if (_cachedToken is not null && _expiresAtUtc - TimeSpan.FromMinutes(1) > DateTimeOffset.UtcNow)
        {
            return _cachedToken;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Re-check under the lock — another caller may have refreshed while we waited.
            if (_cachedToken is not null && _expiresAtUtc - TimeSpan.FromMinutes(1) > DateTimeOffset.UtcNow)
            {
                return _cachedToken;
            }

            using var client = _httpFactory.CreateClient(nameof(DevTokenProvider));
            client.BaseAddress = new Uri(_options.ApiBaseUrl);

            var body = new DevTokenRequest(_options.Subject, _options.TenantId, _options.Roles);
            using var response = await client.PostAsJsonAsync(
                "/api/auth/dev-token", body, JsonOpts, ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var detail = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                throw new InvalidOperationException(
                    $"DevTokenProvider failed to mint a token: HTTP {(int)response.StatusCode} {detail}");
            }

            var payload = await response.Content
                .ReadFromJsonAsync<DevTokenResponse>(JsonOpts, ct)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException("DevTokenProvider got an empty response body.");

            _cachedToken = payload.AccessToken;
            _expiresAtUtc = payload.ExpiresAtUtc;
            return _cachedToken;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    private sealed record DevTokenRequest(
        [property: JsonPropertyName("subject")] string Subject,
        [property: JsonPropertyName("tenantId")] string? TenantId,
        [property: JsonPropertyName("roles")] IReadOnlyList<string>? Roles);

    private sealed record DevTokenResponse(
        [property: JsonPropertyName("accessToken")] string AccessToken,
        [property: JsonPropertyName("tokenType")] string TokenType,
        [property: JsonPropertyName("expiresAtUtc")] DateTimeOffset ExpiresAtUtc);
}

/// <summary>Options consumed by <see cref="DevTokenProvider"/>.</summary>
public sealed class DevTokenOptions
{
    public string ApiBaseUrl { get; init; } = "http://localhost:5070";
    public string Subject { get; init; } = "designer-user";
    public string? TenantId { get; init; }
    public IReadOnlyList<string>? Roles { get; init; }
}
