namespace ProgramInsights.Services.Auth;

/// <summary>
/// The user's sign-in can no longer be used to reach Snowflake and they must
/// re-authenticate. Raised when MSAL cannot silently refresh (no refresh token,
/// cache lost on restart, or a conditional-access policy demands interaction), or
/// when Snowflake rejects a freshly-acquired token twice.
///
/// The UI should respond by forcing a full-page navigation to the OIDC challenge —
/// never by showing a generic error, which is what the old
/// "Snowflake connection not configured" message did.
/// </summary>
public sealed class SnowflakeAuthExpiredException : Exception
{
    public SnowflakeAuthExpiredException(string message, Exception? inner = null)
        : base(message, inner) { }
}
