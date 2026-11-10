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

    // Guards against several components resolving concurrently on first render —
    // ProgramView kicks off benchmark loads in parallel with its main load.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private TenantResolution? _resolution;

    public TenantContext(ISnowflakeTokenProvider tokens, ITenantResolver resolver)
    {
        _tokens = tokens;
        _resolver = resolver;
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
            throw new SnowflakeAccessDeniedException(resolution.Reason ?? "No client database is assigned to your account.");
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
