using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using Stampd.Identity.Oidc;

using Xunit;

namespace Stampd.Engine.Tests;

/// <summary>
/// v3.0 alpha.2 — unit-level tests for <see cref="OidcRelayServiceCollectionExtensions.AddStampdOidcRelay"/>'s
/// eager config validation. Exercised here (not in <c>Stampd.WebApi.Tests</c>) so
/// we sidestep the WebApplicationFactory top-level-statement / in-memory-config
/// timing trap that Program.cs already documents on the JwtOptions registration.
/// </summary>
public sealed class OidcRelayRegistrationTests
{
    [Fact]
    public void AddStampdOidcRelay_EmptyAuthority_Throws()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authority"] = "",
                ["Audience"] = "https://api.example.com",
            })
            .Build();

        var services = new ServiceCollection();
        var ex = Assert.Throws<InvalidOperationException>(
            () => services.AddStampdOidcRelay(config));
        Assert.Contains("Authority", ex.Message);
    }

    [Fact]
    public void AddStampdOidcRelay_EmptyAudience_Throws()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authority"] = "https://example.auth0.com/",
                ["Audience"] = "",
            })
            .Build();

        var services = new ServiceCollection();
        var ex = Assert.Throws<InvalidOperationException>(
            () => services.AddStampdOidcRelay(config));
        Assert.Contains("Audience", ex.Message);
    }

    [Fact]
    public void AddStampdOidcRelay_BothPresent_RegistersWithoutThrowing()
    {
        // Sanity: with both required keys present we get past the eager guards.
        // We don't try to resolve the full pipeline here (that would need the
        // JwtBearer authentication services); just proving the registration call
        // is side-effect-free when config is valid.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authority"] = "https://example.auth0.com/",
                ["Audience"] = "https://api.example.com",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddOptions();
        var result = services.AddStampdOidcRelay(config);
        Assert.Same(services, result);
    }
}
