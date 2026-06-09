using System.Security.Claims;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using Stampd.Identity.Saml2;

using Xunit;

namespace Stampd.Engine.Tests;

/// <summary>
/// v3.0 alpha.3 — unit tests for Saml2 SP scaffold. Eager config validation
/// guards + claim mapper. Same pattern as <see cref="OidcRelayRegistrationTests"/>;
/// proves the surface without needing a live IdP.
/// </summary>
public sealed class Saml2RegistrationTests
{
    [Fact]
    public void AddStampdSaml2Sp_EmptySpEntityId_Throws()
    {
        var config = Build(spEntityId: "", idpEntityId: "x", idpMetadataUrl: "y");
        var ex = Assert.Throws<InvalidOperationException>(
            () => new ServiceCollection().AddStampdSaml2Sp(config));
        Assert.Contains("SpEntityId", ex.Message);
    }

    [Fact]
    public void AddStampdSaml2Sp_EmptyIdpEntityId_Throws()
    {
        var config = Build(spEntityId: "x", idpEntityId: "", idpMetadataUrl: "y");
        var ex = Assert.Throws<InvalidOperationException>(
            () => new ServiceCollection().AddStampdSaml2Sp(config));
        Assert.Contains("IdpEntityId", ex.Message);
    }

    [Fact]
    public void AddStampdSaml2Sp_EmptyIdpMetadataUrl_Throws()
    {
        var config = Build(spEntityId: "x", idpEntityId: "y", idpMetadataUrl: "");
        var ex = Assert.Throws<InvalidOperationException>(
            () => new ServiceCollection().AddStampdSaml2Sp(config));
        Assert.Contains("IdpMetadataUrl", ex.Message);
    }

    [Fact]
    public void ClaimMapper_TranslatesAndDrops()
    {
        var identity = new ClaimsIdentity(authenticationType: "saml");
        identity.AddClaim(new Claim("http://schemas.microsoft.com/ws/2008/06/identity/claims/role", "stampd-admins"));
        identity.AddClaim(new Claim("http://schemas.microsoft.com/ws/2008/06/identity/claims/role", "ent-unknown"));
        var principal = new ClaimsPrincipal(identity);
        var mappings = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["stampd-admins"] = "Admin",
        };

        var result = Saml2ClaimMapper.MapRoles(
            principal,
            roleClaim: "http://schemas.microsoft.com/ws/2008/06/identity/claims/role",
            mappings);

        Assert.Single(result);
        Assert.Contains("Admin", result);
    }

    private static IConfiguration Build(string spEntityId, string idpEntityId, string idpMetadataUrl) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SpEntityId"] = spEntityId,
                ["IdpEntityId"] = idpEntityId,
                ["IdpMetadataUrl"] = idpMetadataUrl,
            })
            .Build();
}
