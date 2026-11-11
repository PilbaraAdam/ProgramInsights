using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Options;
using ProgramInsights.Services.Connections;

namespace ProgramInsights.Services.Auth;

/// <summary>
/// Preview deployment: every query runs as the key-pair service account, but the
/// credential still names the signed-in Preview email as its principal, so the
/// tenant resolver can scope saved work to that email and QUERY_TAG records who
/// acted.
/// </summary>
internal sealed class PreviewSnowflakeTokenProvider : ISnowflakeTokenProvider
{
    private readonly AuthenticationStateProvider _authState;
    private readonly PreviewOptions _preview;

    public PreviewSnowflakeTokenProvider(AuthenticationStateProvider authState, IOptions<PreviewOptions> preview)
    {
        _authState = authState;
        _preview = preview.Value;
    }

    public async Task<SnowflakeCredential> GetAsync(bool forceRefresh = false, CancellationToken ct = default)
    {
        var state = await _authState.GetAuthenticationStateAsync();
        if (state.User?.Identity?.IsAuthenticated != true)
            throw new SnowflakeAuthExpiredException("You are not signed in.");

        var email = state.User.FindFirst(ClaimTypes.Email)?.Value ?? state.User.Identity?.Name;
        if (string.IsNullOrWhiteSpace(email))
            throw new SnowflakeAccessDeniedException("Your Preview session has no email. Sign in again.");

        return ForEmail(_preview, email);
    }

    /// <summary>The service credential acting for a Preview email (also used before sign-in, to check a password).</summary>
    public static SnowflakeCredential ForEmail(PreviewOptions preview, string email) =>
        new(AccessToken: "", Principal: email.Trim().ToLowerInvariant())
        {
            ServiceUser = preview.ServiceUser,
            PrivateKey = preview.PrivateKey,
            PrivateKeyPath = preview.PrivateKeyPath,
            PrivateKeyPassphrase = preview.PrivateKeyPassphrase,
        };
}
