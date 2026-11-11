using ProgramInsights.Models;
using ProgramInsights.Services.Connections;
using ProgramInsights.Services.Tenancy;
using Snowflake.Data.Client;

namespace ProgramInsights.Services;

public class DataMappingService
{
    private readonly ISnowflakeConnectionFactory _db;
    private readonly ITenantContext _tenant;
    private readonly ILogger<DataMappingService> _log;

    public DataMappingService(ISnowflakeConnectionFactory db, ITenantContext tenant, ILogger<DataMappingService> log)
    {
        _db = db;
        _tenant = tenant;
        _log = log;
    }

    /// <summary>
    /// Probes whether the signed-in user's role can actually reach the named source
    /// database. Now a real connectivity and grant check rather than a config check.
    /// </summary>
    public async Task<(bool Ok, string Message)> TestConnectionAsync(string sourceDb, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sourceDb))
            return (false, "Enter a database name first.");

        SnowflakeDbConnection conn;
        try
        {
            conn = await _db.OpenAsync(SnowflakeScope.Tenant, ct);
        }
        catch (Exception ex)
        {
            // Opening the connection is itself part of what this method tests, so a
            // failure here is a result to report rather than an exception to propagate.
            return (false, ex.Message);
        }

        await using (conn)
        {
            return await Task.Run(() =>
            {
                try
                {
                    using var cmd = new SnowflakeDbCommand(conn)
                    {
                        CommandText = $"SELECT COUNT(*) FROM {EscId(sourceDb)}.INFORMATION_SCHEMA.TABLES WHERE TABLE_TYPE = 'BASE TABLE'"
                    };
                    cmd.ExecuteScalar();
                    return (true, $"Connected to {sourceDb} successfully.");
                }
                catch (Exception ex)
                {
                    return (false, ex.Message);
                }
            }, ct);
        }
    }

    public async Task<ModelConfigDto> LoadConfigAsync(Guid scenarioId, CancellationToken ct = default)
    {
        var owner = await _tenant.GetOwnerScopeAsync(ct);
        await using var conn = await _db.OpenAsync(SnowflakeScope.Tenant, ct);
        return await Task.Run(() =>
        {
            owner.EnsureScenario(conn, scenarioId);
            var dto = new ModelConfigDto { ModelId = scenarioId.ToString() };
            using var cmd = SnowflakeDb.CreateCommand(
                "SELECT * FROM MODEL_CONFIG WHERE MODEL_ID = :mid LIMIT 1", conn);
            SnowflakeDb.AddParam(cmd, "mid", scenarioId.ToString());
            var dt = SnowflakeDb.Fill(cmd);
            if (dt.Rows.Count == 0) return dto;
            var row = dt.Rows[0];

            // Col() reads a column only if it exists in this DataTable — safe before migrations are applied.
            string? Col(string name) => dt.Columns.Contains(name) ? row[name]?.ToString() : null;

            dto.ConfigId        = Col("CONFIG_ID") ?? "";
            dto.TenantId        = Col("TENANT_ID") ?? "";
            dto.SourceDatabase  = Col("SOURCE_DATABASE") ?? "";
            dto.SourceSchema    = Col("SOURCE_SCHEMA") is { Length: > 0 } ss ? ss : "PUBLIC";
            dto.ModelFilter     = Col("MODEL_FILTER") ?? "";
            dto.PeriodFilter    = Col("PERIOD_FILTER") ?? "";
            dto.FeeTypesDom     = SplitPipe(Col("FEE_TYPES_DOM"));
            dto.FeeTypesInt     = SplitPipe(Col("FEE_TYPES_INT"));
            dto.FeeTypesCgs     = SplitPipe(Col("FEE_TYPES_CGS"));
            dto.FacCostColumns  = SplitPipe(Col("FAC_COST_COLUMNS"));
            dto.RevDomColumns   = SplitPipe(Col("REV_DOM_COLUMNS"));
            dto.RevIntColumns   = SplitPipe(Col("REV_INT_COLUMNS"));
            dto.RevCgsColumns   = SplitPipe(Col("REV_CGS_COLUMNS"));
            // Migration 1 — Upgrade_ModelConfig_FactTable.sql
            dto.FactTable         = Col("FACT_TABLE")         is { Length: > 0 } ft  ? ft  : "FACT_GL_TO_PRODUCTS_TO_COURSES";
            // Migration 2 — Upgrade_ModelConfig_FeeTypeConfig.sql
            dto.FeeTypeTable      = Col("FEE_TYPE_TABLE")     is { Length: > 0 } ftt ? ftt : "COURSES_OBJECTS";
            dto.FeeTypeColumn     = Col("FEE_TYPE_COLUMN")    is { Length: > 0 } ftc ? ftc : "COURSE_FEE_TYPE";
            dto.DeliveryHoursCol  = Col("DELIVERY_HOURS_COL") is { Length: > 0 } dhc ? dhc : "SUPPLIED_AC_HOURS";
            dto.TotalExpCol       = Col("TOTAL_EXP_COL")      is { Length: > 0 } tec ? tec : "EXP";
            // Migration 3 — Upgrade_ModelConfig_DimensionStructure.sql
            dto.CourseCodeCol  = Col("COURSE_CODE_COL")  is { Length: > 0 } ccc ? ccc : "CODE3";
            dto.UnitCodeCol    = Col("UNIT_CODE_COL")    is { Length: > 0 } ucc ? ucc : "CODE2";
            dto.UnitDimTable   = Col("UNIT_DIM_TABLE")   is { Length: > 0 } udt ? udt : "PRODUCTS_OBJECTS";
            dto.IncludeGlJoin  = Col("INCLUDE_GL_JOIN")?.ToUpperInvariant() != "FALSE";
            dto.GlCodeCol      = Col("GL_CODE_COL")      is { Length: > 0 } gcc ? gcc : "CODE1";
            // Migration 4 — Upgrade_ModelConfig_FoeCodeCol.sql
            dto.FoeCodeCol     = Col("FOE_CODE_COL") ?? "";
            return dto;
        });
    }

    public async Task SaveConfigAsync(ModelConfigDto dto, Guid tenantId, CancellationToken ct = default)
    {
        var access = await _tenant.GetAsync(ct);
        switch (access.Tier)
        {
            case AccessTier.Preview:
                throw new Auth.SnowflakeAccessDeniedException("The Preview has no data connection.");
            case AccessTier.Member when !string.Equals(
                dto.SourceDatabase?.Trim(), access.ModelSourceDatabase, StringComparison.OrdinalIgnoreCase):
                // Members connect only to their own institution's loaded model.
                throw new Auth.SnowflakeAccessDeniedException(access.ModelSourceDatabase is null
                    ? "Your institution's model has not been loaded into Snowflake yet."
                    : $"Your institution's model is in {access.ModelSourceDatabase}.");
        }

        var owner = await _tenant.GetOwnerScopeAsync(ct);
        if (owner.TenantId != tenantId)
            throw new Auth.SnowflakeAccessDeniedException("That scenario was not found.");

        await using var conn = await _db.OpenAsync(SnowflakeScope.Tenant, ct);
        await Task.Run(() =>
        {
            owner.EnsureScenario(conn, Guid.Parse(dto.ModelId));

            if (string.IsNullOrEmpty(dto.ConfigId))
                dto.ConfigId = Guid.NewGuid().ToString();

            // Only the connection + filter fields are required for the normalised view approach.
            // Legacy column-mapping columns are preserved in the table but not written here.
            // Matched on MODEL_ID too, so a CONFIG_ID that belongs to another scenario
            // can never be updated through this one.
            var sql = $@"
                MERGE INTO MODEL_CONFIG t USING (SELECT :cfg_id AS CONFIG_ID, :model_id AS MODEL_ID) s
                    ON t.CONFIG_ID = s.CONFIG_ID AND t.MODEL_ID = s.MODEL_ID
                WHEN MATCHED THEN UPDATE SET
                    SOURCE_DATABASE = :src_db,
                    SOURCE_SCHEMA   = :src_schema,
                    MODEL_FILTER    = :model_flt,
                    PERIOD_FILTER   = :period_flt,
                    UPDATED_DATE    = CURRENT_TIMESTAMP()
                WHEN NOT MATCHED THEN INSERT (
                    CONFIG_ID, MODEL_ID, TENANT_ID,
                    SOURCE_DATABASE, SOURCE_SCHEMA,
                    MODEL_FILTER, PERIOD_FILTER{owner.Col}
                ) VALUES (
                    :cfg_id, :model_id, :tenant_id,
                    :src_db, :src_schema,
                    :model_flt, :period_flt{owner.Val}
                )";

            using var cmd = SnowflakeDb.CreateCommand(sql, conn);
            SnowflakeDb.AddParam(cmd, "cfg_id",    dto.ConfigId);
            SnowflakeDb.AddParam(cmd, "model_id",  dto.ModelId);
            SnowflakeDb.AddParam(cmd, "tenant_id", tenantId.ToString());
            SnowflakeDb.AddParam(cmd, "src_db",    dto.SourceDatabase);
            SnowflakeDb.AddParam(cmd, "src_schema", dto.SourceSchema);
            SnowflakeDb.AddParam(cmd, "model_flt", dto.ModelFilter);
            SnowflakeDb.AddParam(cmd, "period_flt", dto.PeriodFilter);
            owner.Bind(cmd);
            cmd.ExecuteNonQuery();
        }, ct);
    }

    public async Task<List<string>> GetAvailableTablesAsync(string sourceDb, string schema = "PUBLIC")
        => await QueryDistinctAsync($@"
            SELECT TABLE_NAME FROM {sourceDb}.INFORMATION_SCHEMA.TABLES
            WHERE TABLE_SCHEMA = '{EscSql(schema.ToUpperInvariant())}'
            ORDER BY TABLE_NAME");

    /// <summary>
    /// Inspects the fact table columns + table name to infer the dimension structure.
    /// Returns (Preset, Confident, Reason):
    ///   Preset     — "GL3" | "GL2" | "U2"
    ///   Confident  — true if the preset was determined unambiguously
    ///   Reason     — human-readable description shown in the wizard
    /// </summary>
    public async Task<(string Preset, bool Confident, string Reason)> DetectStructureAsync(
        string sourceDb, string schema, string factTable)
    {
        try
        {
            // Step 1 — does CODE3 exist in this table?
            await using var conn = await _db.OpenAsync(SnowflakeScope.Tenant);
            var hasCode3 = await Task.Run(() =>
            {
                using var cmd = new SnowflakeDbCommand(conn)
                {
                    CommandText = $@"
                        SELECT COUNT(*) FROM {EscId(sourceDb)}.INFORMATION_SCHEMA.COLUMNS
                        WHERE TABLE_SCHEMA = '{EscSql(schema.ToUpperInvariant())}'
                          AND TABLE_NAME   = '{EscSql(factTable.ToUpperInvariant())}'
                          AND COLUMN_NAME  = 'CODE3'"
                };
                return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
            });

            // No CODE3 → must be a 2-code table (Units → Courses)
            if (!hasCode3)
                return ("U2", true,
                    "No CODE3 column found — this is a Units → Courses (2-code) table.");

            // Has CODE3 — count how many _TO_ segments are in the table name
            // FACT_GL_TO_PRODUCTS_TO_COURSES  →  2  →  GL3
            // FACT_GL_TO_COURSES              →  1  →  GL2
            var upper = factTable.ToUpperInvariant();
            int toCount = 0;
            int pos = 0;
            while ((pos = upper.IndexOf("_TO_", pos, StringComparison.Ordinal)) >= 0)
            {
                toCount++;
                pos++;
            }

            if (toCount >= 2)
                return ("GL3", true,
                    $"Three-part hierarchy detected ({toCount} '_TO_' segments) — GL → Units → Courses.");

            if (toCount == 1 && upper.Contains("GL"))
                return ("GL2", true,
                    "Two-part hierarchy detected — GL → Courses (direct, no unit view).");

            // Has CODE3 but name doesn't match a known pattern — ask
            return ("GL3", false,
                "Table has a CODE3 column but the name doesn't match a known pattern. Please confirm the structure below.");
        }
        catch (Exception ex)
        {
            return ("GL3", false,
                $"Could not read table metadata ({ex.Message.Split('\n')[0]}). Please select the structure manually.");
        }
    }

    public async Task<bool> VerifyViewExistsAsync(string sourceDb, string schema, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(SnowflakeScope.Tenant, ct);
        return await Task.Run(() =>
        {
            try
            {
                using var cmd = new SnowflakeDbCommand(conn)
                {
                    CommandText =
                        $"SELECT COUNT(*) FROM {EscId(sourceDb)}.INFORMATION_SCHEMA.VIEWS " +
                        $"WHERE TABLE_SCHEMA = '{EscSql(schema.ToUpperInvariant())}' " +
                        $"AND TABLE_NAME = 'VW_APP_COURSE_UNIT_SUMMARY'"
                };
                return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
            }
            catch (SnowflakeDbException ex)
            {
                _log.LogWarning(ex,
                    "Could not verify VW_APP_COURSE_UNIT_SUMMARY in {Db}.{Schema}", sourceDb, schema);
                return false;
            }
        }, ct);
    }

    public async Task<List<string>> GetAvailableModelsAsync(string sourceDb, string schema)
        => await QueryDistinctAsync(
            $"SELECT DISTINCT MODEL FROM {EscId(sourceDb)}.{EscId(schema)}.VW_APP_COURSE_UNIT_SUMMARY ORDER BY 1");

    public async Task<List<string>> GetAvailablePeriodsAsync(string sourceDb, string schema)
        => await QueryDistinctAsync($@"
            SELECT PERIOD_NAME FROM (
                SELECT DISTINCT PERIOD_NAME, MAX(PERIOD_BEGIN) AS PB
                FROM {EscId(sourceDb)}.{EscId(schema)}.VW_APP_COURSE_UNIT_SUMMARY
                GROUP BY PERIOD_NAME
            ) ORDER BY PB DESC");

    public async Task<List<string>> GetAvailableFeeTypesAsync(string sourceDb, string schema, string feeTypeTable, string feeTypeColumn)
    {
        try
        {
            return await QueryDistinctAsync(
                $"SELECT DISTINCT {feeTypeColumn} FROM {sourceDb}.{schema}.{feeTypeTable} ORDER BY 1");
        }
        catch
        {
            return new List<string>();
        }
    }

    public async Task<List<string>> GetAvailableFactColumnsAsync(string sourceDb, string schema, string factTable)
        => await QueryDistinctAsync($@"
            SELECT COLUMN_NAME FROM {sourceDb}.INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = '{EscSql(schema.ToUpperInvariant())}'
              AND TABLE_NAME = '{EscSql(factTable.ToUpperInvariant())}'
              AND DATA_TYPE = 'FLOAT'
            ORDER BY ORDINAL_POSITION");

    /// <summary>
    /// Stamps the MODEL row with the selected model filter value (ACE_MODEL).
    /// No view SQL is generated — VW_APP_COURSE_UNIT_SUMMARY is pre-built in Snowflake.
    /// </summary>
    public async Task<string> RegisterModelAsync(string modelId, string modelFilter, CancellationToken ct = default)
    {
        var owner = await _tenant.GetOwnerScopeAsync(ct);
        await using var conn = await _db.OpenAsync(SnowflakeScope.Tenant, ct);
        return await Task.Run(() =>
        {
            try
            {
                using var cmd = SnowflakeDb.CreateCommand(
                    "UPDATE MODEL SET SAVE_DATE = CURRENT_TIMESTAMP(), ACE_MODEL = :model_flt WHERE MODEL_ID = :mid AND TENANT = :tenant", conn);
                SnowflakeDb.AddParam(cmd, "tenant", owner.TenantId.ToString());
                SnowflakeDb.AddParam(cmd, "model_flt", modelFilter);
                SnowflakeDb.AddParam(cmd, "mid", modelId);
                cmd.ExecuteNonQuery();
                return "";
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }, ct);
    }

    public string BuildCourseViewSql(ModelConfigDto dto)
    {
        var src = $"{dto.SourceDatabase}.{dto.SourceSchema}";
        var facExpr = ColSum(dto.FacCostColumns);
        var courseModCol = ModuleCol(dto.CourseCodeCol);
        var glJoin = dto.IncludeGlJoin && !string.IsNullOrEmpty(dto.GlCodeCol)
            ? $"\nJOIN {src}.GL_OBJECTS g ON f.{dto.GlCodeCol} = g.CODE AND f.{ModuleCol(dto.GlCodeCol)} = g.MODULE_CODE"
            : "";
        return $@"CREATE OR REPLACE VIEW V_COURSE_VALUES_{SafeId(dto.ModelId)} AS
SELECT
    f.MODELFK AS Model_ID,
    c.COURSE_SCHOOL AS School,
    c.COURSE_CAREER AS Career,
    c.COURSE_NAME AS Course_Name,
    SUM({facExpr}) AS Exp_Fac,
    SUM(f.{dto.TotalExpCol} - ({facExpr})) AS Exp_NonFac,
    SUM(CASE WHEN c.{dto.FeeTypeColumn} IN ({InList(dto.FeeTypesDom)}) THEN f.E_F_T_S_L ELSE 0 END) AS EFTSL_Dom,
    SUM(CASE WHEN c.{dto.FeeTypeColumn} IN ({InList(dto.FeeTypesInt)}) THEN f.E_F_T_S_L ELSE 0 END) AS EFTSL_Int,
    SUM(CASE WHEN c.{dto.FeeTypeColumn} IN ({InList(dto.FeeTypesCgs)}) THEN f.E_F_T_S_L ELSE 0 END) AS EFTSL_CGS,
    SUM(f.{dto.DeliveryHoursCol}) AS Delivery_Hours,
    SUM(CASE WHEN c.{dto.FeeTypeColumn} IN ({InList(dto.FeeTypesDom)}) THEN {ColSum(dto.RevDomColumns)} ELSE 0 END) AS RevDom,
    SUM(CASE WHEN c.{dto.FeeTypeColumn} IN ({InList(dto.FeeTypesInt)}) THEN {ColSum(dto.RevIntColumns)} ELSE 0 END) AS RevInt,
    SUM(CASE WHEN c.{dto.FeeTypeColumn} IN ({InList(dto.FeeTypesCgs)}) THEN {ColSum(dto.RevCgsColumns)} ELSE 0 END) AS RevCGS
FROM {src}.{dto.FactTable} f
JOIN {src}.{dto.FeeTypeTable} c ON f.{dto.CourseCodeCol} = c.CODE AND f.{courseModCol} = c.MODULE_CODE{glJoin}
WHERE f.MODEL = '{EscSql(dto.ModelFilter)}'
  AND f.PERIOD_NAME = '{EscSql(dto.PeriodFilter)}'
GROUP BY f.MODELFK, c.COURSE_SCHOOL, c.COURSE_CAREER, c.COURSE_NAME";
    }

    public string BuildUnitViewSql(ModelConfigDto dto)
    {
        var src = $"{dto.SourceDatabase}.{dto.SourceSchema}";
        var facExpr = ColSum(dto.FacCostColumns);
        var courseModCol = ModuleCol(dto.CourseCodeCol);
        var unitModCol = ModuleCol(dto.UnitCodeCol);
        var glJoin = dto.IncludeGlJoin && !string.IsNullOrEmpty(dto.GlCodeCol)
            ? $"\nJOIN {src}.GL_OBJECTS g ON f.{dto.GlCodeCol} = g.CODE AND f.{ModuleCol(dto.GlCodeCol)} = g.MODULE_CODE"
            : "";
        var foeCol = !string.IsNullOrWhiteSpace(dto.FoeCodeCol)
            ? $"\n    MAX(p.{dto.FoeCodeCol}) AS FOE_Code,"
            : "\n    NULL AS FOE_Code,";
        return $@"CREATE OR REPLACE VIEW V_UNIT_VALUES_{SafeId(dto.ModelId)} AS
SELECT
    f.MODELFK AS Model_ID,
    p.PRODUCT_SCHOOL AS School,
    c.COURSE_CAREER AS Career,
    p.UNIT_NAME AS Unit_Name,{foeCol}
    SUM({facExpr}) AS Exp_Fac,
    SUM(f.{dto.TotalExpCol} - ({facExpr})) AS Exp_NonFac,
    SUM(CASE WHEN c.{dto.FeeTypeColumn} IN ({InList(dto.FeeTypesDom)}) THEN f.E_F_T_S_L ELSE 0 END) AS EFTSL_Dom,
    SUM(CASE WHEN c.{dto.FeeTypeColumn} IN ({InList(dto.FeeTypesInt)}) THEN f.E_F_T_S_L ELSE 0 END) AS EFTSL_Int,
    SUM(CASE WHEN c.{dto.FeeTypeColumn} IN ({InList(dto.FeeTypesCgs)}) THEN f.E_F_T_S_L ELSE 0 END) AS EFTSL_CGS,
    SUM(f.{dto.DeliveryHoursCol}) AS Delivery_Hours,
    SUM(CASE WHEN c.{dto.FeeTypeColumn} IN ({InList(dto.FeeTypesDom)}) THEN {ColSum(dto.RevDomColumns)} ELSE 0 END) AS RevDom,
    SUM(CASE WHEN c.{dto.FeeTypeColumn} IN ({InList(dto.FeeTypesInt)}) THEN {ColSum(dto.RevIntColumns)} ELSE 0 END) AS RevInt,
    SUM(CASE WHEN c.{dto.FeeTypeColumn} IN ({InList(dto.FeeTypesCgs)}) THEN {ColSum(dto.RevCgsColumns)} ELSE 0 END) AS RevCGS
FROM {src}.{dto.FactTable} f
JOIN {src}.{dto.UnitDimTable} p ON f.{dto.UnitCodeCol} = p.CODE AND f.{unitModCol} = p.MODULE_CODE
JOIN {src}.{dto.FeeTypeTable} c ON f.{dto.CourseCodeCol} = c.CODE AND f.{courseModCol} = c.MODULE_CODE{glJoin}
WHERE f.MODEL = '{EscSql(dto.ModelFilter)}'
  AND f.PERIOD_NAME = '{EscSql(dto.PeriodFilter)}'
GROUP BY f.MODELFK, p.PRODUCT_SCHOOL, c.COURSE_CAREER, p.UNIT_NAME";
    }

    private async Task<List<string>> QueryDistinctAsync(string sql, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(SnowflakeScope.Tenant, ct);
        return await Task.Run(() =>
        {
            var result = new List<string>();
            using var cmd = new SnowflakeDbCommand(conn) { CommandText = sql };
            using var rdr = cmd.ExecuteReader();
            while (rdr.Read())
                if (!rdr.IsDBNull(0)) result.Add(rdr.GetString(0));
            return result;
        }, ct);
    }

    private static void ExecuteRaw(SnowflakeDbConnection conn, string sql)
    {
        using var cmd = new SnowflakeDbCommand(conn) { CommandText = sql };
        cmd.ExecuteNonQuery();
    }

    // CODE1 → MODULE_CODE1, CODE2 → MODULE_CODE2, CODE3 → MODULE_CODE3
    private static string ModuleCol(string codeCol)
        => codeCol.StartsWith("CODE", StringComparison.OrdinalIgnoreCase)
            ? "MODULE_" + codeCol.ToUpperInvariant()
            : "MODULE_CODE1";

    private static string ColSum(List<string> cols)
        => cols.Count == 0 ? "0" : string.Join(" + ", cols.Select(c => $"f.{c}"));

    private static string InList(List<string> vals)
        => vals.Count == 0 ? "''" : string.Join(",", vals.Select(v => $"'{EscSql(v)}'"));

    private static string SafeId(string id) => id.Replace("-", "_");
    private static string EscSql(string s) => s.Replace("'", "''");
    private static string EscId(string id) => id.Replace("\"", "");

    private static List<string> SplitPipe(string? s)
        => string.IsNullOrWhiteSpace(s)
            ? new()
            : s.Split('|').Where(x => !string.IsNullOrWhiteSpace(x)).ToList();

    private static string JoinPipe(List<string> items) => string.Join("|", items);
}
