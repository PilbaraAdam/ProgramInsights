using Snowflake.Data.Client;

namespace ProgramInsights.Services.Connections;

public interface ISnowflakeConnectionFactory
{
    /// <summary>
    /// Opens a Snowflake connection as the current user, defaulted to the requested
    /// scope's database.
    ///
    /// Call this per operation. Do not cache the returned connection on a service or a
    /// component: it carries a token that expires, and the driver is not safe for
    /// concurrent commands on one connection (several pages start parallel loads).
    /// </summary>
    /// <exception cref="Auth.SnowflakeAuthExpiredException">Re-authentication is required.</exception>
    /// <exception cref="Auth.SnowflakeAccessDeniedException">The user is not provisioned.</exception>
    Task<SnowflakeDbConnection> OpenAsync(
        SnowflakeScope scope = SnowflakeScope.Tenant, CancellationToken ct = default);
}
