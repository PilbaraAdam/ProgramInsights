namespace ProgramInsights.Services.Connections;

/// <summary>Which database a connection should default to.</summary>
public enum SnowflakeScope
{
    /// <summary>
    /// The control plane (PROGRAMINSIGHTS_APP.SECURITY). Used for global reference
    /// data such as CONSTANTS. (The login-time lookup opens its own connections.)
    /// </summary>
    Control,

    /// <summary>
    /// Where the signed-in user's saved work lives: their client database for clients,
    /// PROGRAMINSIGHTS_APP.WORKSPACE for members, PROGRAMINSIGHTS_APP.PREVIEW for the
    /// Preview. App queries name these tables unqualified, so the same SQL works for
    /// every tier.
    /// </summary>
    Tenant,

    /// <summary>
    /// No default database: for fully-qualified reads of shared data (benchmarks,
    /// HELP allocations, Cortex). Works for every tier, including users with no
    /// client database.
    /// </summary>
    Shared
}
