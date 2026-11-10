using Microsoft.Extensions.Options;
using ProgramInsights.Services.Auth;
using ProgramInsights.Services.Tenancy;
using Snowflake.Data.Client;

namespace ProgramInsights.Services.Connections;

/// <summary>
/// The single place the app obtains Snowflake connections.
///
/// Every connection is built from a token acquired at that moment, which is what makes
/// a long-lived Blazor circuit safe: nothing here holds a connection string, a token, or
/// a database name across operations.
/// </summary>
internal sealed class SnowflakeConnectionFactory : ISnowflakeConnectionFactory
{
    private readonly ISnowflakeTokenProvider _tokens;
    private readonly ITenantContext _tenant;
    private readonly SnowflakeSessionOpener _opener;
    private readonly SnowflakeOptions _opts;

    public SnowflakeConnectionFactory(
        ISnowflakeTokenProvider tokens,
        ITenantContext tenant,
        SnowflakeSessionOpener opener,
        IOptions<SnowflakeOptions> opts)
    {
        _tokens = tokens;
        _tenant = tenant;
        _opener = opener;
        _opts = opts.Value;
    }

    public async Task<SnowflakeDbConnection> OpenAsync(
        SnowflakeScope scope = SnowflakeScope.Tenant, CancellationToken ct = default)
    {
        var (database, schema) = scope switch
        {
            SnowflakeScope.Control => (_opts.ControlDatabase, _opts.ControlSchema),
            _ => (await _tenant.GetDatabaseAsync(ct), _opts.DefaultSchema),
        };

        var cred = await _tokens.GetAsync(ct: ct);

        try
        {
            return await _opener.OpenAsync(cred, database, schema, ct);
        }
        catch (SnowflakeDbException ex) when (SnowflakeSessionOpener.IsOAuthTokenRejected(ex))
        {
            // The token looked valid to MSAL but Snowflake refused it. Retry exactly once
            // with a forced refresh. Keeping this here — rather than at the ~20 call
            // sites — is why none of them need to know about token lifetimes.
            var fresh = await _tokens.GetAsync(forceRefresh: true, ct: ct);
            try
            {
                return await _opener.OpenAsync(fresh, database, schema, ct);
            }
            catch (SnowflakeDbException ex2) when (SnowflakeSessionOpener.IsOAuthTokenRejected(ex2))
            {
                throw new SnowflakeAuthExpiredException(
                    "Snowflake rejected your sign-in token. Please sign in again.", ex2);
            }
        }
    }
}
