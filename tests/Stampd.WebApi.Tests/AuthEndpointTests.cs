using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using Xunit;

namespace Stampd.WebApi.Tests;

public sealed class AuthEndpointTests : IClassFixture<StampdWebApplicationFactory>
{
    private readonly StampdWebApplicationFactory _factory;

    public AuthEndpointTests(StampdWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task DevToken_IssuesValidJwt()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/dev-token", new
        {
            subject = "alice@example.com",
            tenantId = "00000000-0000-0000-0000-000000000001",
            roles = new[] { "Admin" },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var token = doc.RootElement.GetProperty("accessToken").GetString();
        Assert.False(string.IsNullOrWhiteSpace(token));

        // JWT shape: three base64url-encoded segments separated by dots.
        var parts = token!.Split('.');
        Assert.Equal(3, parts.Length);
    }

    [Fact]
    public async Task ProtectedEndpoint_WithoutToken_Returns401()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync(new Uri("/api/signing-requests/00000000-0000-0000-0000-000000000000", UriKind.Relative));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ProtectedEndpoint_WithValidToken_DoesNotReturn401()
    {
        var client = _factory.CreateClient();
        var token = await IssueDevTokenAsync(client);

        var request = new HttpRequestMessage(HttpMethod.Get,
            new Uri("/api/signing-requests/00000000-0000-0000-0000-000000000000", UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.SendAsync(request);

        // The request id is a placeholder so we expect 404, not 401 — proving auth passed.
        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task DevToken_RejectsEmptySubject()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/dev-token", new { subject = "" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    internal static async Task<string> IssueDevTokenAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/auth/dev-token", new
        {
            subject = "test@example.com",
            tenantId = "00000000-0000-0000-0000-000000000001",
            roles = new[] { "Admin" },
        });
        response.EnsureSuccessStatusCode();
        var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("accessToken").GetString()!;
    }
}
