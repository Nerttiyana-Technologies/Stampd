using System.Net.Http.Headers;
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
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/templates/");
        req.Content = JsonContent.Create(body, options: JsonOptions);
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
        using var req = new HttpRequestMessage(HttpMethod.Put, $"/api/templates/{id}");
        req.Content = JsonContent.Create(body, options: JsonOptions);
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
    /// Lists signing requests for the current tenant with paging + filtering + sort
    /// (v2.0 Slice B; supersedes v1.3 #158's basic paging signature). A null filter
    /// preserves v1.3 behavior: newest-dispatched-first, all statuses, all templates.
    /// </summary>
    public async Task<SigningRequestListPage?> ListSigningRequestsAsync(
        string bearerToken,
        int page,
        int pageSize,
        SigningRequestListFilter? filter,
        CancellationToken ct)
    {
        // Build query string defensively — only emit params the caller actually set,
        // so a default-filter call results in the same compact URL v1.3 produced.
        var queryParams = new List<string>
        {
            $"page={page}",
            $"pageSize={pageSize}",
        };

        if (filter is not null)
        {
            if (filter.TemplateId is not null)
            {
                queryParams.Add($"templateId={filter.TemplateId.Value}");
            }
            if (filter.Status is { Count: > 0 } statuses)
            {
                queryParams.Add($"status={Uri.EscapeDataString(string.Join(',', statuses))}");
            }
            if (!string.IsNullOrWhiteSpace(filter.SenderEmail))
            {
                queryParams.Add($"senderEmail={Uri.EscapeDataString(filter.SenderEmail)}");
            }
            if (!string.IsNullOrWhiteSpace(filter.RecipientEmail))
            {
                queryParams.Add($"recipientEmail={Uri.EscapeDataString(filter.RecipientEmail)}");
            }
            if (filter.DispatchedFrom is not null)
            {
                queryParams.Add($"dispatchedFrom={Uri.EscapeDataString(filter.DispatchedFrom.Value.ToString("o", System.Globalization.CultureInfo.InvariantCulture))}");
            }
            if (filter.DispatchedTo is not null)
            {
                queryParams.Add($"dispatchedTo={Uri.EscapeDataString(filter.DispatchedTo.Value.ToString("o", System.Globalization.CultureInfo.InvariantCulture))}");
            }
            if (!string.IsNullOrWhiteSpace(filter.SortBy))
            {
                queryParams.Add($"sortBy={Uri.EscapeDataString(filter.SortBy)}");
            }
            if (!string.IsNullOrWhiteSpace(filter.Direction))
            {
                queryParams.Add($"direction={Uri.EscapeDataString(filter.Direction)}");
            }
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
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/signing-requests/");
        req.Content = JsonContent.Create(body, options: JsonOptions);
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

    /// <summary>v2.0 Slice A — GET /api/admin/dashboard summary tile counts.</summary>
    public async Task<AdminDashboardSummary?> GetAdminDashboardSummaryAsync(
        string bearerToken,
        CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/admin/dashboard/");
        ApplyBearer(req, bearerToken);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<AdminDashboardSummary>(JsonOptions, ct).ConfigureAwait(false);
    }

    /// <summary>v2.0 Slice A — GET /api/admin/dashboard/trend (default 30 days).</summary>
    public async Task<AdminDashboardTrend?> GetAdminDashboardTrendAsync(
        string bearerToken,
        int days,
        CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"/api/admin/dashboard/trend?days={days}");
        ApplyBearer(req, bearerToken);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<AdminDashboardTrend>(JsonOptions, ct).ConfigureAwait(false);
    }

    /// <summary>v2.0 Slice A — GET /api/admin/dashboard/top-templates.</summary>
    public async Task<AdminTopTemplates?> GetAdminTopTemplatesAsync(
        string bearerToken,
        int take,
        CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"/api/admin/dashboard/top-templates?take={take}");
        ApplyBearer(req, bearerToken);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<AdminTopTemplates>(JsonOptions, ct).ConfigureAwait(false);
    }

    /// <summary>v2.0 Slice D — POST /api/admin/cleanup-demo. Returns null on non-success.</summary>
    public async Task<CleanupDemoResult?> CleanupDemoAsync(string bearerToken, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/admin/cleanup-demo");
        ApplyBearer(req, bearerToken);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<CleanupDemoResult>(JsonOptions, ct).ConfigureAwait(false);
    }

    /// <summary>v2.0 Slice D — POST /api/admin/signing-requests/void with optional reason.</summary>
    public async Task<BulkOperationResult?> BulkVoidAsync(
        string bearerToken,
        IReadOnlyList<Guid> ids,
        string? reason,
        CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/admin/signing-requests/void");
        req.Content = JsonContent.Create(new { ids, reason }, options: JsonOptions);
        ApplyBearer(req, bearerToken);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<BulkOperationResult>(JsonOptions, ct).ConfigureAwait(false);
    }

    /// <summary>v2.0 Slice D — POST /api/admin/signing-requests/resend-invitation.</summary>
    public async Task<BulkOperationResult?> BulkResendAsync(
        string bearerToken,
        IReadOnlyList<Guid> recipientIds,
        CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/admin/signing-requests/resend-invitation");
        req.Content = JsonContent.Create(new { recipientIds }, options: JsonOptions);
        ApplyBearer(req, bearerToken);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<BulkOperationResult>(JsonOptions, ct).ConfigureAwait(false);
    }

    /// <summary>v2.0 Slice C — GET /api/admin/analytics/funnel.</summary>
    public async Task<AdminAnalyticsFunnel?> GetAdminFunnelAsync(string bearerToken, int days, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"/api/admin/analytics/funnel?days={days}");
        ApplyBearer(req, bearerToken);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<AdminAnalyticsFunnel>(JsonOptions, ct).ConfigureAwait(false);
    }

    /// <summary>v2.0 Slice C — GET /api/admin/analytics/time-to-sign.</summary>
    public async Task<AdminTimeToSign?> GetAdminTimeToSignAsync(string bearerToken, int days, int take, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"/api/admin/analytics/time-to-sign?days={days}&take={take}");
        ApplyBearer(req, bearerToken);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<AdminTimeToSign>(JsonOptions, ct).ConfigureAwait(false);
    }

    /// <summary>v2.0 Slice C — GET /api/admin/analytics/identity-verification.</summary>
    public async Task<AdminIdentityVerification?> GetAdminIdentityVerificationAsync(string bearerToken, int days, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"/api/admin/analytics/identity-verification?days={days}");
        ApplyBearer(req, bearerToken);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<AdminIdentityVerification>(JsonOptions, ct).ConfigureAwait(false);
    }

    /// <summary>v2.3 #230 — GET /api/admin/analytics/webhooks-health.</summary>
    public async Task<AdminWebhooksHealth?> GetAdminWebhooksHealthAsync(string bearerToken, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/admin/analytics/webhooks-health");
        ApplyBearer(req, bearerToken);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<AdminWebhooksHealth>(JsonOptions, ct).ConfigureAwait(false);
    }

    /// <summary>v2.3 #231 — GET /api/admin/analytics/by-sender.</summary>
    public async Task<AdminBySender?> GetAdminBySenderAsync(string bearerToken, int days, int take, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"/api/admin/analytics/by-sender?days={days}&take={take}");
        ApplyBearer(req, bearerToken);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<AdminBySender>(JsonOptions, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// v2.3 #232 — composes the absolute URL for the audit CSV export. The browser
    /// hits this URL directly through a download anchor so the CSV streams straight
    /// to disk without round-tripping through Blazor. Filters are encoded as query
    /// params: <paramref name="days"/> becomes <c>from=&lt;now-days&gt;</c>, plus
    /// optional event-type and actor-user-id filters.
    /// </summary>
    public string BuildAuditExportUrl(int? days, string? eventType, string? actorUserId)
    {
        var qs = new List<string>();
        if (days is > 0)
        {
            var from = DateTimeOffset.UtcNow.AddDays(-days.Value);
            qs.Add($"from={Uri.EscapeDataString(from.ToString("O"))}");
        }
        if (!string.IsNullOrWhiteSpace(eventType)) qs.Add($"eventType={Uri.EscapeDataString(eventType)}");
        if (!string.IsNullOrWhiteSpace(actorUserId)) qs.Add($"actorUserId={Uri.EscapeDataString(actorUserId)}");
        var query = qs.Count > 0 ? "?" + string.Join("&", qs) : string.Empty;
        var baseUrl = _http.BaseAddress is null ? string.Empty : _http.BaseAddress.ToString().TrimEnd('/');
        return $"{baseUrl}/api/admin/audit/export{query}";
    }
}
