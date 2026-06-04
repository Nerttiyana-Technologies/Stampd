using System.Net.Http.Json;
using System.Text.Json;

using Stampd.UI.Models;

namespace Stampd.UI.Services;

/// <summary>
/// Talks to the Stampd WebApi recipient endpoints. All routes here are anonymous (the
/// per-recipient access token in the URL is the auth) — no Bearer header is attached.
/// </summary>
public sealed class StampdApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;

    public StampdApiClient(HttpClient http)
    {
        ArgumentNullException.ThrowIfNull(http);
        _http = http;
    }

    /// <summary>Loads the signing view (fires RecipientViewed in the workflow).</summary>
    public async Task<RecipientSigningView?> GetSigningViewAsync(string accessToken, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);

        using var response = await _http.GetAsync($"/api/sign/{accessToken}", ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        return await response.Content
            .ReadFromJsonAsync<RecipientSigningView>(JsonOptions, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Fetches the source PDF bytes for the signer to read. Returns null if the link is
    /// invalid or the recipient is in a terminal state where the document is no longer
    /// available via this endpoint.
    /// </summary>
    public async Task<byte[]?> GetSourceDocumentAsync(string accessToken, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);

        using var response = await _http.GetAsync($"/api/sign/{accessToken}/document", ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        return await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Submits the signer's field values; on success the document is finalized.</summary>
    public async Task<RecipientSubmitResponse?> SubmitAsync(
        string accessToken,
        IReadOnlyDictionary<int, ApiFieldValue> fieldValues,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);

        using var response = await _http.PostAsJsonAsync(
            $"/api/sign/{accessToken}",
            new SubmitRecipientSignatureRequest(fieldValues),
            JsonOptions,
            ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        return await response.Content
            .ReadFromJsonAsync<RecipientSubmitResponse>(JsonOptions, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Requests an identity-verification challenge be sent (Email OTP). Returns the
    /// verification id + expiry the signer needs to echo back via VerifyIdentityAsync.
    /// </summary>
    public async Task<(bool Success, InitiateVerificationResponse? Body, string? Error)>
        InitiateVerificationAsync(string accessToken, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);

        using var response = await _http.PostAsync(
            $"/api/sign/{accessToken}/initiate-verification",
            content: null,
            ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return (false, null, $"HTTP {(int)response.StatusCode}: {err}");
        }

        var body = await response.Content
            .ReadFromJsonAsync<InitiateVerificationResponse>(JsonOptions, ct)
            .ConfigureAwait(false);
        return (true, body, null);
    }

    /// <summary>Submits the OTP code; on success the recipient is marked identity-verified.</summary>
    public async Task<VerifyIdentityResponse?> VerifyIdentityAsync(
        string accessToken,
        string verificationId,
        string code,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);

        using var response = await _http.PostAsJsonAsync(
            $"/api/sign/{accessToken}/verify-identity",
            new VerifyIdentityRequest(verificationId, code),
            JsonOptions,
            ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        return await response.Content
            .ReadFromJsonAsync<VerifyIdentityResponse>(JsonOptions, ct)
            .ConfigureAwait(false);
    }
}
