using ProgramInsights.Models;
using Snowflake.Data.Client;

namespace ProgramInsights.Services;

public class ScenarioService
{
    private readonly string _connStr;

    public ScenarioService(IConfiguration config)
    {
        _connStr = config.GetConnectionString("Snowflake") ?? "";
    }

    public bool IsConfigured => !string.IsNullOrEmpty(_connStr);

    public async Task<List<ScenarioDto>> GetAllAsync(Guid tenantId)
    {
        return await Task.Run(() =>
        {
            var result = new List<ScenarioDto>();
            if (!IsConfigured) return result;
            using var conn = SnowflakeDb.OpenConnection(_connStr);
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
        });
    }

    public async Task<Guid> CreateAsync(Guid tenantId, string name, string sourceDb)
    {
        return await Task.Run(() =>
        {
            var modelId = Guid.NewGuid();
            using var conn = SnowflakeDb.OpenConnection(_connStr);
            using (var cmd = SnowflakeDb.CreateCommand(
                "INSERT INTO MODEL (TENANT, MODEL_ID, NAME, ACE_MODEL, ACE_DBNAME) VALUES (:Tenant, :Model_ID, :Name, '', :Ace_DBName)", conn))
            {
                SnowflakeDb.AddParam(cmd, "Tenant", tenantId.ToString());
                SnowflakeDb.AddParam(cmd, "Model_ID", modelId.ToString());
                SnowflakeDb.AddParam(cmd, "Name", name);
                SnowflakeDb.AddParam(cmd, "Ace_DBName", sourceDb);
                cmd.ExecuteNonQuery();
            }
            SeedDefaultProfiles(conn, modelId);
            return modelId;
        });
    }

    public async Task UpdateAsync(Guid modelId, string name, string sourceDb)
    {
        await Task.Run(() =>
        {
            using var conn = SnowflakeDb.OpenConnection(_connStr);
            using var cmd = SnowflakeDb.CreateCommand(
                "UPDATE MODEL SET NAME = :Name, ACE_DBNAME = :Ace_DBName, SAVE_DATE = CURRENT_TIMESTAMP() WHERE MODEL_ID = :Model_ID", conn);
            SnowflakeDb.AddParam(cmd, "Name", name);
            SnowflakeDb.AddParam(cmd, "Ace_DBName", sourceDb);
            SnowflakeDb.AddParam(cmd, "Model_ID", modelId.ToString());
            cmd.ExecuteNonQuery();
        });
    }

    public async Task DeleteAsync(Guid tenantId, Guid modelId)
    {
        await Task.Run(() =>
        {
            using var conn = SnowflakeDb.OpenConnection(_connStr);
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

            // Drop the per-scenario Snowflake views (created by the mapping wizard)
            var safeId = modelId.ToString().Replace("-", "_");
            ExecuteRaw(conn, $"DROP VIEW IF EXISTS V_COURSE_VALUES_{safeId}");
            ExecuteRaw(conn, $"DROP VIEW IF EXISTS V_UNIT_VALUES_{safeId}");
        });
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

    private static void SeedDefaultProfiles(SnowflakeDbConnection conn, Guid modelId)
    {
        const string q =
            "INSERT INTO PROFILES (PROFILE_ID, MODEL_ID, LEVEL, NAME, TUT_SIZE, LECT_HOURS, TUT_HOURS, PREP_HOURS, ADMIN_HOURS, STUDENT_HOURS) " +
            "VALUES (:PK, :Model_ID, :Level, :Name, :TutSize, :LectHours, :TutHours, :PrepHours, :AdminHours, :StudentHours)";

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
            cmd.ExecuteNonQuery();
        }

        Seed("Standard", 20, 24, 24, 2, 30, 1.5);
        Seed("Online", 500, 12, 0, 3, 30, 5.0);
        Seed("HDR", 5, 0, 0, 0, 30, 30.0);
    }
}
