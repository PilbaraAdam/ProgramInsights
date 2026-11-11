using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using ProgramInsights.Services.Auth;
using ProgramInsights.Services.Connections;
using Snowflake.Data.Client;

namespace ProgramInsights.Services.Tenancy;

/// <summary>
/// Resolves a signed-in user to their access tier and where their work lives, then
/// caches it.
///
/// Three tiers, checked in this order:
/// <list type="number">
///   <item><b>Preview</b> — the credential is the Preview service account. No lookup.</item>
///   <item><b>Client</b> — APP_USERS + ROLE_DATABASE_MAP, exactly as before, so nothing
///   changes for existing Pilbara Insights clients.</item>
///   <item><b>Member</b> — the session can assume a mapped &lt;X&gt;_PROGRAMINSIGHTS_MEMBER
///   role. Ported from Benchmarks' pick_mapped_role / assert_benchmark_access: the role
///   is the licence, and several roles with no basis to choose means the user is asked.</item>
/// </list>
///
/// Singleton so the cache is shared across circuits: a browser refresh, a second tab,
/// or a SignalR reconnect then costs zero Snowflake round trips. The TTL doubles as
/// the worst-case delay before a deactivation takes effect.
/// </summary>
internal sealed class TenantResolver : ITenantResolver
{
    private const string MemberRoleSuffix = "_PROGRAMINSIGHTS_MEMBER";

    private const string NotSetUp =
        "Your account is not set up for Program Insights. Contact Pilbara Group to request access.";

    private readonly SnowflakeSessionOpener _opener;
    private readonly SnowflakeOptions _opts;
    private readonly PreviewOptions _preview;
    private readonly InstitutionChoiceStore _choices;
    private readonly IMemoryCache _cache;
    private readonly ILogger<TenantResolver> _log;

    public TenantResolver(
        SnowflakeSessionOpener opener,
        IOptions<SnowflakeOptions> opts,
        IOptions<PreviewOptions> preview,
        InstitutionChoiceStore choices,
        IMemoryCache cache,
        ILogger<TenantResolver> log)
    {
        _opener = opener;
        _opts = opts.Value;
        _preview = preview.Value;
        _choices = choices;
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

        var resolution = cred.IsKeyPair
            ? await ResolvePreviewAsync(cred, ct)
            : await LookupAsync(cred, ct);

        // Cache failures too, but briefly — otherwise an unprovisioned user hammers
        // Snowflake on every page load, while a genuine grant fix still lands quickly.
        // A pending institution choice is not cached at all: the next call follows the pick.
        if (!resolution.NeedsChoice)
        {
            var ttl = resolution.Ok
                ? TimeSpan.FromMinutes(_opts.TenantCacheMinutes)
                : TimeSpan.FromSeconds(30);
            _cache.Set(CacheKey(cred.Principal), resolution, ttl);
        }
        return resolution;
    }

    // ── Preview ──────────────────────────────────────────────────────────────

    private async Task<TenantResolution> ResolvePreviewAsync(SnowflakeCredential cred, CancellationToken ct)
    {
        await using var conn = await _opener.OpenAsync(cred, null, null, ct, _preview.Role);
        var capabilities = await ProbeCapabilitiesAsync(conn, ct) with { Cortex = false };
        var owner = cred.Principal.Trim().ToLowerInvariant();

        return new TenantResolution(
            true, null, AccessTier.Preview, _preview.Role,
            _opts.ControlDatabase, _opts.PreviewSchema, "Private Preview",
            TenantResolution.DeriveTenantId(AccessTier.Preview, owner), capabilities)
        {
            ConnectRole = _preview.Role,
            OwnerKey = owner,
        };
    }

    // ── SSO: client, then member ─────────────────────────────────────────────

    private async Task<TenantResolution> LookupAsync(SnowflakeCredential cred, CancellationToken ct)
    {
        // Bootstrap on the control plane with the user's DEFAULT_ROLE, because what
        // they are entitled to is precisely what we do not know yet.
        await using var conn = await _opener.OpenAsync(
            cred, _opts.ControlDatabase, _opts.ControlSchema, ct);

        var (client, clientReason) = await TryClientAsync(conn, cred, ct);
        if (client is not null) return client;

        var member = await TryMemberAsync(conn, cred, ct);
        if (member is not null) return member;

        // Neither. A registered-but-unrouted client gets the specific reason; anyone
        // else (including a Benchmarks-only licence holder) gets the generic one.
        return TenantResolution.Denied(clientReason ?? NotSetUp);
    }

