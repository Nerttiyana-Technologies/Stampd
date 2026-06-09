using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;

using Stampd.Core.Entities;
using Stampd.Infrastructure;

using Xunit;

namespace Stampd.WebApi.Tests;

/// <summary>
/// v3.0 alpha.1 — exercises the auth gate behavior at the HTTP layer instead of
/// instantiating <c>AdminScopeAuthorizationHandler</c> directly. The handler
/// depends on <c>HttpCurrentActorContext</c>, <c>ITenantContext</c>, and the live
/// DI scope, so a real HTTP request through the WebApplicationFactory is the
/// honest test surface. Each test pokes a known admin-policy endpoint and
/// inspects the status code.
/// </summary>
/// <remarks>
/// We use <c>GET /api/admin/dashboard</c> as the canonical gate target — it's the
/// simplest admin endpoint with no side effects, so we can hit it repeatedly.
/// </remarks>
public sealed class AdminScopeAuthorizationHandlerTests
{
    private const string TenantA = "00000000-0000-0000-0000-000000000001";
    private const string TenantB = "00000000-0000-0000-0000-000000000002";

    // No IClassFixture: each test builds its own ScopedStampdWebApplicationFactory
    // with the super-admin config it needs. Per-test SQLite isolation comes for free
    // via the base factory's per-instance temp directory.

    /// <summary>A1 — Anonymous request bounces with 401, never reaching the handler.</summary>
    [Fact]
    public async Task A1_Anonymous_Returns401()
    {
        using var factory = NewFactory();
        var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri("/api/admin/dashboard", UriKind.Relative));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// A2 — Authenticated caller without the Admin role claim. Gate 1 fails;
    /// we expect 403 (not 401) — auth ran, requirement denied.
    /// </summary>
    [Fact]
    public async Task A2_AuthenticatedWithoutAdminRole_Returns403()
    {
        using var factory = NewFactory();
        var client = factory.CreateClient();

        var token = await IssueTokenAsync(client, "alice@test", TenantA, roles: new[] { "Sender" });
        var response = await GetWithBearerAsync(client, "/api/admin/dashboard", token, TenantA);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>
    /// A3 — Admin role claim present BUT no AdminScope row + not in super-admin
    /// list. Gate 2 fails; expect 403.
    /// </summary>
    [Fact]
    public async Task A3_AdminRoleNoScopeNoSuperAdmin_Returns403()
    {
        using var factory = NewFactory();
        var client = factory.CreateClient();

        var token = await IssueTokenAsync(client, "alice@test", TenantA, roles: new[] { "Admin" });
        var response = await GetWithBearerAsync(client, "/api/admin/dashboard", token, TenantA);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>
    /// A4 — Admin role + matching AdminScope row. Both gates pass; expect 200.
    /// We seed the scope row directly via DI so the test doesn't depend on the
    /// grant endpoint also working.
    /// </summary>
    [Fact]
    public async Task A4_AdminRoleWithScope_Returns200()
    {
        using var factory = NewFactory();
        await SeedScopeAsync(factory, userId: "alice@test", tenantId: Guid.Parse(TenantA));

        var client = factory.CreateClient();
        var token = await IssueTokenAsync(client, "alice@test", TenantA, roles: new[] { "Admin" });
        var response = await GetWithBearerAsync(client, "/api/admin/dashboard", token, TenantA);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// A5 — Super-admin override bypasses gate 2 even with no AdminScope row.
    /// Expect 200 with no DB seed.
    /// </summary>
    [Fact]
    public async Task A5_AdminRoleAsSuperAdmin_Returns200()
    {
        using var factory = NewFactory(superAdminIds: "alice@test");
        var client = factory.CreateClient();

        var token = await IssueTokenAsync(client, "alice@test", TenantA, roles: new[] { "Admin" });
        var response = await GetWithBearerAsync(client, "/api/admin/dashboard", token, TenantA);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// A6 — Admin holds scope on tenant A but the request lands in tenant B's
    /// context. Tenant boundary enforced; expect 403.
    /// </summary>
    [Fact]
    public async Task A6_ScopeOnOtherTenant_Returns403()
    {
        using var factory = NewFactory();

        // Seed: alice has scope on tenant A only.
        await SeedScopeAsync(factory, userId: "alice@test", tenantId: Guid.Parse(TenantA));

        var client = factory.CreateClient();
        // Request token for tenant B — alice has no scope here. Send the matching
        // TenantB header so the request's tenant context resolves to B, not A.
        var token = await IssueTokenAsync(client, "alice@test", TenantB, roles: new[] { "Admin" });
        var response = await GetWithBearerAsync(client, "/api/admin/dashboard", token, TenantB);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>
    /// Build a per-test factory with optional super-admin override. Default empty
    /// = no super-admin. Pass an explicit value (e.g. "alice@test") to test the
    /// super-admin bypass path.
    /// </summary>
    private static ScopedStampdWebApplicationFactory NewFactory(string superAdminIds = "")
        => new(new Dictionary<string, string?>
        {
            ["Stampd:Auth:SuperAdminUserIds"] = superAdminIds,
        });

    // ----- helpers -----

    private static async Task<string> IssueTokenAsync(HttpClient client, string subject, string tenantId, string[] roles)
    {
        var response = await client.PostAsJsonAsync("/api/auth/dev-token", new { subject, tenantId, roles });
        response.EnsureSuccessStatusCode();
        var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("accessToken").GetString()!;
    }

    // Note: Stampd's tenant context reads from X-Stampd-Tenant (header), NOT the
    // JWT's tenantId claim. Every request must carry BOTH the bearer token (for
    // authentication) and the tenant header (for scope routing). Otherwise the
    // request falls back to the configured default tenant.
    private static async Task<HttpResponseMessage> GetWithBearerAsync(HttpClient client, string path, string token, string tenantId)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Add("X-Stampd-Tenant", tenantId);
        return await client.SendAsync(req);
    }

    /// <summary>
    /// Seeds an AdminScope row directly via DI. Bypasses the grant endpoint so
    /// auth-handler tests stay decoupled from endpoint behavior.
    /// </summary>
    private static async Task SeedScopeAsync(ScopedStampdWebApplicationFactory factory, string userId, Guid tenantId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<StampdDbContext>();
        db.AdminScopes.Add(new AdminScope
        {
            UserId = userId,
            TenantId = tenantId,
            GrantedByUserId = "test-seed",
        });
        await db.SaveChangesAsync();
    }
}
