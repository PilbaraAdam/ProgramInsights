using ProgramInsights.Services.Auth;
using ProgramInsights.Services.Connections;
using Snowflake.Data.Client;

namespace ProgramInsights.Services.Tenancy;

/// <summary>
/// Per-circuit snapshot of the current user's tenant. Scoped, so in Blazor Server
/// there is one per SignalR circuit.
/// </summary>
internal sealed class TenantContext : ITenantContext
{
    private readonly ISnowflakeTokenProvider _tokens;
    private readonly ITenantResolver _resolver;
    private readonly InstitutionChoiceStore _choices;

    // Guards against several components resolving concurrently on first render —
    // ProgramView kicks off benchmark loads in parallel with its main load.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private TenantResolution? _resolution;

    public TenantContext(ISnowflakeTokenProvider tokens, ITenantResolver resolver, InstitutionChoiceStore choices)
    {
        _tokens = tokens;
        _resolver = resolver;
        _choices = choices;
    }

    public async Task<TenantResolution> ChooseInstitutionAsync(string role, CancellationToken ct = default)
    {
        var current = await GetAsync(ct);
        var match = current.Memberships.FirstOrDefault(
            m => string.Equals(m.Role, role, StringComparison.OrdinalIgnoreCase));
        if (match is null)
            throw new SnowflakeAccessDeniedException("Choose one of the institutions listed.");

        var cred = await _tokens.GetAsync(ct: ct);
        _choices.Set(cred.Principal, match.Role);
        await InvalidateAsync(ct);

        var resolved = await GetAsync(ct);
        if (!resolved.Ok || !string.Equals(resolved.ConnectRole, match.Role, StringComparison.OrdinalIgnoreCase))
        {
            _choices.Clear(cred.Principal);
            throw new SnowflakeAccessDeniedException(
                resolved.Reason ?? "Could not sign you in as that institution. Sign in again, or contact Pilbara Group.");
        }
        return resolved;
    }

    public async Task<TenantResolution> GetAsync(CancellationToken ct = default)
    {
        if (_resolution is not null) return _resolution;

        await _gate.WaitAsync(ct);
        try
        {
            if (_resolution is not null) return _resolution;

            var cred = await _tokens.GetAsync(ct: ct);
            try
            {
                _resolution = await _resolver.ResolveAsync(cred, ct);
            }
            catch (SnowflakeDbException ex) when (SnowflakeSessionOpener.IsOAuthTokenRejected(ex))
            {
                // Snowflake rejected a token MSAL considered valid. Force a refresh and
                // try once more; a second rejection means genuine re-authentication.
                var fresh = await _tokens.GetAsync(forceRefresh: true, ct: ct);
                try
                {
                    _resolution = await _resolver.ResolveAsync(fresh, ct);
                }
                catch (SnowflakeDbException ex2) when (SnowflakeSessionOpener.IsOAuthTokenRejected(ex2))
                {
                    throw new SnowflakeAuthExpiredException(
                        "Snowflake rejected your sign-in token. Please sign in again.", ex2);
                }
            }

            return _resolution;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string> GetDatabaseAsync(CancellationToken ct = default)
    {
        var resolution = await GetAsync(ct);
        if (!resolution.Ok || string.IsNullOrWhiteSpace(resolution.DatabaseName))
            throw new SnowflakeAccessDeniedException(resolution.Reason ?? "No database is assigned to your account.");
        return resolution.DatabaseName!;
    }

    public async Task InvalidateAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            _resolution = null;
            var cred = await _tokens.GetAsync(ct: ct);
            _resolver.Invalidate(cred.Principal);
        }
        finally
        {
            _gate.Release();
        }
    }
}
