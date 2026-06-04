using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Stampd.Core.Entities;
using Stampd.Core.Tenancy;
using Stampd.Infrastructure;

namespace Stampd.WebApi.Services;

/// <summary>
/// Drains the <see cref="WebhookDelivery"/> outbox. Each tick picks deliveries whose
/// <see cref="WebhookDelivery.NextAttemptAtUtc"/> is in the past, POSTs the payload with
/// an <c>X-Stampd-Signature</c> HMAC-SHA256 header, and either deletes the row on success
/// or schedules an exponentially-backed-off retry on failure.
/// </summary>
internal sealed class WebhookDeliveryWorker : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    private const int MaxAttempts = 12; // ~2 days at 2^n seconds backoff capped at 1h

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<WebhookDeliveryWorker> _logger;

    public WebhookDeliveryWorker(
        IServiceScopeFactory scopeFactory,
        IHttpClientFactory httpClientFactory,
        ILogger<WebhookDeliveryWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("WebhookDeliveryWorker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var processed = await DrainBatchAsync(stoppingToken).ConfigureAwait(false);
                if (processed == 0)
                {
                    await Task.Delay(PollInterval, stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "WebhookDeliveryWorker iteration failed.");
                await Task.Delay(PollInterval, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<int> DrainBatchAsync(CancellationToken ct)
    {
        // Cross-tenant scope: webhook delivery is a system concern, not user-bound.
        using (TenantScope.Enter(Guid.Empty))
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<StampdDbContext>();

            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            // Filter and order on the long epoch-ms shadow column so SQLite can translate
            // both WHERE and ORDER BY into a real indexed query. Pre-v1.2 this materialized
            // a capped pool client-side because TEXT-stored DateTimeOffset comparisons
            // aren't reliable across offsets.
            var batch = await db.WebhookDeliveries
                .Include(d => d.WebhookEndpoint)
                .IgnoreQueryFilters()
                .Where(d => d.NextAttemptAtUtcEpochMs <= nowMs)
                .OrderBy(d => d.NextAttemptAtUtcEpochMs)
                .Take(20)
                .ToListAsync(ct)
                .ConfigureAwait(false);

            if (batch.Count == 0)
            {
                return 0;
            }

            using var httpClient = _httpClientFactory.CreateClient(nameof(WebhookDeliveryWorker));
            httpClient.Timeout = RequestTimeout;

            foreach (var delivery in batch)
            {
                if (delivery.WebhookEndpoint is null || !delivery.WebhookEndpoint.IsActive)
                {
                    db.WebhookDeliveries.Remove(delivery);
                    continue;
                }

                await TryDeliverAsync(httpClient, db, delivery, ct).ConfigureAwait(false);
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return batch.Count;
        }
    }

    private async Task TryDeliverAsync(
        HttpClient httpClient,
        StampdDbContext db,
        WebhookDelivery delivery,
        CancellationToken ct)
    {
        var endpoint = delivery.WebhookEndpoint!;
        delivery.AttemptCount++;

        try
        {
            using var content = new StringContent(delivery.PayloadJson, Encoding.UTF8);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint.Url) { Content = content };
            request.Headers.Add("X-Stampd-Event", delivery.EventType.ToString());
            request.Headers.Add("X-Stampd-Delivery-Id", delivery.Id.ToString("N"));
            request.Headers.Add("X-Stampd-Signature", ComputeSignature(endpoint.Secret, delivery.PayloadJson));

            using var response = await httpClient.SendAsync(request, ct).ConfigureAwait(false);
            delivery.LastResponseStatus = (int)response.StatusCode;

            if (response.IsSuccessStatusCode)
            {
                db.WebhookDeliveries.Remove(delivery);
                endpoint.LastDeliveryAttemptAtUtc = DateTimeOffset.UtcNow;
                endpoint.LastSuccessAtUtc = DateTimeOffset.UtcNow;
                endpoint.ConsecutiveFailures = 0;
                return;
            }

            delivery.LastErrorMessage = $"HTTP {(int)response.StatusCode}";
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            delivery.LastErrorMessage = ex.Message;
            delivery.LastResponseStatus = null;
        }

        endpoint.LastDeliveryAttemptAtUtc = DateTimeOffset.UtcNow;
        endpoint.ConsecutiveFailures++;

        if (delivery.AttemptCount >= MaxAttempts)
        {
            _logger.LogWarning(
                "Webhook delivery {DeliveryId} to {Url} gave up after {Attempts} attempts.",
                delivery.Id, endpoint.Url, delivery.AttemptCount);
            db.WebhookDeliveries.Remove(delivery);
            return;
        }

        // Exponential backoff with cap: 2s, 4s, 8s, ..., capped at 1h.
        var backoff = TimeSpan.FromSeconds(Math.Min(3600, Math.Pow(2, delivery.AttemptCount)));
        delivery.NextAttemptAtUtc = DateTimeOffset.UtcNow.Add(backoff);
    }

    internal static string ComputeSignature(string secret, string payload)
    {
        var keyBytes = Encoding.UTF8.GetBytes(secret);
        var payloadBytes = Encoding.UTF8.GetBytes(payload);
        return "sha256=" + Convert.ToHexString(HMACSHA256.HashData(keyBytes, payloadBytes)).ToLowerInvariant();
    }
}
