namespace Stampd.WebApi.Auth;

public sealed class JwtOptions
{
    /// <summary>HS256 signing key. Must be 32+ chars. Override in production via Stampd:Auth:Jwt:SigningKey.</summary>
    public string SigningKey { get; set; } = "stampd-dev-only-signing-key-please-replace-in-prod";
    public string Issuer { get; set; } = "stampd";
    public string Audience { get; set; } = "stampd-api";
    public TimeSpan TokenLifetime { get; set; } = TimeSpan.FromHours(8);

    /// <summary>
    /// Enables the <c>/api/auth/dev-token</c> endpoint that mints JWTs for any caller. Only
    /// for dev demos; never enable in production. Production hosts wire a real identity
    /// provider (Entra ID, Auth0, Okta, custom) instead.
    /// </summary>
    public bool EnableDevTokenEndpoint { get; set; } = true;
}
