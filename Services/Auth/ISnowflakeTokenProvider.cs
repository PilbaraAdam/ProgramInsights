namespace ProgramInsights.Services.Auth;

public interface ISnowflakeTokenProvider
{
    /// <summary>
    /// Acquires an Entra access token for the current user, scoped for Snowflake.
    /// Call this per operation rather than caching the result — MSAL handles silent
    /// refresh internally, so a fresh call is cheap and is what keeps a long-lived
    /// Blazor circuit from outliving its token.
    /// </summary>
    /// <param name="forceRefresh">
    /// Bypass the MSAL cache. Used only to retry once after Snowflake rejects a
    /// token it considers expired or invalid.
    /// </param>
    /// <exception cref="SnowflakeAuthExpiredException">Interactive sign-in is required.</exception>
    /// <exception cref="SnowflakeAccessDeniedException">The SSO claims carry no usable identity.</exception>
    Task<SnowflakeCredential> GetAsync(bool forceRefresh = false, CancellationToken ct = default);
}
