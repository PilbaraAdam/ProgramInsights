using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using ProgramInsights.Services.Auth;
using ProgramInsights.Services.Connections;
using Snowflake.Data.Client;

namespace ProgramInsights.Services.Tenancy;

/// <summary>
/// Resolves a signed-in user to their client database, then caches it.
///
/// Singleton so the cache is shared across circuits: a browser refresh, a second tab,
/// or a SignalR reconnect then costs zero Snowflake round trips. The TTL doubles as
/// the worst-case delay before an APP_USERS deactivation takes effect.
/// </summary>
internal sealed class TenantResolver : ITenantResolver
{
    private readonly SnowflakeSessionOpener _opener;
    private readonly SnowflakeOptions _opts;
    private readonly IMemoryCache _cache;
    private readonly ILogger<TenantResolver> _log;

    public TenantResolver(
        SnowflakeSessionOpener opener,
        IOptions<SnowflakeOptions> opts,
        IMemoryCache cache,
        ILogger<TenantResolver> log)
    {
        _opener = opener;
        _opts = opts.Value;
        _cache = cache;
        _log = log;
    }

    private static string CacheKey(string principal) => $"tenant:{principal.ToLowerInvariant()}";

    public void Invalidate(string principal) => _cache.Remove(CacheKey(principal));

    public async Task<TenantResolution> ResolveAsync(
        SnowflakeCredential cred, CancellationToken ct = default)
    {
        if (_cache.TryGetValue(CacheKey(cred.Principal), out TenantResolution? cached) && cached is not null)
            return cached;

        var resolution = await LookupAsync(cred, ct);

        // Cache failures too, but briefly — otherwise an unprovisioned user hammers
        // Snowflake on every page load, while a genuine grant fix still lands quickly.
        var ttl = resolution.Ok
            ? TimeSpan.FromMinutes(_opts.TenantCacheMinutes)
            : TimeSpan.FromSeconds(30);

        _cache.Set(CacheKey(cred.Principal), resolution, ttl);
        return resolution;
    }

    private async Task<TenantResolution> LookupAsync(SnowflakeCredential cred, CancellationToken ct)
    {
        // Bootstrap: connect to the control plane, because the client database is
        // precisely what we do not know yet.
        await using var conn = await _opener.OpenAsync(
            cred, _opts.ControlDatabase, _opts.ControlSchema, ct);

        // 1. App-level gate. Reads the secure view, which filters to CURRENT_USER(),
        //    so one client cannot enumerate another's users.
        var isActive = await ScalarAsync(conn, "SELECT IS_ACTIVE FROM V_MY_APP_USER", ct);
        if (isActive is null)
            return TenantResolution.Denied(
                "Your account is not registered for Program Insights. Contact support to request access.");
        if (isActive is bool active && !active)
            return TenantResolution.Denied(
                "Your Program Insights access has been deactivated. Contact support.");

        // 2. The role that routing is keyed on. Comes from the user's Snowflake
        //    DEFAULT_ROLE, since the connection deliberately specifies no role.
        var role = (await ScalarAsync(conn, "SELECT CURRENT_ROLE()", ct))?.ToString();
        if (string.IsNullOrWhiteSpace(role) || role.Equals("NONE", StringComparison.OrdinalIgnoreCase))
            return TenantResolution.Denied(
                "Your Snowflake user has no default role. Ask support to set one (DEFAULT_ROLE).");

        // 3. Route the role to its client database.
        string? database = null, clientName = null, tenantIdRaw = null;
        await using (var cmd = new SnowflakeDbCommand(conn))
        {
            cmd.CommandText = "SELECT DATABASE_NAME, CLIENT_NAME, TENANT_ID FROM V_MY_CLIENT";
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                database = reader.IsDBNull(0) ? null : reader.GetString(0);
                clientName = reader.IsDBNull(1) ? null : reader.GetString(1);
                tenantIdRaw = reader.IsDBNull(2) ? null : reader.GetString(2);
            }
        }

        if (string.IsNullOrWhiteSpace(database))
            return TenantResolution.Denied(
                $"Your Snowflake role '{role}' is not mapped to a client database. Contact support.",
                role);

        if (!Guid.TryParse(tenantIdRaw, out var tenantId))
            return TenantResolution.Denied(
                $"The client mapping for role '{role}' has an invalid tenant id. Contact support.",
                role);

        var capabilities = await ProbeCapabilitiesAsync(conn, ct);

        _log.LogInformation(
            "Resolved tenant for {Principal}: role={Role} database={Database} capabilities={Capabilities}",
            cred.Principal, role, database, capabilities);

        return TenantResolution.Success(
            role, database!, clientName ?? database!, tenantId, capabilities);
    }

    /// <summary>
    /// Probes the three optional cross-database datasets. Runs on the control-plane
    /// connection because every one of these is referenced fully qualified, so the
    /// session's default database is irrelevant — one connection, three cheap checks.
    /// </summary>
    private async Task<SnowflakeCapabilities> ProbeCapabilitiesAsync(
        SnowflakeDbConnection conn, CancellationToken ct)
    {
        var benchmarks = await ProbeAsync(conn,
            "SELECT 1 FROM BENCHMARK_APP.CLIENT.BM_FOE4_SECTOR_AGG_BASE LIMIT 1",
            "benchmarks", ct);

        var helpAllocations = await ProbeAsync(conn,
            "SELECT 1 FROM AU_HIGHER_ED_FUNDING.CURATED.HELP_ALLOCATIONS LIMIT 1",
            "HELP allocations", ct);

        // Free: checks the grant rather than paying for a completion.
        var cortex = await ProbeAsync(conn,
            "SELECT IS_DATABASE_ROLE_IN_SESSION('SNOWFLAKE.CORTEX_USER')",
            "Cortex", ct, expectTruthyResult: true);

        return new SnowflakeCapabilities(benchmarks, helpAllocations, cortex);
    }

    private async Task<bool> ProbeAsync(
        SnowflakeDbConnection conn, string sql, string label,
        CancellationToken ct, bool expectTruthyResult = false)
    {
        try
        {
            var value = await ScalarAsync(conn, sql, ct);
            if (value is null) return false;
            return !expectTruthyResult || value is bool b && b
                   || bool.TryParse(value.ToString(), out var parsed) && parsed;
        }
        catch (SnowflakeDbException ex)
        {
            // A missing grant is an expected, recoverable state — the feature just gets
            // switched off in the UI. Logged at Information so it is visible without
            // looking like a fault.
            _log.LogInformation("Capability probe for {Capability} failed: {Message}", label, ex.Message);
            return false;
        }
    }

    private static async Task<object?> ScalarAsync(
        SnowflakeDbConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = new SnowflakeDbCommand(conn) { CommandText = sql };
        var value = await cmd.ExecuteScalarAsync(ct);
        return value is null or DBNull ? null : value;
    }
}
