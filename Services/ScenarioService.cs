using ProgramInsights.Models;
using ProgramInsights.Services.Connections;
using ProgramInsights.Services.Tenancy;
using Snowflake.Data.Client;

namespace ProgramInsights.Services;

public class ScenarioService
{
    private readonly ISnowflakeConnectionFactory _db;
    private readonly ITenantContext _tenant;
    private readonly ILogger<ScenarioService> _log;

    public ScenarioService(ISnowflakeConnectionFactory db, ITenantContext tenant, ILogger<ScenarioService> log)
    {
        _db = db;
        _tenant = tenant;
        _log = log;
    }

    public async Task<List<ScenarioDto>> GetAllAsync(Guid tenantId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(SnowflakeScope.Tenant, ct);
        return await Task.Run(() =>
        {
            var result = new List<ScenarioDto>();
            using var cmd = SnowflakeDb.CreateCommand(@"
                SELECT m.MODEL_ID, m.NAME, COALESCE(m.ACE_DBNAME, '') AS ACE_DBNAME,
                       COUNT(DISTINCT p.PROGRAM_ID) AS PROGRAM_COUNT,
                       MAX(CASE WHEN mc.MODEL_ID IS NOT NULL THEN 1 ELSE 0 END) AS HAS_MAPPING_CONFIG
                FROM MODEL m
                LEFT JOIN PROGRAM p ON p.MODEL_ID = m.MODEL_ID
                LEFT JOIN MODEL_CONFIG mc ON mc.MODEL_ID = m.MODEL_ID
                WHERE m.TENANT = :Tenant
                GROUP BY m.MODEL_ID, m.NAME, m.ACE_DBNAME
                ORDER BY m.NAME", conn);
            SnowflakeDb.AddParam(cmd, "Tenant", tenantId.ToString());
            var dt = SnowflakeDb.Fill(cmd);
            foreach (System.Data.DataRow row in dt.Rows)
                result.Add(new ScenarioDto(
                    Guid.Parse(row["MODEL_ID"].ToString()!),
                    row["NAME"].ToString()!,
                    row["ACE_DBNAME"].ToString() ?? "",
                    Convert.ToInt32(row["PROGRAM_COUNT"]),
                    Convert.ToInt32(row["HAS_MAPPING_CONFIG"]) == 1));
            return result;
        }, ct);
    }

    public async Task<Guid> CreateAsync(Guid tenantId, string name, string sourceDb, CancellationToken ct = default)
    {
        var owner = await _tenant.GetOwnerScopeAsync(ct);
        if (owner.TenantId != tenantId)
            throw new Auth.SnowflakeAccessDeniedException("Scenarios can only be created for your own organisation.");
        await using var conn = await _db.OpenAsync(SnowflakeScope.Tenant, ct);
        return await Task.Run(() =>
        {
            var modelId = Guid.NewGuid();
            using (var cmd = SnowflakeDb.CreateCommand(
                $"INSERT INTO MODEL (TENANT, MODEL_ID, NAME, ACE_MODEL, ACE_DBNAME{owner.Col}) VALUES (:Tenant, :Model_ID, :Name, '', :Ace_DBName{owner.Val})", conn))
            {
                owner.Bind(cmd);
                SnowflakeDb.AddParam(cmd, "Tenant", tenantId.ToString());
                SnowflakeDb.AddParam(cmd, "Model_ID", modelId.ToString());
                SnowflakeDb.AddParam(cmd, "Name", name);
                SnowflakeDb.AddParam(cmd, "Ace_DBName", sourceDb);
                cmd.ExecuteNonQuery();
            }
            SeedDefaultProfiles(conn, modelId, owner);
            return modelId;
        }, ct);
    }

    public async Task UpdateAsync(Guid modelId, string name, string sourceDb, CancellationToken ct = default)
    {
        var owner = await _tenant.GetOwnerScopeAsync(ct);
        await using var conn = await _db.OpenAsync(SnowflakeScope.Tenant, ct);
        await Task.Run(() =>
        {
            using var cmd = SnowflakeDb.CreateCommand(
                "UPDATE MODEL SET NAME = :Name, ACE_DBNAME = :Ace_DBName, SAVE_DATE = CURRENT_TIMESTAMP() WHERE MODEL_ID = :Model_ID AND TENANT = :Tenant", conn);
            SnowflakeDb.AddParam(cmd, "Tenant", owner.TenantId.ToString());
            SnowflakeDb.AddParam(cmd, "Name", name);
            SnowflakeDb.AddParam(cmd, "Ace_DBName", sourceDb);
            SnowflakeDb.AddParam(cmd, "Model_ID", modelId.ToString());
            cmd.ExecuteNonQuery();
        }, ct);
    }

    public async Task DeleteAsync(Guid tenantId, Guid modelId, CancellationToken ct = default)
    {
        var owner = await _tenant.GetOwnerScopeAsync(ct);
        await using var conn = await _db.OpenAsync(SnowflakeScope.Tenant, ct);
        await Task.Run(() =>
        {
            // Check ownership before touching any child table: the child deletes below
            // are keyed by MODEL_ID alone.
            owner.EnsureScenario(conn, modelId);

            var programIds = new List<string>();
            using (var cmd = SnowflakeDb.CreateCommand("SELECT PROGRAM_ID FROM PROGRAM WHERE MODEL_ID = :Model", conn))
            {
                SnowflakeDb.AddParam(cmd, "Model", modelId.ToString());
                using var reader = cmd.ExecuteReader();
                while (reader.Read()) programIds.Add(reader["PROGRAM_ID"].ToString()!);
            }
            foreach (var pid in programIds)
                Execute(conn, "DELETE FROM PROGRAM_SETTINGS WHERE PROGRAM_ID = :ProgramID", ("ProgramID", pid));

            Execute(conn, "DELETE FROM UNITS WHERE MODEL_ID = :Model", ("Model", modelId.ToString()));
            Execute(conn, "DELETE FROM PROGRAM WHERE MODEL_ID = :Model", ("Model", modelId.ToString()));
            Execute(conn, "DELETE FROM PROFILES WHERE MODEL_ID = :Model", ("Model", modelId.ToString()));
            Execute(conn, "DELETE FROM MODEL WHERE MODEL_ID = :Model AND TENANT = :Tenant",
                ("Model", modelId.ToString()), ("Tenant", tenantId.ToString()));

            // Drop the per-scenario Snowflake views (created by the mapping wizard). Only
            // client databases ever have them; the shared schemas grant no CREATE VIEW.
            if (owner.OwnerKey is null)
            {
                var safeId = modelId.ToString().Replace("-", "_");
                ExecuteRaw(conn, $"DROP VIEW IF EXISTS V_COURSE_VALUES_{safeId}");
                ExecuteRaw(conn, $"DROP VIEW IF EXISTS V_UNIT_VALUES_{safeId}");
            }
        }, ct);
    }

    private static void Execute(SnowflakeDbConnection conn, string sql, params (string Name, object Value)[] parameters)
    {
        using var cmd = SnowflakeDb.CreateCommand(sql, conn);
        foreach (var (name, value) in parameters)
            SnowflakeDb.AddParam(cmd, name, value);
        cmd.ExecuteNonQuery();
    }

    private static void ExecuteRaw(SnowflakeDbConnection conn, string sql)
    {
        using var cmd = new Snowflake.Data.Client.SnowflakeDbCommand(conn) { CommandText = sql };
        cmd.ExecuteNonQuery();
    }

    private static void SeedDefaultProfiles(SnowflakeDbConnection conn, Guid modelId, OwnerScope owner)
    {
        var q =
            $"INSERT INTO PROFILES (PROFILE_ID, MODEL_ID, LEVEL, NAME, TUT_SIZE, LECT_HOURS, TUT_HOURS, PREP_HOURS, ADMIN_HOURS, STUDENT_HOURS{owner.Col}) " +
            $"VALUES (:PK, :Model_ID, :Level, :Name, :TutSize, :LectHours, :TutHours, :PrepHours, :AdminHours, :StudentHours{owner.Val})";

        void Seed(string name, double tutSize, double lect, double tut, double prep, double admin, double student)
        {
            using var cmd = SnowflakeDb.CreateCommand(q, conn);
            SnowflakeDb.AddParam(cmd, "PK", Guid.NewGuid().ToString());
            SnowflakeDb.AddParam(cmd, "Model_ID", modelId.ToString());
            SnowflakeDb.AddParam(cmd, "Level", "Uni");
            SnowflakeDb.AddParam(cmd, "Name", name);
            SnowflakeDb.AddParam(cmd, "TutSize", tutSize);
            SnowflakeDb.AddParam(cmd, "LectHours", lect);
            SnowflakeDb.AddParam(cmd, "TutHours", tut);
            SnowflakeDb.AddParam(cmd, "PrepHours", prep);
            SnowflakeDb.AddParam(cmd, "AdminHours", admin);
            SnowflakeDb.AddParam(cmd, "StudentHours", student);
            owner.Bind(cmd);
            cmd.ExecuteNonQuery();
        }

        Seed("Standard", 20, 24, 24, 2, 30, 1.5);
        Seed("Online", 500, 12, 0, 3, 30, 5.0);
        Seed("HDR", 5, 0, 0, 0, 30, 30.0);
    }
}
