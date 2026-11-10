namespace ProgramInsights.Services.Auth;

/// <summary>
/// The user authenticated successfully but is not provisioned for the app: not in
/// APP_USERS, marked inactive, has no Snowflake DEFAULT_ROLE, or their role has no
/// row in ROLE_DATABASE_MAP.
///
/// Distinct from <see cref="SnowflakeAuthExpiredException"/> because re-authenticating
/// will not help — someone has to grant them access. <see cref="Reason"/> is safe to
/// show the user and should be actionable.
/// </summary>
public sealed class SnowflakeAccessDeniedException : Exception
{
    public string Reason { get; }

    public SnowflakeAccessDeniedException(string reason, Exception? inner = null)
        : base(reason, inner) => Reason = reason;
}
