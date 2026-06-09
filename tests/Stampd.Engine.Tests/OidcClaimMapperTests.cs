using System.Security.Claims;

using Stampd.Identity.Oidc;

using Xunit;

namespace Stampd.Engine.Tests;

/// <summary>
/// v3.0 alpha.2 — pure-function tests for the OIDC claim → Stampd-role translation
/// and tenant-id extraction. Keep the unit count tight: 4 cases that exercise the
/// happy path, the silent-drop policy, the multi-role distinct guarantee, and the
/// tenant-id GUID guard. If we add more behavior later we'll add more cases.
/// </summary>
public sealed class OidcClaimMapperTests
{
    private static ClaimsPrincipal PrincipalWith(params (string Type, string Value)[] claims)
    {
        var identity = new ClaimsIdentity(authenticationType: "test");
        foreach (var (type, value) in claims)
        {
            identity.AddClaim(new Claim(type, value));
        }
        return new ClaimsPrincipal(identity);
    }

    [Fact]
    public void MapRoles_TranslatesConfiguredGroupNames_ToStampdRoles()
    {
        var principal = PrincipalWith(
            ("groups", "stampd-admins"),
            ("groups", "stampd-senders"));
        var mappings = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["stampd-admins"] = "Admin",
            ["stampd-senders"] = "Sender",
        };

        var result = OidcClaimMapper.MapRoles(principal, roleClaim: "groups", mappings);

        Assert.Equal(2, result.Count);
        Assert.Contains("Admin", result);
        Assert.Contains("Sender", result);
    }

    [Fact]
    public void MapRoles_DropsUnmappedGroups_Silently()
    {
        // Unknown groups are dropped without error — adopters must opt every group
        // into the role taxonomy explicitly. "stampd-admins" gets through; the
        // other two have no entry in the mappings dictionary.
        var principal = PrincipalWith(
            ("groups", "stampd-admins"),
            ("groups", "some-other-group"),
            ("groups", "azure-ad-users"));
        var mappings = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["stampd-admins"] = "Admin",
        };

        var result = OidcClaimMapper.MapRoles(principal, roleClaim: "groups", mappings);

        Assert.Single(result);
        Assert.Contains("Admin", result);
    }

    [Fact]
    public void MapRoles_DuplicateMappingsTargets_AreDeduplicated()
    {
        // Two external groups both mapping to "Admin" must produce ONE Admin role.
        // Otherwise downstream IsInRole / HasClaim queries see noise.
        var principal = PrincipalWith(
            ("groups", "ent-admins"),
            ("groups", "stampd-admins"));
        var mappings = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ent-admins"] = "Admin",
            ["stampd-admins"] = "Admin",
        };

        var result = OidcClaimMapper.MapRoles(principal, roleClaim: "groups", mappings);

        Assert.Single(result);
        Assert.Contains("Admin", result);
    }

    [Fact]
    public void TryGetTenantId_ReturnsParsedGuid_WhenClaimIsValidGuid()
    {
        var tenant = Guid.NewGuid();
        var principal = PrincipalWith(("tenant_id", tenant.ToString()));

        var result = OidcClaimMapper.TryGetTenantId(principal, tenantClaim: "tenant_id");

        Assert.Equal(tenant, result);
    }

    [Fact]
    public void TryGetTenantId_ReturnsNull_WhenClaimIsAbsent()
    {
        var principal = PrincipalWith(("sub", "alice"));
        Assert.Null(OidcClaimMapper.TryGetTenantId(principal, tenantClaim: "tenant_id"));
    }

    [Fact]
    public void TryGetTenantId_ReturnsNull_WhenClaimIsMalformed()
    {
        // "not-a-guid" is a non-throwing return-null path — the auth handler
        // decides whether to fail the request via RequireTenantClaim.
        var principal = PrincipalWith(("tenant_id", "not-a-guid"));
        Assert.Null(OidcClaimMapper.TryGetTenantId(principal, tenantClaim: "tenant_id"));
    }
}
