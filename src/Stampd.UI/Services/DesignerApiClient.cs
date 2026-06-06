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
/// <remarks>
/// In Development, callers pass <see cref="string.Empty"/> and the
/// <c>DevAuthHttpHandler</c> registered via DI auto-injects a dev-minted JWT on every
/// outbound call. <see cref="ApplyBearer"/> below skips the header entirely when the
/// supplied token is empty, so the handler can take over.
/// </remarks>
public sealed class DesignerApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;

    public DesignerApiClient(HttpClient http)
    {
        ArgumentNullException.ThrowIfNull(http);
        _http = http;
    }

    private static void ApplyBearer(HttpRequestMessage req, string bearerToken)
    {
        // When bearerToken is empty we leave the request header alone — DevAuthHttpHandler
        // takes over in Development and injects a dev-minted JWT. When a real token is
        // supplied (e.g. the DesignerAuthBar in non-Dev mode), attach it as a standard
        // Bearer Authorization header.
        if (!string.IsNullOrEmpty(bearerToken))
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        }
    }

    public async Task<IReadOnlyList<TemplateSummary>?> ListAsync(string bearerToken, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/templates/");
        ApplyBearer(req, bearerToken);

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
        ApplyBearer(req, bearerToken);

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

    /// <summary>Fetches a single template's full detail (name, description, roles, fields).</summary>
    public async Task<TemplateDetail?> GetAsync(string bearerToken, Guid id, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"/api/templates/{id}");
        ApplyBearer(req, bearerToken);

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            return null;
        }

        return await resp.Content
            .ReadFromJsonAsync<TemplateDetail>(JsonOptions, ct)
            .ConfigureAwait(false);
    }

    /// <summary>Fetches the template's source PDF bytes (for canvas rehydration on edit).</summary>
    public async Task<byte[]?> GetPdfAsync(string bearerToken, Guid id, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"/api/templates/{id}/pdf");
        ApplyBearer(req, bearerToken);

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            return null;
        }

        return await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
    }

    public async Task<(bool Success, string? Error)> UpdateAsync(
        string bearerToken,
        Guid id,
        UpdateTemplateRequest body,
        CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Put, $"/api/templates/{id}")
        {
            Content = JsonContent.Create(body, options: JsonOptions),
        };
        ApplyBearer(req, bearerToken);

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (resp.IsSuccessStatusCode)
        {
            return (true, null);
        }

        var error = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return (false, $"HTTP {(int)resp.StatusCode}: {error}");
    }

    /// <summary>
    /// Lists signing requests for the current tenant, newest first, with paging
    /// (v1.3 #158). Used by the <c>/designer/requests</c> page so the sender can browse
    /// in-flight and completed workflows and grab signed PDFs. <paramref name="page"/>
    /// and <paramref name="pageSize"/> map to the API's query params and the server
    /// coerces both to safe bounds — page≥1, pageSize∈[1,200] defaulting to 25.
    /// </summary>
    public async Task<SigningRequestListPage?> ListSigningRequestsAsync(
        string bearerToken,
        Guid? templateId,
        int page,
        int pageSize,
        CancellationToken ct)
    {
        var queryParams = new List<string> { $"page={page}", $"pageSize={pageSize}" };
        if (templateId is not null)
        {
            queryParams.Add($"templateId={templateId.Value}");
        }

        var url = "/api/signing-requests/?" + string.Join('&', queryParams);

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        ApplyBearer(req, bearerToken);

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            return null;
        }

        return await resp.Content
            .ReadFromJsonAsync<SigningRequestListPage>(JsonOptions, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Creates a new signing request from a template + recipient assignment(s). Returns
    /// the workflow-side response including each recipient's clickable AccessUrl.
    /// </summary>
    public async Task<(bool Success, SigningRequestResponse? Body, string? Error)>
        CreateSigningRequestAsync(
            string bearerToken,
            CreateSigningRequestBody body,
            CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/signing-requests/")
        {
            Content = JsonContent.Create(body, options: JsonOptions),
        };
        ApplyBearer(req, bearerToken);

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            var error = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return (false, null, $"HTTP {(int)resp.StatusCode}: {error}");
        }

        var payload = await resp.Content
            .ReadFromJsonAsync<SigningRequestResponse>(JsonOptions, ct)
            .ConfigureAwait(false);
        return (true, payload, null);
    }

    /// <summary>
    /// Fetches a single signing request with all its recipients (v1.3 #135 — sender
    /// detail page). Returns null on 404 or any non-success status.
    /// </summary>
    public async Task<SigningRequestDetail?> GetSigningRequestAsync(
        string bearerToken,
        Guid id,
        CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"/api/signing-requests/{id}");
        ApplyBearer(req, bearerToken);

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return null;

        return await resp.Content
            .ReadFromJsonAsync<SigningRequestDetail>(JsonOptions, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Fetches the audit trail for a signing request, oldest-first (v1.3 #135). The
    /// detail page renders this as a timeline alongside the recipients table.
    /// </summary>
    public async Task<SigningRequestAuditPage?> GetSigningRequestAuditAsync(
        string bearerToken,
        Guid id,
        CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"/api/signing-requests/{id}/audit");
        ApplyBearer(req, bearerToken);

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return null;

        return await resp.Content
            .ReadFromJsonAsync<SigningRequestAuditPage>(JsonOptions, ct)
            .ConfigureAwait(false);
    }
}
