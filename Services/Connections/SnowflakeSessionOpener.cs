using Microsoft.Extensions.Options;
using ProgramInsights.Services.Auth;
using Snowflake.Data.Client;

namespace ProgramInsights.Services.Connections;

/// <summary>
/// Opens a Snowflake session for an explicit (credential, database, schema).
///
/// Deliberately knows nothing about tenants. That is what lets both
/// <see cref="SnowflakeConnectionFactory"/> (which needs the tenant's database) and
/// <see cref="Tenancy.TenantResolver"/> (which runs the lookup that *determines* the
/// tenant's database) share it without a circular dependency.
///
/// Stateless, so registered as a singleton.
/// </summary>
public sealed class SnowflakeSessionOpener
{
    // Snowflake error codes for a rejected External OAuth token.
    private const int ExtOAuthAccessTokenExpired = 390318;
    private const int ExtOAuthAccessTokenInvalid = 390303;

    private readonly SnowflakeOptions _opts;

    public SnowflakeSessionOpener(IOptions<SnowflakeOptions> opts) => _opts = opts.Value;

    /// <param name="database">
    /// Default database for the session. Pass null/empty to omit it (and the schema)
    /// entirely — useful for diagnosing whether a connection failure is caused by a
    /// missing database or by the OAuth handshake itself.
    /// </param>
    public async Task<SnowflakeDbConnection> OpenAsync(
        SnowflakeCredential cred, string? database, string? schema, CancellationToken ct = default)
    {
        // Build with the builder, never by concatenation: an Entra access token is a
        // 2-8 KB base64url JWT and the builder handles quoting/escaping for us.
        var csb = new SnowflakeDbConnectionStringBuilder
        {
            ["account"] = _opts.Account,
            ["user"] = cred.Principal,
            ["authenticator"] = "oauth",
            ["token"] = cred.AccessToken,
            ["warehouse"] = _opts.Warehouse,
            ["application"] = "ProgramInsights",
            ["poolingEnabled"] = _opts.PoolingEnabled ? "true" : "false",
            ["connection_timeout"] = _opts.ConnectionTimeoutSeconds.ToString(),
        };

        // Snowflake fails session creation outright if db/schema name a non-existent
        // object, so omitting them isolates the authentication handshake.
        if (!string.IsNullOrWhiteSpace(database))
        {
            csb["db"] = database;
            if (!string.IsNullOrWhiteSpace(schema)) csb["schema"] = schema;
        }

        // Note: no `role=`. Omitting it means the user's Snowflake DEFAULT_ROLE applies,
        // which is exactly what CURRENT_ROLE() reads during tenant resolution.

        var conn = new SnowflakeDbConnection { ConnectionString = csb.ConnectionString };
        try
        {
            await conn.OpenAsync(ct);
            await ApplySessionPreludeAsync(conn, cred, ct);
            return conn;
        }
        catch
        {
            await conn.DisposeAsync();
            throw;
        }
    }

    private async Task ApplySessionPreludeAsync(
        SnowflakeDbConnection conn, SnowflakeCredential cred, CancellationToken ct)
    {
        // Tags every query in Snowflake's QUERY_HISTORY with the acting user — the only
        // practical way to audit who did what, now that sessions are per-user.
        // Parameterised because the principal, though sanitised, is user-derived.
        await using (var tag = new SnowflakeDbCommand(conn))
        {
            tag.CommandText = "ALTER SESSION SET QUERY_TAG = :Tag";
            SnowflakeDb.AddParam(tag, "Tag", $"programinsights | user={cred.Principal}");
            await tag.ExecuteNonQueryAsync(ct);
        }

        // Secondary roles would union every role the user holds, which would let a user
        // belonging to two clients see both at once and break role-based DB routing.
        await using (var roles = new SnowflakeDbCommand(conn))
        {
            roles.CommandText = "USE SECONDARY ROLES NONE";
            await roles.ExecuteNonQueryAsync(ct);
        }
    }

    /// <summary>
    /// True when Snowflake rejected the OAuth token itself (as opposed to a permissions
    /// or SQL error), meaning a forced token refresh is worth one retry.
    /// </summary>
    public static bool IsOAuthTokenRejected(SnowflakeDbException ex) =>
        ex.ErrorCode is ExtOAuthAccessTokenExpired or ExtOAuthAccessTokenInvalid
        || ex.Message.Contains("OAUTH_ACCESS_TOKEN", StringComparison.OrdinalIgnoreCase);
}
