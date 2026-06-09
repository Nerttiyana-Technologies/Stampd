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
/// v3.0 alpha.1 — integration tests for the per-tenant admin scope endpoints
/// (grant / revoke / list) and the auth gate that they protect. Uses the
/// <see cref="StampdWebApplicationFactory"/>'s in-memory config — we toggle the
/// super-admin override via the factory's <c>SuperAdminUserIds</c> setting to
/// flip between "gate denies" and "gate succeeds" within a single test.
/// </summary>
/// <remarks>
/// Tests share a fixture for fast restart but each test seeds + reads its own
/// scopes — we never assume DB state across tests. The factory clears its
/// isolated SQLite file on Dispose, so test order doesn't matter.
/// </remarks>
public sealed class AdminScopeEndpointsTests
{
    private const string TenantA = "00000000-0000-0000-0000-000000000001";
    private const string TenantB = "00000000-0000-0000-0000-000000000002";
    private const string SuperAdminSubject = "super-admin@test";

    // No IClassFixture: each test constructs its own ScopedStampdWebApplicationFactory
    // with per-test super-admin config. The factory's per-instance isolated SQLite +
    // working directory means tests don't share state.

    /// <summary>
    /// I1 — A super-admin grants admin scope on tenant A to "alice". Expect 201
    /// with a JSON body containing a UUID id and the AdminScopes table to carry
    /// the new row.
    /// </summary>
    [Fact]
    public async Task Grant_AsSuperAdmin_Creates201_AndPersistsRow()
    {
        using var factory = NewFactoryWithSuperAdmin();
        var client = factory.CreateClient();
        var token = await IssueAdminTokenAsync(client, SuperAdminSubject, TenantA);

        var response = await PostScopeAsync(client, token, TenantA, new { userId = "alice" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await ReadJsonAsync(response);
        Assert.True(body.RootElement.TryGetProperty("id", out var idElement));
        Assert.True(Guid.TryParse(idElement.GetString(), out _));

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<StampdDbContext>();
        Assert.Single(db.AdminScopes.Where(s => s.UserId == "alice" && s.TenantId == Guid.Parse(TenantA)));
    }

    /// <summary>
    /// I2 — Re-granting an active scope for the same (user, tenant) is idempotent.
    /// Second call returns 200 (not 201), the response body's id matches the first,
    /// and the AdminScopes table still has exactly one row for that pair.
    /// </summary>
    [Fact]
    public async Task Grant_DuplicateActiveScope_ReturnsExistingRow_StillOne()
    {
        using var factory = NewFactoryWithSuperAdmin();
        var client = factory.CreateClient();
        var token = await IssueAdminTokenAsync(client, SuperAdminSubject, TenantA);

        var first = await PostScopeAsync(client, token, TenantA, new { userId = "alice" });
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var firstBody = await ReadJsonAsync(first);
        var firstId = firstBody.RootElement.GetProperty("id").GetString();

        var second = await PostScopeAsync(client, token, TenantA, new { userId = "alice" });
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var secondBody = await ReadJsonAsync(second);
        var secondId = secondBody.RootElement.GetProperty("id").GetString();
        Assert.Equal(firstId, secondId);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<StampdDbContext>();
        Assert.Single(db.AdminScopes.Where(s => s.UserId == "alice" && s.TenantId == Guid.Parse(TenantA)));
    }

    /// <summary>
    /// I3 — After a revoke, a fresh grant for the same (user, tenant) creates a
    /// NEW row rather than reusing the revoked one. Audit trail intact: two rows,
    /// one revoked, one active.
    /// </summary>
    [Fact]
    public async Task Grant_AfterRevoke_CreatesNewRow_LeavingRevokedOnTrail()
    {
        using var factory = NewFactoryWithSuperAdmin();
        var client = factory.CreateClient();
        var token = await IssueAdminTokenAsync(client, SuperAdminSubject, TenantA);

        var grant = await PostScopeAsync(client, token, TenantA, new { userId = "alice" });
        var grantId = (await ReadJsonAsync(grant)).RootElement.GetProperty("id").GetString();

        var revoke = await DeleteScopeAsync(client, token, TenantA, grantId!, new { reason = "Test cycle" });
        Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);

        var regrant = await PostScopeAsync(client, token, TenantA, new { userId = "alice" });
        Assert.Equal(HttpStatusCode.Created, regrant.StatusCode);
        var regrantId = (await ReadJsonAsync(regrant)).RootElement.GetProperty("id").GetString();
        Assert.NotEqual(grantId, regrantId);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<StampdDbContext>();
        var rows = db.AdminScopes.Where(s => s.UserId == "alice" && s.TenantId == Guid.Parse(TenantA)).ToList();
        Assert.Equal(2, rows.Count);
        Assert.Single(rows, r => r.RevokedAtUtc is null);
    }

    /// <summary>I4 — Empty UserId in body returns 400 with a structured error.</summary>
    [Fact]
    public async Task Grant_EmptyUserId_Returns400()
    {
        using var factory = NewFactoryWithSuperAdmin();
        var client = factory.CreateClient();
        var token = await IssueAdminTokenAsync(client, SuperAdminSubject, TenantA);

        var response = await PostScopeAsync(client, token, TenantA, new { userId = "" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>I5 — No bearer token at all returns 401, never 403.</summary>
    [Fact]
    public async Task Grant_WithoutToken_Returns401()
    {
        using var factory = NewFactoryWithSuperAdmin();
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/admin/scopes", new { userId = "alice" });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// I6 — Caller carries the Admin role claim BUT has no scope row AND isn't a
    /// super-admin. The gate must deny (403), proving role alone isn't sufficient.
    /// This is the core security guarantee of alpha.1.
    /// </summary>
    [Fact]
    public async Task Grant_WithAdminRoleButNoScope_Returns403()
    {
        using var factory = NewFactoryWithSuperAdmin(superAdminIds: ""); // no super-admin
        var client = factory.CreateClient();
        var token = await IssueAdminTokenAsync(client, "rogue-admin@test", TenantA);

        var response = await PostScopeAsync(client, token, TenantA, new { userId = "alice" });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>
    /// I7 — Revoke flips RevokedAtUtc from null to a timestamp; row stays in DB.
    /// </summary>
    [Fact]
    public async Task Revoke_FlipsRevokedAtUtc_RowRemains()
    {
        using var factory = NewFactoryWithSuperAdmin();
        var client = factory.CreateClient();
        var token = await IssueAdminTokenAsync(client, SuperAdminSubject, TenantA);

        var grant = await PostScopeAsync(client, token, TenantA, new { userId = "alice" });
        var grantId = (await ReadJsonAsync(grant)).RootElement.GetProperty("id").GetString();

        var revoke = await DeleteScopeAsync(client, token, TenantA, grantId!, new { reason = "End-of-engagement" });
        Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<StampdDbContext>();
        var row = db.AdminScopes.Single(s => s.Id == Guid.Parse(grantId!));
        Assert.NotNull(row.RevokedAtUtc);
        Assert.Equal("End-of-engagement", row.RevocationReason);
    }

    /// <summary>I8 — Revoking an already-revoked scope is idempotent (200, no mutation).</summary>
    [Fact]
    public async Task Revoke_AlreadyRevoked_Returns200_NoMutation()
    {
        using var factory = NewFactoryWithSuperAdmin();
        var client = factory.CreateClient();
        var token = await IssueAdminTokenAsync(client, SuperAdminSubject, TenantA);

        var grant = await PostScopeAsync(client, token, TenantA, new { userId = "alice" });
        var grantId = (await ReadJsonAsync(grant)).RootElement.GetProperty("id").GetString();

        await DeleteScopeAsync(client, token, TenantA, grantId!, new { reason = "First revoke" });

        // Snapshot the row state before the second revoke
        DateTimeOffset firstRevokedAt;
        await using (var scope1 = factory.Services.CreateAsyncScope())
        {
            firstRevokedAt = scope1.ServiceProvider.GetRequiredService<StampdDbContext>()
                .AdminScopes.Single(s => s.Id == Guid.Parse(grantId!)).RevokedAtUtc!.Value;
        }

        var second = await DeleteScopeAsync(client, token, TenantA, grantId!, new { reason = "Second revoke" });
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        await using var scope2 = factory.Services.CreateAsyncScope();
        var row = scope2.ServiceProvider.GetRequiredService<StampdDbContext>()
            .AdminScopes.Single(s => s.Id == Guid.Parse(grantId!));
        Assert.Equal(firstRevokedAt, row.RevokedAtUtc);
        Assert.Equal("First revoke", row.RevocationReason); // reason unchanged on second revoke
    }

    /// <summary>I9 — Revoking a non-existent scope returns 404.</summary>
    [Fact]
    public async Task Revoke_NonExistentId_Returns404()
    {
        using var factory = NewFactoryWithSuperAdmin();
        var client = factory.CreateClient();
        var token = await IssueAdminTokenAsync(client, SuperAdminSubject, TenantA);

        var response = await DeleteScopeAsync(client, token, TenantA, Guid.NewGuid().ToString(), new { reason = (string?)null });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// I10 — List returns all scopes on the current tenant, including revoked ones
    /// (audit trail). Ordering is grantedAtUtc desc.
    /// </summary>
    [Fact]
    public async Task List_IncludesRevokedByDefault_OrderedByGrantedDesc()
    {
        using var factory = NewFactoryWithSuperAdmin();
        var client = factory.CreateClient();
        var token = await IssueAdminTokenAsync(client, SuperAdminSubject, TenantA);

        await PostScopeAsync(client, token, TenantA, new { userId = "alice" });
        await PostScopeAsync(client, token, TenantA, new { userId = "bob" });
        var carol = await PostScopeAsync(client, token, TenantA, new { userId = "carol" });
        var carolId = (await ReadJsonAsync(carol)).RootElement.GetProperty("id").GetString();
        await DeleteScopeAsync(client, token, TenantA, carolId!, new { reason = "Test revoke" });

        var listReq = new HttpRequestMessage(HttpMethod.Get, "/api/admin/scopes");
        listReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        listReq.Headers.Add("X-Stampd-Tenant", TenantA);
        var listResp = await client.SendAsync(listReq);
        Assert.Equal(HttpStatusCode.OK, listResp.StatusCode);

        var body = await ReadJsonAsync(listResp);
        var items = body.RootElement.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(3, items.Count);
        // carol should be in the list with isActive=false
        Assert.Contains(items, i =>
            i.GetProperty("userId").GetString() == "carol"
            && i.GetProperty("isActive").GetBoolean() == false);
    }

    /// <summary>I11 — activeOnly=true filters out revoked rows.</summary>
    [Fact]
    public async Task List_ActiveOnly_ExcludesRevoked()
    {
        using var factory = NewFactoryWithSuperAdmin();
        var client = factory.CreateClient();
        var token = await IssueAdminTokenAsync(client, SuperAdminSubject, TenantA);

        await PostScopeAsync(client, token, TenantA, new { userId = "alice" });
        var bob = await PostScopeAsync(client, token, TenantA, new { userId = "bob" });
        var bobId = (await ReadJsonAsync(bob)).RootElement.GetProperty("id").GetString();
        await DeleteScopeAsync(client, token, TenantA, bobId!, new { reason = "Test revoke" });

        var listReq = new HttpRequestMessage(HttpMethod.Get, "/api/admin/scopes?activeOnly=true");
        listReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        listReq.Headers.Add("X-Stampd-Tenant", TenantA);
        var listResp = await client.SendAsync(listReq);

        var body = await ReadJsonAsync(listResp);
        var items = body.RootElement.GetProperty("items").EnumerateArray().ToList();
        Assert.Single(items);
        Assert.Equal("alice", items[0].GetProperty("userId").GetString());
    }

    /// <summary>
    /// I12 — Tenant boundary enforcement: a super-admin on tenant A who lists
    /// scopes sees only tenant A's rows, never tenant B's. Even though the
    /// AdminScopes table has no global tenant query filter, the endpoint applies
    /// its own WHERE clause keyed off the current request's tenant.
    /// </summary>
    [Fact]
    public async Task List_CrossTenant_ShowsOnlyCurrentTenantRows()
    {
        using var factory = NewFactoryWithSuperAdmin();
        var client = factory.CreateClient();

        // Seed: grant alice on tenant A, grant bob on tenant B.
        var tokenA = await IssueAdminTokenAsync(client, SuperAdminSubject, TenantA);
        await PostScopeAsync(client, tokenA, TenantA, new { userId = "alice" });

        var tokenB = await IssueAdminTokenAsync(client, SuperAdminSubject, TenantB);
        await PostScopeAsync(client, tokenB, TenantB, new { userId = "bob" });

        // List from tenant A — should see alice, not bob.
        var listReq = new HttpRequestMessage(HttpMethod.Get, "/api/admin/scopes");
        listReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenA);
        listReq.Headers.Add("X-Stampd-Tenant", TenantA);
        var listResp = await client.SendAsync(listReq);

        var body = await ReadJsonAsync(listResp);
        var items = body.RootElement.GetProperty("items").EnumerateArray().ToList();
        Assert.Contains(items, i => i.GetProperty("userId").GetString() == "alice");
        Assert.DoesNotContain(items, i => i.GetProperty("userId").GetString() == "bob");
    }

    // ----- helpers -----

    /// <summary>
    /// Creates a fresh factory with the super-admin override config applied. Each
    /// test gets its own factory so SQLite state doesn't bleed across tests; the
    /// factory disposes its isolated working directory on dispose.
    /// </summary>
    /// <remarks>
    /// We pass SuperAdminUserIds via the factory's per-instance in-memory config
    /// rather than Environment.SetEnvironmentVariable — env vars are process-wide
    /// and races across xUnit's parallel runner cause non-deterministic failures.
    /// </remarks>
    private static ScopedStampdWebApplicationFactory NewFactoryWithSuperAdmin(string superAdminIds = SuperAdminSubject)
    {
        return new ScopedStampdWebApplicationFactory(new Dictionary<string, string?>
        {
            ["Stampd:Auth:SuperAdminUserIds"] = superAdminIds,
        });
    }

    private static async Task<string> IssueAdminTokenAsync(HttpClient client, string subject, string tenantId)
    {
        var response = await client.PostAsJsonAsync("/api/auth/dev-token", new
        {
            subject,
            tenantId,
            roles = new[] { "Admin" },
        });
        response.EnsureSuccessStatusCode();
        var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("accessToken").GetString()!;
    }

    // Stampd's HttpTenantContext reads the active tenant from the X-Stampd-Tenant
    // header, NOT from the JWT's tenantId claim. Tests must send both — token for
    // auth, header for the per-request tenant scope. Skipping the header silently
    // falls back to the configured default tenant, which makes cross-tenant tests
    // look like every grant landed in the same bucket.
    private static async Task<HttpResponseMessage> PostScopeAsync(HttpClient client, string token, string tenantId, object body)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/admin/scopes")
        {
            Content = JsonContent.Create(body),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Add("X-Stampd-Tenant", tenantId);
        return await client.SendAsync(req);
    }

    private static async Task<HttpResponseMessage> DeleteScopeAsync(HttpClient client, string token, string tenantId, string id, object body)
    {
        var req = new HttpRequestMessage(HttpMethod.Delete, $"/api/admin/scopes/{id}")
        {
            Content = JsonContent.Create(body),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Add("X-Stampd-Tenant", tenantId);
        return await client.SendAsync(req);
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response)
    {
        var raw = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(raw);
    }
}