    /// <returns>
    /// The client resolution, or (null, reason) when the user is not a working client.
    /// The reason is null when they are not registered as a client at all.
    /// </returns>
    private async Task<(TenantResolution? Resolution, string? Reason)> TryClientAsync(
        SnowflakeDbConnection conn, SnowflakeCredential cred, CancellationToken ct)
    {
        // 1. App-level gate. Reads the secure view, which filters to CURRENT_USER(),
        //    so one client cannot enumerate another's users.
        var isActive = await ScalarAsync(conn, "SELECT IS_ACTIVE FROM V_MY_APP_USER", ct);
        if (isActive is null)
            return (null, null);
        if (isActive is bool active && !active)
            return (null, "Your Program Insights access has been deactivated. Contact support.");

        // 2. The role that routing is keyed on: the user's DEFAULT_ROLE.
        var role = (await ScalarAsync(conn, "SELECT CURRENT_ROLE()", ct))?.ToString();
        if (string.IsNullOrWhiteSpace(role) || role.Equals("NONE", StringComparison.OrdinalIgnoreCase))
            return (null, "Your Snowflake user has no default role. Ask support to set one (DEFAULT_ROLE).");

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
            return (null, $"Your Snowflake role '{role}' is not mapped to a client database. Contact support.");

        if (!Guid.TryParse(tenantIdRaw, out var tenantId))
            return (null, $"The client mapping for role '{role}' has an invalid tenant id. Contact support.");

        var capabilities = await ProbeCapabilitiesAsync(conn, ct);

        _log.LogInformation(
            "Resolved client tenant for {Principal}: role={Role} database={Database} capabilities={Capabilities}",
            cred.Principal, role, database, capabilities);

        return (TenantResolution.Client(
            role, database!, _opts.DefaultSchema, clientName ?? database!, tenantId, capabilities), null);
    }

    private async Task<TenantResolution?> TryMemberAsync(
        SnowflakeDbConnection conn, SnowflakeCredential cred, CancellationToken ct)
    {
        // Every mapped membership role this session can assume (inherited included).
        var memberships = new List<InstitutionChoice>();
        try
        {
            await using var cmd = new SnowflakeDbCommand(conn)
            {
                CommandText = "SELECT ROLE_NAME, MODEL, CLIENT_NAME FROM V_MY_MEMBERSHIPS"
            };
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var r = reader.IsDBNull(0) ? null : reader.GetString(0);
                var m = reader.IsDBNull(1) ? null : reader.GetString(1);
                var n = reader.IsDBNull(2) ? null : reader.GetString(2);
                if (string.IsNullOrWhiteSpace(r) || string.IsNullOrWhiteSpace(m)) continue;
                if (!r.Trim().EndsWith(MemberRoleSuffix, StringComparison.OrdinalIgnoreCase)) continue;
                memberships.Add(new InstitutionChoice(r.Trim(), m.Trim(), string.IsNullOrWhiteSpace(n) ? m.Trim() : n.Trim()));
            }
        }
        catch (SnowflakeDbException ex)
        {
            // V_MY_MEMBERSHIPS not deployed yet (SQL/Control/07) — membership is simply
            // unavailable, not a sign-in fault.
            _log.LogWarning("Membership lookup failed for {Principal}: {Message}", cred.Principal, ex.Message);
            return null;
        }

        if (memberships.Count == 0) return null;

        memberships.Sort((a, b) =>
        {
            var c = string.Compare(a.Institution, b.Institution, StringComparison.OrdinalIgnoreCase);
            return c != 0 ? c : string.Compare(a.Role, b.Role, StringComparison.OrdinalIgnoreCase);
        });

        var chosen = PickMembership(memberships,
            (await ScalarAsync(conn, "SELECT CURRENT_ROLE()", ct))?.ToString(),
            _choices.Get(cred.Principal));

        if (chosen is null)
        {
            _log.LogInformation("{Principal} holds {Roles} with no default among them; asking which.",
                cred.Principal, string.Join(", ", memberships.Select(m => m.Role)));
            return TenantResolution.ChooseInstitution(memberships);
        }

