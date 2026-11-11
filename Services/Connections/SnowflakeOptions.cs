using System.ComponentModel.DataAnnotations;

namespace ProgramInsights.Services.Connections;

/// <summary>
/// Bound from the "Snowflake" configuration section and validated on start, so a
/// missing account or warehouse fails the app at boot rather than on the first query.
/// </summary>
public sealed class SnowflakeOptions
{
    [Required] public string Account { get; set; } = "";

    /// <summary>
    /// Required. The previous shared service account relied on its DEFAULT_WAREHOUSE;
    /// per-user OAuth has no equivalent fallback, and without a warehouse every query
    /// fails with "No active warehouse selected in the current session."
    /// </summary>
    [Required] public string Warehouse { get; set; } = "";

    /// <summary>Control plane — holds the user gate, tenant routing, and global CONSTANTS.</summary>
    [Required] public string ControlDatabase { get; set; } = "PROGRAMINSIGHTS_APP";
    [Required] public string ControlSchema { get; set; } = "SECURITY";

    /// <summary>Schema used inside each client database.</summary>
    [Required] public string DefaultSchema { get; set; } = "PUBLIC";

    /// <summary>Schema in <see cref="ControlDatabase"/> holding members' saved work (SQL/Control/08).</summary>
    [Required] public string WorkspaceSchema { get; set; } = "WORKSPACE";

    /// <summary>Schema in <see cref="ControlDatabase"/> holding Preview users' saved work (SQL/Control/09).</summary>
    [Required] public string PreviewSchema { get; set; } = "PREVIEW";

    /// <summary>e.g. api://&lt;clientId&gt;/session:role-any</summary>
    [Required] public string Scope { get; set; } = "";

    /// <summary>
    /// Off by default and it should stay off. The driver's pool key is the entire
    /// connection string, which now contains a rotating per-user token: pooled sessions
    /// can outlive the token that created them and outlive an APP_USERS deactivation,
    /// and each token rotation accumulates another pool. If per-operation login latency
    /// becomes a problem, batch a page's queries onto one connection instead.
    /// </summary>
    public bool PoolingEnabled { get; set; }

    [Range(5, 300)] public int ConnectionTimeoutSeconds { get; set; } = 30;
    [Range(5, 600)] public int CommandTimeoutSeconds { get; set; } = 120;

    /// <summary>
    /// How long a resolved tenant (role → database) stays cached across circuits.
    /// Also the worst-case delay before an APP_USERS deactivation takes effect.
    /// </summary>
    [Range(1, 240)] public int TenantCacheMinutes { get; set; } = 15;
}
