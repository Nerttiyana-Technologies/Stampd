using System.Security.Claims;

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Stampd.Identity.Oidc;

/// <summary>
/// v3.0 alpha.2 — DI registration for the OIDC relay. Wires
/// <see cref="JwtBearerOptions"/> to validate tokens against the configured
/// external IdP and applies role-claim mapping at token-validated time so the
/// downstream <c>ClaimsPrincipal</c> carries Stampd's role taxonomy directly.
/// </summary>
public static class OidcRelayServiceCollectionExtensions
{
    /// <summary>
    /// Register the OIDC relay against the existing <see cref="JwtBearerDefaults.AuthenticationScheme"/>
    /// pipeline. Caller must have already invoked <c>AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer()</c>
    /// — Stampd.WebApi's Program.cs does that unconditionally, so this is a pure
    /// configuration layer on top.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown at startup if <see cref="OidcRelayOptions.Authority"/> or
    /// <see cref="OidcRelayOptions.Audience"/> is missing. Better to fail loudly
    /// than to silently accept any token.
    /// </exception>
    public static IServiceCollection AddStampdOidcRelay(
        this IServiceCollection services,
        IConfiguration oidcSection)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(oidcSection);

        // Validate eagerly at registration time so misconfig fails at host build
        // (Program.cs top-level statements) instead of as a JwtBearer middleware
        // exception that ASP.NET swallows into a 500 / 401. The IOptionsMonitor
        // callback below also re-validates in case adopters mutate config at
        // runtime via Configure<>.
        if (string.IsNullOrWhiteSpace(oidcSection["Authority"]))
        {
            throw new InvalidOperationException(
                "Stampd:Auth:Oidc:Authority is required when Stampd:Auth:Mode=Oidc. " +
                "Set it to your IdP's issuer URL (e.g. https://example.auth0.com/).");
        }
        if (string.IsNullOrWhiteSpace(oidcSection["Audience"]))
        {
            throw new InvalidOperationException(
                "Stampd:Auth:Oidc:Audience is required when Stampd:Auth:Mode=Oidc. " +
                "Set it to the API identifier your IdP issues tokens for.");
        }

        services.Configure<OidcRelayOptions>(oidcSection);

        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptionsMonitor<OidcRelayOptions>>((bearer, oidcMonitor) =>
            {
                var oidc = oidcMonitor.CurrentValue;
                if (string.IsNullOrWhiteSpace(oidc.Authority))
                {
                    throw new InvalidOperationException(
                        "Stampd:Auth:Oidc:Authority is required when Stampd:Auth:Mode=Oidc. " +
                        "Set it to your IdP's issuer URL (e.g. https://example.auth0.com/).");
                }
                if (string.IsNullOrWhiteSpace(oidc.Audience))
                {
                    throw new InvalidOperationException(
                        "Stampd:Auth:Oidc:Audience is required when Stampd:Auth:Mode=Oidc. " +
                        "Set it to the API identifier your IdP issues tokens for.");
                }

                bearer.Authority = oidc.Authority;
                bearer.Audience = oidc.Audience;
                // JwtBearer auto-fetches {Authority}/.well-known/openid-configuration
                // and the JWKS from the resulting jwks_uri. We don't override
                // ConfigurationManager — the defaults handle key rotation, caching,
                // and refresh-on-validation-failure.
                bearer.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = oidc.Authority,
                    ValidateAudience = true,
                    ValidAudience = oidc.Audience,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    ClockSkew = TimeSpan.FromMinutes(2),
                    NameClaimType = ClaimTypes.NameIdentifier,
                    RoleClaimType = ClaimTypes.Role,
                };

                // Apply Stampd's role mapping at token-validated time. Adds new
                // ClaimTypes.Role claims to the principal so the downstream
                // RequireRole / IsInRole checks see the translated values directly.
                bearer.Events ??= new JwtBearerEvents();
                var existingValidated = bearer.Events.OnTokenValidated;
                bearer.Events.OnTokenValidated = async ctx =>
                {
                    if (existingValidated is not null)
                    {
                        await existingValidated(ctx).ConfigureAwait(false);
                    }

                    if (ctx.Principal is null) return;
                    var stampdRoles = OidcClaimMapper.MapRoles(
                        ctx.Principal, oidc.RoleClaim, oidc.RoleClaimMappings);
                    if (stampdRoles.Count > 0 && ctx.Principal.Identity is ClaimsIdentity identity)
                    {
                        foreach (var role in stampdRoles)
                        {
                            identity.AddClaim(new Claim(ClaimTypes.Role, role));
                        }
                    }

                    if (oidc.RequireTenantClaim
                        && OidcClaimMapper.TryGetTenantId(ctx.Principal, oidc.TenantClaim) is null)
                    {
                        ctx.Fail($"Token missing required '{oidc.TenantClaim}' claim.");
                    }
                };
            });

        return services;
    }
}
