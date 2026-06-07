using System.Security.Cryptography;

using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

using Stampd.Core.Entities;
using Stampd.Infrastructure;

namespace Stampd.WebApi.Endpoints;

internal static class WebhookEndpoints
{
    public static IEndpointRouteBuilder MapWebhooks(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/api/webhooks", ListAsync).WithName("WebhookList");
        app.MapPost("/api/webhooks", CreateAsync).WithName("WebhookCreate");
        app.MapDelete("/api/webhooks/{id:guid}", DeleteAsync).WithName("WebhookDelete");

        return app;
    }

    public sealed record CreateWebhookBody(
        string Url,
        string SubscribedEvents,
        bool IsActive = true);

    public sealed record WebhookEndpointView(
        Guid Id,
        string Url,
        string SubscribedEvents,
        bool IsActive,
        string Secret,
        DateTimeOffset CreatedAtUtc,
        DateTimeOffset? LastSuccessAtUtc,
        int ConsecutiveFailures);

    private static async Task<IResult> ListAsync(
        [FromServices] StampdDbContext db,
        CancellationToken ct)
    {
        var endpoints = await db.WebhookEndpoints
            .OrderBy(w => w.CreatedAtUtc)
            .Select(w => new WebhookEndpointView(
                w.Id, w.Url, w.SubscribedEvents, w.IsActive,
                Secret: "***",
                w.CreatedAtUtc, w.LastSuccessAtUtc, w.ConsecutiveFailures))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return Results.Ok(endpoints);
    }

    private static async Task<IResult> CreateAsync(
        [FromBody] CreateWebhookBody body,
        [FromServices] StampdDbContext db,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body.Url)
            || !Uri.TryCreate(body.Url, UriKind.Absolute, out var parsed)
            || (parsed.Scheme != "http" && parsed.Scheme != "https"))
        {
            return Results.Problem(
                title: "Url must be an absolute http(s) URI.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var secret = GenerateSecret();
        var entry = new WebhookEndpoint
        {
            Url = body.Url,
            Secret = secret,
            SubscribedEvents = body.SubscribedEvents,
            IsActive = body.IsActive,
        };

        db.WebhookEndpoints.Add(entry);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        // Return the secret in the create response only — subsequent List calls mask it.
        return Results.Created($"/api/webhooks/{entry.Id}", new WebhookEndpointView(
            entry.Id, entry.Url, entry.SubscribedEvents, entry.IsActive,
            Secret: entry.Secret,
            entry.CreatedAtUtc, null, 0));
    }

    private static async Task<IResult> DeleteAsync(
        Guid id,
        [FromServices] StampdDbContext db,
        CancellationToken ct)
    {
        var rows = await db.WebhookEndpoints
            .Where(w => w.Id == id)
            .ExecuteDeleteAsync(ct)
            .ConfigureAwait(false);

        return rows == 0 ? Results.NotFound() : Results.NoContent();
    }

    private static string GenerateSecret()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