        return await ConfirmMemberAsync(cred, chosen, memberships, ct);
    }

    /// <summary>
    /// The membership to use, or null when the user must be asked. Same precedence as
    /// Benchmarks: an active role that is itself a mapped membership wins; then the
    /// user's own earlier pick, if still valid; then the only one there is.
    /// </summary>
    private static InstitutionChoice? PickMembership(
        IReadOnlyList<InstitutionChoice> memberships, string? activeRole, string? requestedRole)
    {
        InstitutionChoice? Find(string? role) => string.IsNullOrWhiteSpace(role)
            ? null
            : memberships.FirstOrDefault(m => string.Equals(m.Role, role.Trim(), StringComparison.OrdinalIgnoreCase));

        return Find(activeRole)
               ?? Find(requestedRole)
               ?? (memberships.Count == 1 ? memberships[0] : null);
    }

    /// <summary>
    /// Proves the session really comes up on the chosen role — opened with role=, then
    /// CURRENT_ROLE() read back — the same way every later connection will. Then reads
    /// the institution's model database and probes capabilities AS that role.
    /// </summary>
    private async Task<TenantResolution> ConfirmMemberAsync(
        SnowflakeCredential cred, InstitutionChoice chosen, IReadOnlyList<InstitutionChoice> memberships,
        CancellationToken ct)
    {
        try
        {
            await using var conn = await _opener.OpenAsync(
                cred, _opts.ControlDatabase, _opts.ControlSchema, ct, chosen.Role);

            var actual = (await ScalarAsync(conn, "SELECT CURRENT_ROLE()", ct))?.ToString();
            if (!string.Equals(actual, chosen.Role, StringComparison.OrdinalIgnoreCase))
            {
                _log.LogWarning("Refused {Principal}: asked for {Role}, session came up as {Actual}.",
                    cred.Principal, chosen.Role, actual);
                return TenantResolution.Denied(
                    "Could not sign you in as that institution. Sign in again, or contact Pilbara Group.");
            }

            string? modelDb = null, modelSchema = null;
            try
            {
                await using var cmd = new SnowflakeDbCommand(conn)
                {
                    CommandText = "SELECT DATABASE_NAME, SCHEMA_NAME FROM V_MY_MODEL_DB"
                };
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                if (await reader.ReadAsync(ct))
                {
                    modelDb = reader.IsDBNull(0) ? null : reader.GetString(0);
                    modelSchema = reader.IsDBNull(1) ? null : reader.GetString(1);
                }
            }
            catch (SnowflakeDbException ex)
            {
                // No model loaded / view missing: Custom, Benchmark and CGS cluster still work.
                _log.LogInformation("No model database for {Role}: {Message}", chosen.Role, ex.Message);
            }

            var capabilities = await ProbeCapabilitiesAsync(conn, ct);

            _log.LogInformation(
                "Resolved member tenant for {Principal}: role={Role} model={Model} modelDb={ModelDb} capabilities={Capabilities}",
                cred.Principal, chosen.Role, chosen.Model, modelDb ?? "(none)", capabilities);

            return new TenantResolution(
                true, null, AccessTier.Member, chosen.Role,
                _opts.ControlDatabase, _opts.WorkspaceSchema, chosen.Institution,
                TenantResolution.DeriveTenantId(AccessTier.Member, chosen.Model), capabilities)
            {
                ConnectRole = chosen.Role,
                OwnerKey = chosen.Model,
                ModelKey = chosen.Model,
                ModelSourceDatabase = string.IsNullOrWhiteSpace(modelDb) ? null : modelDb,
                ModelSourceSchema = string.IsNullOrWhiteSpace(modelSchema) ? "PUBLIC" : modelSchema,
                Memberships = memberships,
            };
        }
        catch (SnowflakeDbException ex) when (!SnowflakeSessionOpener.IsOAuthTokenRejected(ex))
        {
            _log.LogWarning(ex, "Could not open a session for {Principal} as {Role}", cred.Principal, chosen.Role);
            return TenantResolution.Denied(
                "Could not sign you in as that institution. Sign in again, or contact Pilbara Group.");
        }
    }

    /// <summary>
    /// Probes the three optional cross-database datasets on the given connection, so
    /// the answer reflects the role that connection runs as.
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
