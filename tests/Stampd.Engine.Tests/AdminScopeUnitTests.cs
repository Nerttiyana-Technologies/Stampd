using Stampd.Core.Entities;

using Xunit;

namespace Stampd.Engine.Tests;

/// <summary>
/// v3.0 alpha.1 — unit-level invariants on the <see cref="AdminScope"/> entity.
/// Pure property tests; no DI, no DB. The interceptor-driven behavior (Id/Granted
/// stamp on insert, ConcurrencyToken rotation) is covered separately in
/// <c>Stampd.WebApi.Tests/AdminScopeEndpointsTests</c> where the live SaveChanges
/// pipeline can run.
/// </summary>
public sealed class AdminScopeUnitTests
{
    [Fact]
    public void DefaultConstructor_IsActive_Returns_True()
    {
        var scope = new AdminScope();
        Assert.True(scope.IsActive);
        Assert.Null(scope.RevokedAtUtc);
    }

    [Fact]
    public void SettingRevokedAtUtc_FlipsIsActiveToFalse()
    {
        var scope = new AdminScope { RevokedAtUtc = DateTimeOffset.UtcNow };
        Assert.False(scope.IsActive);
    }

    [Fact]
    public void ClearingRevokedAtUtc_FlipsIsActiveBackToTrue()
    {
        // Re-granting a previously-revoked scope is done by inserting a NEW row,
        // not by clearing RevokedAtUtc — but the property should still report
        // correctly if any code path mutates it directly.
        var scope = new AdminScope { RevokedAtUtc = DateTimeOffset.UtcNow };
        scope.RevokedAtUtc = null;
        Assert.True(scope.IsActive);
    }

    [Fact]
    public void DefaultConstructor_StringFields_AreEmpty_NotNull()
    {
        // Documenting v2.x convention: non-null defaults so JSON serializers don't
        // emit `null` for these fields in test fixtures or hand-crafted instances.
        var scope = new AdminScope();
        Assert.Equal(string.Empty, scope.UserId);
        Assert.Equal(string.Empty, scope.GrantedByUserId);
        Assert.Null(scope.RevokedByUserId);
        Assert.Null(scope.RevocationReason);
    }
}
