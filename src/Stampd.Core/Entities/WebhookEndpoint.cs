namespace Stampd.Core.Entities;

/// <summary>
/// A subscriber endpoint that wants to be notified of signing lifecycle events. One row per
/// (tenant, URL) pair. Stampd POSTs a JSON envelope to <see cref="Url"/> when subscribed
/// events fire; signature header is HMAC-SHA256(<see cref="Secret"/>) over the body.
/// </summary>
public sealed class WebhookEndpoint
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }

    public string Url { get; set; } = string.Empty;

    /// <summary>
    /// Shared secret used to compute the HMAC-SHA256 of the payload that's sent on the
    /// <c>X-Stampd-Signature</c> header. Subscribers verify with the same secret.
    /// </summary>
    public string Secret { get; set; } = string.Empty;

    /// <summary>
    /// Comma-separated list of <see cref="WebhookEventType"/> names this endpoint
    /// subscribes to. Empty string means "all events".
    /// </summary>
    public string SubscribedEvents { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? LastDeliveryAttemptAtUtc { get; set; }
    public DateTimeOffset? LastSuccessAtUtc { get; set; }
    public int ConsecutiveFailures { get; set; }

    public IsSubscribed Matcher() => new(SubscribedEvents);

    public readonly struct IsSubscribed
    {
        private readonly string _events;
        public IsSubscribed(string events) => _events = events;

        public bool For(WebhookEventType type)
        {
            if (string.IsNullOrWhiteSpace(_events))
            {
                return true;
            }

            var name = type.ToString();
            return _events.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Any(t => string.Equals(t, name, StringComparison.OrdinalIgnoreCase));
        }
    }
}

public enum WebhookEventType
{
    RecipientInvited = 0,
    RecipientViewed = 1,
    RecipientSigned = 2,
    RecipientDeclined = 3,
    SigningRequestCompleted = 4,
    SigningRequestVoided = 5,
}

/// <summary>
/// One queued delivery attempt. Outbox pattern: the worker picks rows in
/// <c>NextAttemptAtUtc &lt;= now</c> order and POSTs them; success deletes the row,
/// failure schedules a retry with exponential backoff.
/// </summary>
public sealed class WebhookDelivery
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }

    public Guid WebhookEndpointId { get; set; }
    public WebhookEndpoint? WebhookEndpoint { get; set; }

    public WebhookEventType EventType { get; set; }

    /// <summary>JSON payload sent in the request body.</summary>
    public string PayloadJson { get; set; } = string.Empty;

    public int AttemptCount { get; set; }
    public DateTimeOffset NextAttemptAtUtc { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }

    /// <summary>
    /// Unix epoch milliseconds copy of <see cref="NextAttemptAtUtc"/>. The webhook
    /// delivery worker filters AND orders on this column so SQLite can translate the
    /// query to a real WHERE/ORDER BY (TEXT-stored DateTimeOffset comparisons aren't
    /// reliable across offsets). Maintained on insert and on every retry reschedule.
    /// </summary>
    public long NextAttemptAtUtcEpochMs { get; set; }

    public string? LastErrorMessage { get; set; }
    public int? LastResponseStatus { get; set; }
}
