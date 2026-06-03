using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using Stampd.Core.Entities;
using Stampd.Infrastructure;

namespace Stampd.WebApi.Services;

/// <summary>
/// Inserts <see cref="WebhookDelivery"/> outbox rows for every subscribed
/// <see cref="WebhookEndpoint"/> in the active tenant. The actual HTTP POSTs are performed
/// by <see cref="WebhookDeliveryWorker"/> on a background loop.
/// </summary>
public sealed class WebhookDispatcher
{
    private readonly StampdDbContext _db;

    public WebhookDispatcher(StampdDbContext db)
    {
        _db = db;
    }

    public async Task EnqueueAsync(
        WebhookEventType eventType,
        object payload,
        CancellationToken ct)
    {
        var endpoints = await _db.WebhookEndpoints
            .Where(w => w.IsActive)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var payloadJson = JsonSerializer.Serialize(new
        {
            EventType = eventType.ToString(),
            OccurredAtUtc = DateTimeOffset.UtcNow,
            Data = payload,
        });

        var now = DateTimeOffset.UtcNow;
        foreach (var endpoint in endpoints)
        {
            if (!endpoint.Matcher().For(eventType))
            {
                continue;
            }

            _db.WebhookDeliveries.Add(new WebhookDelivery
            {
                WebhookEndpointId = endpoint.Id,
                EventType = eventType,
                PayloadJson = payloadJson,
                AttemptCount = 0,
                NextAttemptAtUtc = now,
            });
        }

        // Caller is expected to SaveChangesAsync as part of its own unit of work — keeps
        // the outbox insert atomic with the state change that produced it.
    }
}
