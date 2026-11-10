namespace ProgramInsights.Services.Tenancy;

/// <summary>
/// Which cross-database datasets the user's Snowflake role can actually reach.
///
/// These are all optional enrichments — a client whose role lacks them must still be
/// able to model programs. Probing once at login lets the UI say "your role has no
/// Cortex access" instead of silently showing an empty panel, which is what the old
/// empty catch blocks did.
/// </summary>
public sealed record SnowflakeCapabilities(
    bool Benchmarks,
    bool HelpAllocations,
    bool Cortex)
{
    public static readonly SnowflakeCapabilities None = new(false, false, false);
}

/// <summary>
/// The outcome of resolving a signed-in user to a client database.
/// </summary>
public sealed record TenantResolution(
    bool Ok,
    string? Reason,
    string? Role,
    string? DatabaseName,
    string? ClientName,
    Guid TenantId,
    SnowflakeCapabilities Capabilities)
{
    public static TenantResolution Denied(string reason, string? role = null) =>
        new(false, reason, role, null, null, Guid.Empty, SnowflakeCapabilities.None);

    public static TenantResolution Success(
        string role, string databaseName, string clientName, Guid tenantId,
        SnowflakeCapabilities capabilities) =>
        new(true, null, role, databaseName, clientName, tenantId, capabilities);
}
