using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Stampd.Identity.Saml2;

/// <summary>
/// v3.0 alpha.3 — endpoint surface scaffold for the SAML2 Service Provider.
/// Each endpoint returns 501 Not Implemented with a clear "wired in v3.0.0
/// stable" message so adopters can verify their config + DNS + reverse-proxy
/// setup against the right URLs before the full implementation lands.
/// </summary>
/// <remarks>
/// The three endpoints map onto SAML2 SP roles:
/// <list type="bullet">
///   <item><c>GET /api/auth/saml/metadata</c> — SP metadata XML the IdP imports.</item>
///   <item><c>GET /api/auth/saml/login</c> — initiate SP-initiated SSO (redirects to IdP).</item>
///   <item><c>POST /api/auth/saml/acs</c> — Assertion Consumer Service endpoint.</item>
/// </list>
/// </remarks>
public static class Saml2EndpointExtensions
{
    public static IEndpointRouteBuilder MapStampdSaml2Endpoints(this IEndpointRouteBuilder builder)
    {
        var group = builder.MapGroup("/api/auth/saml").WithTags("Saml2");

        group.MapGet("/metadata", NotYetImplemented("metadata"))
            .WithName("Saml2Metadata")
            .WithSummary("SP metadata XML for IdP import. v3.0 alpha.3 scaffold — wired in v3.0.0 stable.");

        group.MapGet("/login", NotYetImplemented("login"))
            .WithName("Saml2Login")
            .WithSummary("SP-initiated SSO entry. v3.0 alpha.3 scaffold — wired in v3.0.0 stable.");

        group.MapPost("/acs", NotYetImplemented("ACS (assertion consumer service)"))
            .WithName("Saml2Acs")
            .WithSummary("Assertion Consumer Service. v3.0 alpha.3 scaffold — wired in v3.0.0 stable.");

        return builder;
    }

    private static Delegate NotYetImplemented(string endpointName) => () =>
        Results.StatusCode(StatusCodes.Status501NotImplemented);
}
