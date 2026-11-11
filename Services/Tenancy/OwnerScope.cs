using ProgramInsights.Services.Auth;
using Snowflake.Data.Client;

namespace ProgramInsights.Services.Tenancy;

/// <summary>
/// Who owns the rows a request reads and writes.
///
/// Every scenario (MODEL row) carries TENANT. For clients that is their
/// ROLE_DATABASE_MAP tenant id; for members and Preview users it is derived from the
/// institution key or email (<see cref="TenantResolution.DeriveTenantId"/>). So
/// "this scenario belongs to TENANT" is the ownership test for every tier, and the
/// guards below apply it before any operation addressed by a bare MODEL_ID or
/// PROGRAM_ID.
///
/// Members' and Preview users' tables also carry OWNER_KEY, which the row access
/// policy on WORKSPACE enforces in Snowflake. Preview runs as one service account,
/// so for it these guards are the only isolation — keep them on every entry point.
/// </summary>
public sealed record OwnerScope(Guid TenantId, string? OwnerKey)
{
    /// <summary>", OWNER_KEY" for tiers whose tables have the column, else empty.</summary>
    public string Col => OwnerKey is null ? "" : ", OWNER_KEY";

    /// <summary>", :OwnerKey" matching <see cref="Col"/>.</summary>
    public string Val => OwnerKey is null ? "" : ", :OwnerKey";

    public void Bind(SnowflakeDbCommand cmd)
    {
        if (OwnerKey is not null) SnowflakeDb.AddParam(cmd, "OwnerKey", OwnerKey);
    }

    /// <summary>Throws unless the scenario belongs to this tenant.</summary>
    public void EnsureScenario(SnowflakeDbConnection conn, Guid scenarioId)
    {
        using var cmd = SnowflakeDb.CreateCommand(
            "SELECT 1 FROM MODEL WHERE MODEL_ID = :Model AND TENANT = :Tenant LIMIT 1", conn);
        SnowflakeDb.AddParam(cmd, "Model", scenarioId.ToString());
        SnowflakeDb.AddParam(cmd, "Tenant", TenantId.ToString());
        if (cmd.ExecuteScalar() is null or DBNull)
            throw new SnowflakeAccessDeniedException("That scenario was not found.");
    }

    /// <summary>Throws unless the program's scenario belongs to this tenant.</summary>
    public void EnsureProgram(SnowflakeDbConnection conn, Guid programId)
    {
        using var cmd = SnowflakeDb.CreateCommand(
            "SELECT 1 FROM PROGRAM p JOIN MODEL m ON m.MODEL_ID = p.MODEL_ID " +
            "WHERE p.PROGRAM_ID = :Program AND m.TENANT = :Tenant LIMIT 1", conn);
        SnowflakeDb.AddParam(cmd, "Program", programId.ToString());
        SnowflakeDb.AddParam(cmd, "Tenant", TenantId.ToString());
        if (cmd.ExecuteScalar() is null or DBNull)
            throw new SnowflakeAccessDeniedException("That program was not found.");
    }
}

public static class OwnerScopeExtensions
{
    public static async Task<OwnerScope> GetOwnerScopeAsync(this ITenantContext tenant, CancellationToken ct = default)
    {
        var r = await tenant.GetAsync(ct);
        if (!r.Ok) throw new SnowflakeAccessDeniedException(r.Reason ?? "You do not have access to Program Insights.");
        return new OwnerScope(r.TenantId, r.UsesOwnerKey ? r.OwnerKey : null);
    }
}
