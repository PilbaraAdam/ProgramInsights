namespace ProgramInsights.Services.Connections;

/// <summary>Which database a connection should default to.</summary>
public enum SnowflakeScope
{
    /// <summary>
    /// The control plane (PROGRAMINSIGHTS_APP.SECURITY). Used for the login-time
    /// tenant lookup — which has to happen before the client database is known — and
    /// for global reference data such as CONSTANTS.
    /// </summary>
    Control,

    /// <summary>
    /// The signed-in user's client database, resolved from their Snowflake role.
    /// Everything the app reads or writes about programs and scenarios uses this.
    /// </summary>
    Tenant
}
