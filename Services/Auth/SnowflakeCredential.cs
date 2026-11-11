namespace ProgramInsights.Services.Auth;

/// <summary>
/// What a Snowflake session authenticates with, plus the person acting.
///
/// Two shapes:
/// <list type="bullet">
///   <item>SSO (clients and members): an Entra access token, passed as <c>token=</c>
///   with <c>authenticator=oauth</c>, connecting as the user's own Snowflake user.</item>
///   <item>Preview: a key-pair service account (<see cref="ServiceUser"/>), because a
///   Preview user has no Entra identity. <see cref="Principal"/> is still the Preview
///   email, so QUERY_TAG and the audit trail name the person, not the service.</item>
/// </list>
/// </summary>
/// <param name="AccessToken">
/// The Entra access token, or empty for a key-pair credential. Never log this and
/// never put it in a UI.
/// </param>
/// <param name="Principal">
/// The acting person: Entra UPN for SSO (which must match the Snowflake user's
/// LOGIN_NAME), Preview email otherwise. Already sanitised for connection-string safety.
/// </param>
public sealed record SnowflakeCredential(string AccessToken, string Principal)
{
    /// <summary>Key-pair service user to log in as. Set only for Preview.</summary>
    public string? ServiceUser { get; init; }

    /// <summary>PEM private key content, or use <see cref="PrivateKeyPath"/>.</summary>
    public string? PrivateKey { get; init; }
    public string? PrivateKeyPath { get; init; }
    public string? PrivateKeyPassphrase { get; init; }

    public bool IsKeyPair => !string.IsNullOrEmpty(ServiceUser);

    // Guard against the token or key accidentally reaching a log or an error page.
    public override string ToString() =>
        IsKeyPair
            ? $"SnowflakeCredential {{ Principal = {Principal}, ServiceUser = {ServiceUser} }}"
            : $"SnowflakeCredential {{ Principal = {Principal} }}";
}
