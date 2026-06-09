using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Stampd.Identity.Saml2;

/// <summary>
/// v3.0 alpha.3 — DI registration scaffold for the SAML2 Service Provider.
/// Eager config validation lands now; full assertion-validation + ACS handler
/// wiring (ITfoxtec.Identity.Saml2 or equivalent) lands in v3.0.0 stable.
/// </summary>
public static class Saml2ServiceCollectionExtensions
{
    /// <summary>
    /// Validate the SAML2 config eagerly and register the options binding.
    /// Throws at registration time if required keys are missing — same pattern
    /// as <c>AddStampdOidcRelay</c>.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Required config (<c>SpEntityId</c>, <c>IdpEntityId</c>, <c>IdpMetadataUrl</c>)
    /// missing.
    /// </exception>
    public static IServiceCollection AddStampdSaml2Sp(
        this IServiceCollection services,
        IConfiguration saml2Section)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(saml2Section);

        if (string.IsNullOrWhiteSpace(saml2Section["SpEntityId"]))
        {
            throw new InvalidOperationException(
                "Stampd:Auth:Saml2:SpEntityId is required when Stampd:Auth:Mode=Saml2. " +
                "Set it to the public URL of your Stampd WebApi (e.g. https://api.stampd.example.com).");
        }
        if (string.IsNullOrWhiteSpace(saml2Section["IdpEntityId"]))
        {
            throw new InvalidOperationException(
                "Stampd:Auth:Saml2:IdpEntityId is required when Stampd:Auth:Mode=Saml2. " +
                "Set it to the entity ID published in your IdP's metadata document.");
        }
        if (string.IsNullOrWhiteSpace(saml2Section["IdpMetadataUrl"]))
        {
            throw new InvalidOperationException(
                "Stampd:Auth:Saml2:IdpMetadataUrl is required when Stampd:Auth:Mode=Saml2. " +
                "Set it to the URL of your IdP's published metadata XML document.");
        }

        services.Configure<Saml2RelayOptions>(saml2Section);
        return services;
    }
}
