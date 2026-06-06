using Microsoft.Extensions.Hosting;

namespace Stampd.UI.Services;

/// <summary>
/// v2.0 — single source of truth for "who is the signed-in user, and what can they see"
/// across the Blazor UI. Razor pages call <see cref="IsInRole"/> instead of poking at JWT
/// claims directly so the role-name string only lives in one place.
/// </summary>
/// <remarks>
/// <para>
/// In Development the service mirrors <see cref="DevTokenOptions"/> — the dev-mint flow
/// is the source of truth for what roles the active JWT carries (the UI doesn't try to
/// decode the bearer token, it trusts the options that minted it).
/// </para>
/// <para>
/// In non-Development environments the service returns an empty principal because the
/// UI doesn't yet have a real Blazor auth pipeline (cookie auth + OIDC are post-v2.0
/// roadmap items). Pages that gate on role checks degrade to "no admin nav" in non-Dev
/// — the user can still hit the API directly via the auth bar's manual JWT, but UI
/// chrome stays read-only. Switching to a real auth pipeline later only needs this
/// service's internals to change; no Razor page edits.
/// </para>
/// </remarks>
public sealed class CurrentUserService
{
    private readonly DevTokenOptions? _devTokenOptions;
    private readonly IHostEnvironment _hostEnv;

    public CurrentUserService(IHostEnvironment hostEnv, DevTokenOptions? devTokenOptions = null)
    {
        ArgumentNullException.ThrowIfNull(hostEnv);
        _hostEnv = hostEnv;
        // DevTokenOptions is registered via AddSingleton(new DevTokenOptions {...}) in
        // Program.cs (Development only). In non-Dev the registration is absent and DI
        // resolves to null because the ctor parameter has a default value.
        _devTokenOptions = devTokenOptions;
    }

    /// <summary>
    /// Human-readable display label for the active user. Returns the dev subject in
    /// Development (e.g. "designer-user") or "Not signed in" otherwise.
    /// </summary>
    public string DisplayName =>
        _hostEnv.IsDevelopment() && !string.IsNullOrWhiteSpace(_devTokenOptions?.Subject)
            ? _devTokenOptions.Subject
            : "Not signed in";

    /// <summary>
    /// All roles the active user carries. Empty list when no user is resolved (non-Dev
    /// without a real auth pipeline).
    /// </summary>
    public IReadOnlyList<string> Roles =>
        _hostEnv.IsDevelopment() && _devTokenOptions?.Roles is { Count: > 0 } r
            ? r
            : Array.Empty<string>();

    /// <summary>
    /// Returns true iff the active user holds the specified role. Use the
    /// <c>StampdRoles</c> constants from <c>Stampd.Core.Authorization</c> as the
    /// argument so role names can't drift between the UI and the API authorization
    /// policies. Comparison is case-sensitive (matches how the JWT bearer middleware
    /// expects role claims).
    /// </summary>
    public bool IsInRole(string role)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(role);
        return Roles.Contains(role, StringComparer.Ordinal);
    }

    /// <summary>True iff the active user is signed in (has at least one role).</summary>
    public bool IsAuthenticated => Roles.Count > 0;
}
