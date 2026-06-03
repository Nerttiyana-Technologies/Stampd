using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using Stampd.UI.Models;

namespace Stampd.UI.Services;

/// <summary>
/// Talks to the authenticated <c>/api/templates</c> endpoints. The bearer token is supplied
/// per-call because the JWT lives in the browser's sessionStorage and is passed in via
/// JSInterop from the page — we don't persist it server-side.
/// </summary>
public sealed class DesignerApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;

    public DesignerApiClient(HttpClient http)
    {
        ArgumentNullException.ThrowIfNull(http);
        _http = http;
    }

    public async Task<IReadOnlyList<TemplateSummary>?> ListAsync(string bearerToken, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/templates/");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            return null;
        }

        return await resp.Content
            .ReadFromJsonAsync<List<TemplateSummary>>(JsonOptions, ct)
            .ConfigureAwait(false);
    }

    public async Task<(bool Success, Guid? Id, string? Error)> CreateAsync(
        string bearerToken,
        CreateTemplateRequest body,
        CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/templates/")
        {
            Content = JsonContent.Create(body, options: JsonOptions),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
        {
            var error = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return (false, null, $"HTTP {(int)resp.StatusCode}: {error}");
        }

        var doc = await resp.Content
            .ReadFromJsonAsync<JsonElement>(JsonOptions, ct)
            .ConfigureAwait(false);
        var id = doc.TryGetProperty("id", out var idProp) ? idProp.GetGuid() : Guid.Empty;
        return (true, id, null);
    }
}
