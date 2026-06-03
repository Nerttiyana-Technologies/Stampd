using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;

using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Stampd.WebApi.Auth;

internal static class AuthEndpoint
{
    public static IEndpointRouteBuilder MapAuth(this IEndpointRouteBuilder builder)
    {
        var group = builder.MapGroup("/api/auth").WithTags("Auth");

        group.MapPost("/dev-token", IssueDevTokenAsync)
            .WithName("IssueDevToken")
            .WithSummary("DEV-ONLY. Mints a JWT bearer token for any caller. Disable via Stampd:Auth:Jwt:EnableDevTokenEndpoint=false in production.")
            .AllowAnonymous();

        return builder;
    }

    private static IResult IssueDevTokenAsync(
        [FromBody] DevTokenRequest body,
        [FromServices] IOptions<JwtOptions> jwtOptions)
    {
        var opts = jwtOptions.Value;

        if (!opts.EnableDevTokenEndpoint)
        {
            return Results.NotFound();
        }

        ArgumentNullException.ThrowIfNull(body);
        if (string.IsNullOrWhiteSpace(body.Subject))
        {
            return Results.Problem("Subject is required.", statusCode: 400);
        }

        var keyBytes = Encoding.UTF8.GetBytes(opts.SigningKey);
        var key = new SymmetricSecurityKey(keyBytes);
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, body.Subject),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
        };

        if (!string.IsNullOrWhiteSpace(body.TenantId))
        {
            claims.Add(new Claim("tenant_id", body.TenantId));
        }

        foreach (var role in body.Roles ?? [])
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }

        var now = DateTime.UtcNow;
        var token = new JwtSecurityToken(
            issuer: opts.Issuer,
            audience: opts.Audience,
            claims: claims,
            notBefore: now,
            expires: now.Add(opts.TokenLifetime),
            signingCredentials: creds);

        var jwt = new JwtSecurityTokenHandler().WriteToken(token);

        return Results.Ok(new DevTokenResponse(
            AccessToken: jwt,
            TokenType: "Bearer",
            ExpiresAtUtc: token.ValidTo));
    }

    public sealed record DevTokenRequest(
        string Subject,
        string? TenantId = null,
        IReadOnlyList<string>? Roles = null);

    public sealed record DevTokenResponse(
        string AccessToken,
        string TokenType,
        DateTime ExpiresAtUtc);
}
