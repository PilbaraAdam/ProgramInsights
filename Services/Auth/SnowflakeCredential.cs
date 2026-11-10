namespace ProgramInsights.Services.Auth;

/// <summary>
/// An Entra ID access token plus the principal it belongs to, ready to be handed
/// to Snowflake as an OAuth bearer token.
/// </summary>
/// <param name="AccessToken">
/// The Entra access token. Passed to Snowflake as <c>token=</c> with
/// <c>authenticator=oauth</c>. Never log this and never put it in a UI.
/// </param>
/// <param name="Principal">
/// The user's Entra UPN. Passed to Snowflake as <c>user=</c>, so it must match the
/// Snowflake user's LOGIN_NAME (the External OAuth integration's user-mapping
/// attribute controls that). Already sanitised for connection-string safety.
/// </param>
public sealed record SnowflakeCredential(string AccessToken, string Principal)
{
    // Guard against the token accidentally reaching a log or an error page.
    public override string ToString() => $"SnowflakeCredential {{ Principal = {Principal} }}";
}
