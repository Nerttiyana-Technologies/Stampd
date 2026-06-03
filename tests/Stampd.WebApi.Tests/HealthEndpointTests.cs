using System.Net;
using System.Text.Json;

using Xunit;

namespace Stampd.WebApi.Tests;

public sealed class HealthEndpointTests : IClassFixture<StampdWebApplicationFactory>
{
    private readonly StampdWebApplicationFactory _factory;

    public HealthEndpointTests(StampdWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task HealthLive_ReturnsHealthyJson()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync(new Uri("/health/live", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Healthy", doc.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task HealthReady_ReturnsPerCheckBreakdown()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync(new Uri("/health/ready", UriKind.Relative));

        var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var checks = doc.RootElement.GetProperty("checks");

        Assert.True(checks.TryGetProperty("database", out _));
        Assert.True(checks.TryGetProperty("sealing-provider", out _));
        Assert.True(checks.TryGetProperty("timestamp-authority", out _));
    }

    [Fact]
    public async Task HealthEndpoints_DoNotRequireAuth()
    {
        var client = _factory.CreateClient();

        // No Authorization header → still 200/503, never 401.
        var live = await client.GetAsync(new Uri("/health/live", UriKind.Relative));
        var ready = await client.GetAsync(new Uri("/health/ready", UriKind.Relative));

        Assert.NotEqual(HttpStatusCode.Unauthorized, live.StatusCode);
        Assert.NotEqual(HttpStatusCode.Unauthorized, ready.StatusCode);
    }
}
