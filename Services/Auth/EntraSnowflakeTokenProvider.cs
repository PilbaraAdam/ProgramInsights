using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Identity.Client;
using Microsoft.Identity.Web;

namespace ProgramInsights.Services.Auth;

/// <summary>
/// Acquires Snowflake-scoped Entra access tokens inside a Blazor Server circuit.
/// </summary>
internal sealed class EntraSnowflakeTokenProvider : ISnowflakeTokenProvider
{
    private readonly ITokenAcquisition _tokens;
    private readonly AuthenticationStateProvider _authState;
    private readonly string[] _scopes;

    public EntraSnowflakeTokenProvider(
        ITokenAcquisition tokens,
        AuthenticationStateProvider authState,
        IConfiguration config)
    {
        _tokens = tokens;
        _authState = authState;
        _scopes = new[] { config["Snowflake:Scope"]
            ?? throw new InvalidOperationException("Snowflake:Scope is not configured.") };
    }

    public async Task<SnowflakeCredential> GetAsync(bool forceRefresh = false, CancellationToken ct = default)
    {
        // AuthenticationStateProvider, not IHttpContextAccessor. Injecting the latter
        // into a circuit-scoped service captures the *initial* request's context, which
        // goes stale as soon as the user navigates.
        var state = await _authState.GetAuthenticationStateAsync();
        var user = state.User;

        if (user?.Identity?.IsAuthenticated != true)
            throw new SnowflakeAuthExpiredException("You are not signed in.");

        var principal = ResolvePrincipal(user)
            ?? throw new SnowflakeAccessDeniedException(
                "Could not determine your identity from the SSO claims. Contact support.");

        try
        {
            // The `user:` argument is mandatory here. The parameterless overload reads
            // HttpContext.User, which is null mid-circuit in Blazor Server, and throws
            // a NullReferenceException. MSAL derives the cache key from the oid/tid
            // claims on this principal instead.
            var token = await _tokens.GetAccessTokenForUserAsync(
                _scopes,
                user: user,
                tokenAcquisitionOptions: forceRefresh
                    ? new TokenAcquisitionOptions { ForceRefresh = true }
                    : null);

            return new SnowflakeCredential(token, principal);
        }
        catch (MicrosoftIdentityWebChallengeUserException ex)
        {
            throw new SnowflakeAuthExpiredException("Your sign-in needs to be renewed.", ex);
        }
        catch (MsalUiRequiredException ex)
        {
            // Also the symptom of an app restart with the in-memory token cache: the
            // auth cookie still looks valid but MSAL has nothing cached to refresh from.
            throw new SnowflakeAuthExpiredException("Your sign-in has expired.", ex);
        }
    }

    /// <summary>
    /// Picks the identifier that Snowflake will match against a user's LOGIN_NAME.
    /// Order mirrors the Benchmarks app so both connect as the same Snowflake user.
    /// </summary>
    private static string? ResolvePrincipal(ClaimsPrincipal user) =>
        Sanitize(
            user.FindFirst("preferred_username")?.Value
            ?? user.FindFirst(ClaimTypes.Upn)?.Value
            ?? user.FindFirst("upn")?.Value
            ?? user.FindFirst(ClaimTypes.Email)?.Value
            ?? user.FindFirst("email")?.Value
            ?? user.FindFirst(ClaimTypes.Name)?.Value);

    /// <summary>
    /// This value is interpolated into a connection string, so reject the characters
    /// that could terminate or inject a property as well as control characters.
    /// Returns null rather than a mangled value — a principal we cannot trust should
    /// fail the login, not connect as something unexpected.
    /// </summary>
    private static string? Sanitize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var value = raw.Trim();
        foreach (var c in value)
        {
            if (char.IsControl(c) || c is ';' or '=' or '"' or '\'' or '{' or '}')
                return null;
        }
        return value;
    }
}
