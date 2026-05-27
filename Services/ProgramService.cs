using ProgramInsights.Models;
using Snowflake.Data.Client;
using System.Data;

namespace ProgramInsights.Services;

public class ProgramService
{
    private readonly string _connStr;

    public ProgramService(IConfiguration config)
    {
        _connStr = config.GetConnectionString("Snowflake") ?? "";
    }

    public bool IsConfigured => !string.IsNullOrEmpty(_connStr);

    public async Task<ProgramDetailsDto?> GetDetailsAsync(Guid programId)
    {
        return await Task.Run(() =>
        {
            if (!IsConfigured) return null;
            using var conn = SnowflakeDb.OpenConnection(_connStr);

            ProgramDetailsDto? details = null;
            const string programSql =
                "SELECT p.PROGRAM_ID, p.MODEL_ID, p.NAME, p.DESCRIPTION, p.SAVE_DATE, p.START_YEAR, " +
                "p.REVCGS_CLUSTER, p.REVCGS_SELECT, p.REVCGS_CUSTOM, p.REVDOM_SELECT, p.REVDOM_CUSTOM, " +
                "p.REVINT_SELECT, p.REVINT_CUSTOM, p.CPH_SELECT, p.CPH_CUSTOM, p.NFC_SELECT, p.NFC_CUSTOM, " +
                "p.PROFILE, p.SCHOOL_SELECT, p.CAREER_SELECT, " +
                "m.NAME AS MODEL_NAME, m.ACE_MODEL, m.SAVE_DATE AS MODEL_SAVE_DATE, m.ACE_DBNAME " +
                "FROM PROGRAM p LEFT JOIN MODEL m ON p.MODEL_ID = m.MODEL_ID " +
                "WHERE p.PROGRAM_ID = :ProgramID";

            using (var cmd = SnowflakeDb.CreateCommand(programSql, conn))
            {
                SnowflakeDb.AddParam(cmd, "ProgramID", programId.ToString());
                var dt = SnowflakeDb.Fill(cmd);
                if (dt.Rows.Count == 0) return null;

                var row = dt.Rows[0];
                details = new ProgramDetailsDto
                {
                    Id = programId,
                    ScenarioId = GetGuid(row, "MODEL_ID"),
                    Name = GetString(row, "NAME"),
                    Description = GetString(row, "DESCRIPTION"),
                    SaveDate = GetDate(row, "SAVE_DATE"),
                    StartYear = GetInt(row, "START_YEAR", DateTime.Today.Year),
                    RevCgsCluster = GetString(row, "REVCGS_CLUSTER", "Cluster 1"),
                    RevCgsSelect = GetString(row, "REVCGS_SELECT", "Custom"),
                    RevCgsCustom = GetDouble(row, "REVCGS_CUSTOM"),
                    RevDomSelect = GetString(row, "REVDOM_SELECT", "Custom"),
                    RevDomCustom = GetDouble(row, "REVDOM_CUSTOM"),
                    RevIntSelect = GetString(row, "REVINT_SELECT", "Custom"),
                    RevIntCustom = GetDouble(row, "REVINT_CUSTOM"),
                    CphSelect = GetString(row, "CPH_SELECT", "Custom"),
                    CphCustom = GetDouble(row, "CPH_CUSTOM"),
                    NfcSelect = GetString(row, "NFC_SELECT", "Custom"),
                    NfcCustom = GetDouble(row, "NFC_CUSTOM"),
                    Profile = GetString(row, "PROFILE", "Standard"),
                    SchoolSelect = GetString(row, "SCHOOL_SELECT"),
                    CourseSelect = "",
                    CareerSelect = GetString(row, "CAREER_SELECT"),
                    ScenarioName = GetString(row, "MODEL_NAME"),
                    InsightsModel = GetString(row, "ACE_MODEL"),
                    ScenarioSaveDate = GetDate(row, "MODEL_SAVE_DATE"),
                    SourceDb = GetString(row, "ACE_DBNAME")
                };
            }

            details.Settings = GetSettings(conn, programId);
            details.Units = GetUnits(conn, programId);
            details.Profiles = GetProfiles(conn, details.ScenarioId);
            details.UnitOptions = GetUnitOptions(conn, details.ScenarioId, details.SourceDb);
            details.UnitValues = GetUnitValues(conn, details.ScenarioId, details.SourceDb);
            details.ModelValues = GetModelValues(conn, details.ScenarioId, details.SourceDb);
            details.CgsClusters = GetConstants(conn, "CGS");

            if (details.Settings.Count == 0)
            {
                details.Settings.Add(new ProgramYearSettingDto { Year = 0 });
            }

            return details;
        });
    }

    public async Task<List<ProgramDto>> GetByScenarioAsync(Guid scenarioId)
    {
        return await Task.Run(() =>
        {
            var result = new List<ProgramDto>();
            if (!IsConfigured) return result;
            using var conn = SnowflakeDb.OpenConnection(_connStr);

            const string sql = @"
                SELECT
                    p.PROGRAM_ID, p.NAME, p.DESCRIPTION, p.SAVE_DATE,
                    COALESCE(p.START_YEAR, YEAR(CURRENT_DATE())) AS START_YEAR,
                    COALESCE(u.UNIT_COUNT, 0)                   AS UNIT_COUNT,
                    CASE WHEN
                        (p.CPH_SELECT IS NOT NULL AND p.CPH_SELECT != 'Custom'
                            OR COALESCE(p.CPH_CUSTOM, 0) > 0)
                        AND
                        (p.NFC_SELECT IS NOT NULL AND p.NFC_SELECT != 'Custom'
                            OR COALESCE(p.NFC_CUSTOM, 0) > 0)
                    THEN 1 ELSE 0 END                           AS HAS_COST_REVENUE,
                    COALESCE(s.YEAR_COUNT, 0)                   AS YEAR_COUNT
                FROM PROGRAM p
                LEFT JOIN (
                    SELECT PROGRAM_ID, COUNT(*) AS UNIT_COUNT
                    FROM UNITS GROUP BY PROGRAM_ID
                ) u ON p.PROGRAM_ID = u.PROGRAM_ID
                LEFT JOIN (
                    SELECT PROGRAM_ID, COUNT(*) AS YEAR_COUNT
                    FROM PROGRAM_SETTINGS GROUP BY PROGRAM_ID
                ) s ON p.PROGRAM_ID = s.PROGRAM_ID
                WHERE p.MODEL_ID = :Model
                ORDER BY p.NAME";

            using var cmd = SnowflakeDb.CreateCommand(sql, conn);
            SnowflakeDb.AddParam(cmd, "Model", scenarioId.ToString());
            var dt = SnowflakeDb.Fill(cmd);
            foreach (System.Data.DataRow row in dt.Rows)
            {
                result.Add(new ProgramDto(
                    Guid.Parse(row["PROGRAM_ID"].ToString()!),
                    scenarioId,
                    row["NAME"].ToString()!,
                    row["DESCRIPTION"]?.ToString(),
                    row["SAVE_DATE"] == DBNull.Value ? null : Convert.ToDateTime(row["SAVE_DATE"]),
                    Convert.ToInt32(row["START_YEAR"]),
                    Convert.ToInt32(row["UNIT_COUNT"]),
                    Convert.ToInt32(row["HAS_COST_REVENUE"]) == 1,
                    Convert.ToInt32(row["YEAR_COUNT"])));
            }
            return result;
        });
    }

    public async Task<Guid> CreateAsync(Guid scenarioId, string name, string description, int startYear, int numberOfYears)
    {
        return await Task.Run(() =>
        {
            var programId = Guid.NewGuid();
            using var conn = SnowflakeDb.OpenConnection(_connStr);

            string profileName = "Standard";
            using (var cmd = SnowflakeDb.CreateCommand(
                "SELECT NAME FROM PROFILES WHERE MODEL_ID = :model AND NAME = 'Standard' LIMIT 1", conn))
            {
                SnowflakeDb.AddParam(cmd, "model", scenarioId.ToString());
                var val = cmd.ExecuteScalar();
                if (val != null && val != DBNull.Value) profileName = val.ToString()!;
            }

            using (var cmd = SnowflakeDb.CreateCommand(
                "SELECT NAME FROM PROGRAM WHERE MODEL_ID = :Model AND NAME = :name", conn))
            {
                SnowflakeDb.AddParam(cmd, "Model", scenarioId.ToString());
                SnowflakeDb.AddParam(cmd, "name", name);
                var existing = cmd.ExecuteScalar();
                if (existing != null && existing != DBNull.Value)
                    throw new InvalidOperationException("A program with this name already exists in this scenario.");
            }

            const string insertSql =
                "INSERT INTO PROGRAM (MODEL_ID, PROGRAM_ID, NAME, REVCGS_CLUSTER, REVCGS_SELECT, REVCGS_CUSTOM, " +
                "REVDOM_SELECT, REVDOM_CUSTOM, REVINT_SELECT, REVINT_CUSTOM, CPH_SELECT, CPH_CUSTOM, " +
                "NFC_SELECT, NFC_CUSTOM, PROFILE, SAVE_DATE, DESCRIPTION, SCHOOL_SELECT, CAREER_SELECT, START_YEAR) " +
                "VALUES (:Model_ID, :Program_ID, :Name, 'Cluster 1', 'Custom', 0, 'Custom', 0, 'Custom', 0, " +
                "'Custom', 0, 'Custom', 0, :Profile, CURRENT_TIMESTAMP(), :Description, '', '', :Start_Year)";
            using (var cmd = SnowflakeDb.CreateCommand(insertSql, conn))
            {
                SnowflakeDb.AddParam(cmd, "Model_ID", scenarioId.ToString());
                SnowflakeDb.AddParam(cmd, "Program_ID", programId.ToString());
                SnowflakeDb.AddParam(cmd, "Name", name);
                SnowflakeDb.AddParam(cmd, "Profile", profileName);
                SnowflakeDb.AddParam(cmd, "Description", description);
                SnowflakeDb.AddParam(cmd, "Start_Year", startYear);
                cmd.ExecuteNonQuery();
            }

            const string settingsSql =
                "INSERT INTO PROGRAM_SETTINGS (PROGRAM_ID, YEAR, EFTSL_CGS, EFTSL_DOM, EFTSL_INT, FIXED_EXP, CPI, CPI_EXP) " +
                "VALUES (:Program_ID, :Year, 0, 0, 0, 0, 0, 0)";
            for (int i = 0; i < numberOfYears; i++)
            {
                using var cmd = SnowflakeDb.CreateCommand(settingsSql, conn);
                SnowflakeDb.AddParam(cmd, "Program_ID", programId.ToString());
                SnowflakeDb.AddParam(cmd, "Year", i);
                cmd.ExecuteNonQuery();
            }

            return programId;
        });
    }

    public async Task DeleteAsync(Guid scenarioId, Guid programId)
    {
        await Task.Run(() =>
        {
            using var conn = SnowflakeDb.OpenConnection(_connStr);
            using (var cmd = SnowflakeDb.CreateCommand(
                "DELETE FROM PROGRAM WHERE MODEL_ID = :Model AND PROGRAM_ID = :ProgramID", conn))
            {
                SnowflakeDb.AddParam(cmd, "Model", scenarioId.ToString());
                SnowflakeDb.AddParam(cmd, "ProgramID", programId.ToString());
                cmd.ExecuteNonQuery();
            }
            using (var cmd = SnowflakeDb.CreateCommand(
                "DELETE FROM PROGRAM_SETTINGS WHERE PROGRAM_ID = :ProgramID", conn))
            {
                SnowflakeDb.AddParam(cmd, "ProgramID", programId.ToString());
                cmd.ExecuteNonQuery();
            }
        });
    }

    public async Task SaveDetailsAsync(ProgramDetailsDto details)
    {
        await Task.Run(() =>
        {
            if (!IsConfigured) throw new InvalidOperationException("Snowflake connection not configured.");
            if (string.IsNullOrWhiteSpace(details.Name)) throw new InvalidOperationException("Program name is required.");

            using var conn = SnowflakeDb.OpenConnection(_connStr);
            const string updateSql =
                "UPDATE PROGRAM SET NAME = :Name, DESCRIPTION = :Description, START_YEAR = :StartYear, " +
                "REVCGS_CLUSTER = :RevCgsCluster, REVCGS_SELECT = :RevCgsSelect, REVCGS_CUSTOM = :RevCgsCustom, " +
                "REVDOM_SELECT = :RevDomSelect, REVDOM_CUSTOM = :RevDomCustom, " +
                "REVINT_SELECT = :RevIntSelect, REVINT_CUSTOM = :RevIntCustom, " +
                "CPH_SELECT = :CphSelect, CPH_CUSTOM = :CphCustom, NFC_SELECT = :NfcSelect, NFC_CUSTOM = :NfcCustom, " +
                "SCHOOL_SELECT = :SchoolSelect, CAREER_SELECT = :CareerSelect, " +
                "PROFILE = :Profile, SAVE_DATE = CURRENT_TIMESTAMP() WHERE PROGRAM_ID = :ProgramID";

            using (var cmd = SnowflakeDb.CreateCommand(updateSql, conn))
            {
                SnowflakeDb.AddParam(cmd, "Name", details.Name.Trim());
                SnowflakeDb.AddParam(cmd, "Description", details.Description ?? "");
                SnowflakeDb.AddParam(cmd, "StartYear", details.StartYear);
                SnowflakeDb.AddParam(cmd, "RevCgsCluster", details.RevCgsCluster);
                SnowflakeDb.AddParam(cmd, "RevCgsSelect", details.RevCgsSelect);
                SnowflakeDb.AddParam(cmd, "RevCgsCustom", details.RevCgsCustom);
                SnowflakeDb.AddParam(cmd, "RevDomSelect", details.RevDomSelect);
                SnowflakeDb.AddParam(cmd, "RevDomCustom", details.RevDomCustom);
                SnowflakeDb.AddParam(cmd, "RevIntSelect", details.RevIntSelect);
                SnowflakeDb.AddParam(cmd, "RevIntCustom", details.RevIntCustom);
                SnowflakeDb.AddParam(cmd, "CphSelect", details.CphSelect);
                SnowflakeDb.AddParam(cmd, "CphCustom", details.CphCustom);
                SnowflakeDb.AddParam(cmd, "NfcSelect", details.NfcSelect);
                SnowflakeDb.AddParam(cmd, "NfcCustom", details.NfcCustom);
                SnowflakeDb.AddParam(cmd, "SchoolSelect", details.SchoolSelect);
                SnowflakeDb.AddParam(cmd, "CareerSelect", details.CareerSelect);
                SnowflakeDb.AddParam(cmd, "Profile", details.Profile);
                SnowflakeDb.AddParam(cmd, "ProgramID", details.Id.ToString());
                cmd.ExecuteNonQuery();
            }

            Execute(conn, "DELETE FROM PROGRAM_SETTINGS WHERE PROGRAM_ID = :ProgramID", ("ProgramID", details.Id.ToString()));
            const string settingSql =
                "INSERT INTO PROGRAM_SETTINGS (PROGRAM_ID, YEAR, EFTSL_CGS, EFTSL_DOM, EFTSL_INT, FIXED_EXP, CPI, CPI_EXP) " +
                "VALUES (:ProgramID, :Year, :Cgs, :Dom, :Int, :Fixed, :Cpi, :CpiExp)";
            for (var i = 0; i < details.Settings.Count; i++)
            {
                var setting = details.Settings[i];
                setting.Year = i;
                using var cmd = SnowflakeDb.CreateCommand(settingSql, conn);
                SnowflakeDb.AddParam(cmd, "ProgramID", details.Id.ToString());
                SnowflakeDb.AddParam(cmd, "Year", setting.Year);
                SnowflakeDb.AddParam(cmd, "Cgs", setting.CgsEftsl);
                SnowflakeDb.AddParam(cmd, "Dom", setting.DomesticEftsl);
                SnowflakeDb.AddParam(cmd, "Int", setting.InternationalEftsl);
                SnowflakeDb.AddParam(cmd, "Fixed", setting.FixedExpense);
                SnowflakeDb.AddParam(cmd, "Cpi", setting.RevenueCpi);
                SnowflakeDb.AddParam(cmd, "CpiExp", setting.ExpenseCpi);
                cmd.ExecuteNonQuery();
            }

            Execute(conn, "DELETE FROM UNITS WHERE PROGRAM_ID = :ProgramID", ("ProgramID", details.Id.ToString()));
            const string unitSql =
                "INSERT INTO UNITS (PROGRAM_ID, UNIT, PROFILE, MODEL_ID, PROGRAM_YEAR, " +
                "CUSTOM_TUT_SIZE, CUSTOM_LECT_HOURS, CUSTOM_TUT_HOURS, CUSTOM_PREP_HOURS, CUSTOM_ADMIN_HOURS, CUSTOM_STUDENT_HOURS) " +
                "VALUES (:ProgramID, :Unit, :Profile, :ModelID, :ProgramYear, " +
                ":CustomTutSize, :CustomLectHours, :CustomTutHours, :CustomPrepHours, :CustomAdminHours, :CustomStudentHours)";
            foreach (var unit in details.Units.Where(u => !string.IsNullOrWhiteSpace(u.Unit) && !string.IsNullOrWhiteSpace(u.Profile)))
            {
                using var cmd = SnowflakeDb.CreateCommand(unitSql, conn);
                SnowflakeDb.AddParam(cmd, "ProgramID", details.Id.ToString());
                SnowflakeDb.AddParam(cmd, "Unit", unit.Unit);
                SnowflakeDb.AddParam(cmd, "Profile", unit.Profile);
                SnowflakeDb.AddParam(cmd, "ModelID", details.ScenarioId.ToString());
                SnowflakeDb.AddParam(cmd, "ProgramYear", unit.ProgramYear);
                SnowflakeDb.AddParam(cmd, "CustomTutSize", (object?)unit.CustomTutSize ?? DBNull.Value);
                SnowflakeDb.AddParam(cmd, "CustomLectHours", (object?)unit.CustomLectHours ?? DBNull.Value);
                SnowflakeDb.AddParam(cmd, "CustomTutHours", (object?)unit.CustomTutHours ?? DBNull.Value);
                SnowflakeDb.AddParam(cmd, "CustomPrepHours", (object?)unit.CustomPrepHours ?? DBNull.Value);
                SnowflakeDb.AddParam(cmd, "CustomAdminHours", (object?)unit.CustomAdminHours ?? DBNull.Value);
                SnowflakeDb.AddParam(cmd, "CustomStudentHours", (object?)unit.CustomStudentHours ?? DBNull.Value);
                cmd.ExecuteNonQuery();
            }
        });
    }

    private static List<ProgramYearSettingDto> GetSettings(SnowflakeDbConnection conn, Guid programId)
    {
        var result = new List<ProgramYearSettingDto>();
        using var cmd = SnowflakeDb.CreateCommand(
            "SELECT YEAR, EFTSL_CGS, EFTSL_DOM, EFTSL_INT, FIXED_EXP, CPI, CPI_EXP FROM PROGRAM_SETTINGS WHERE PROGRAM_ID = :ProgramID ORDER BY YEAR", conn);
        SnowflakeDb.AddParam(cmd, "ProgramID", programId.ToString());
        var dt = SnowflakeDb.Fill(cmd);
        foreach (DataRow row in dt.Rows)
        {
            result.Add(new ProgramYearSettingDto
            {
                Year = GetInt(row, "YEAR"),
                CgsEftsl = GetDouble(row, "EFTSL_CGS"),
                DomesticEftsl = GetDouble(row, "EFTSL_DOM"),
                InternationalEftsl = GetDouble(row, "EFTSL_INT"),
                FixedExpense = GetDouble(row, "FIXED_EXP"),
                RevenueCpi = GetDouble(row, "CPI"),
                ExpenseCpi = GetDouble(row, "CPI_EXP")
            });
        }
        return result;
    }

    private static List<ProgramUnitDto> GetUnits(SnowflakeDbConnection conn, Guid programId)
    {
        var result = new List<ProgramUnitDto>();
        using var cmd = SnowflakeDb.CreateCommand(
            "SELECT UNIT, PROFILE, COALESCE(PROGRAM_YEAR, 1) AS PROGRAM_YEAR, " +
            "CUSTOM_TUT_SIZE, CUSTOM_LECT_HOURS, CUSTOM_TUT_HOURS, CUSTOM_PREP_HOURS, CUSTOM_ADMIN_HOURS, CUSTOM_STUDENT_HOURS " +
            "FROM UNITS WHERE PROGRAM_ID = :ProgramID ORDER BY PROGRAM_YEAR, UNIT", conn);
        SnowflakeDb.AddParam(cmd, "ProgramID", programId.ToString());
        var dt = SnowflakeDb.Fill(cmd);
        foreach (DataRow row in dt.Rows)
        {
            result.Add(new ProgramUnitDto
            {
                Unit = GetString(row, "UNIT"),
                Profile = GetString(row, "PROFILE"),
                ProgramYear = GetInt(row, "PROGRAM_YEAR", 1),
                CustomTutSize = GetDoubleNullable(row, "CUSTOM_TUT_SIZE"),
                CustomLectHours = GetDoubleNullable(row, "CUSTOM_LECT_HOURS"),
                CustomTutHours = GetDoubleNullable(row, "CUSTOM_TUT_HOURS"),
                CustomPrepHours = GetDoubleNullable(row, "CUSTOM_PREP_HOURS"),
                CustomAdminHours = GetDoubleNullable(row, "CUSTOM_ADMIN_HOURS"),
                CustomStudentHours = GetDoubleNullable(row, "CUSTOM_STUDENT_HOURS"),
            });
        }
        return result;
    }

    private static List<ProgramProfileDto> GetProfiles(SnowflakeDbConnection conn, Guid scenarioId)
    {
        var result = new List<ProgramProfileDto>();
        using var cmd = SnowflakeDb.CreateCommand(
            "SELECT DISTINCT NAME, TUT_SIZE, LECT_HOURS, TUT_HOURS, PREP_HOURS, ADMIN_HOURS, STUDENT_HOURS FROM PROFILES WHERE MODEL_ID = :Model ORDER BY NAME", conn);
        SnowflakeDb.AddParam(cmd, "Model", scenarioId.ToString());
        var dt = SnowflakeDb.Fill(cmd);
        foreach (DataRow row in dt.Rows)
        {
            result.Add(new ProgramProfileDto
            {
                Name = GetString(row, "NAME"),
                TutSize = GetDouble(row, "TUT_SIZE"),
                LectureHours = GetDouble(row, "LECT_HOURS"),
                TutorialHours = GetDouble(row, "TUT_HOURS"),
                PreparationHours = GetDouble(row, "PREP_HOURS"),
                AdministrationHours = GetDouble(row, "ADMIN_HOURS"),
                StudentHours = GetDouble(row, "STUDENT_HOURS")
            });
        }
        return result;
    }

    private static List<ProgramUnitOptionDto> GetUnitOptions(SnowflakeDbConnection conn, Guid scenarioId, string sourceDb)
    {
        var result = new List<ProgramUnitOptionDto>();
        if (string.IsNullOrWhiteSpace(sourceDb)) return result;
        try
        {
            var view = $"V_UNIT_VALUES_{SafeId(scenarioId)}";
            using var cmd = SnowflakeDb.CreateCommand(
                $"SELECT DISTINCT Unit_Name AS UNIT_NAME, School AS SCHOOL FROM {view} ORDER BY Unit_Name", conn);
            var dt = SnowflakeDb.Fill(cmd);
            foreach (DataRow row in dt.Rows)
                result.Add(new ProgramUnitOptionDto { Name = GetString(row, "UNIT_NAME"), School = GetString(row, "SCHOOL") });
        }
        catch { /* view not yet created */ }
        return result;
    }

    private static List<ProgramUnitValueDto> GetUnitValues(SnowflakeDbConnection conn, Guid scenarioId, string sourceDb)
    {
        var result = new List<ProgramUnitValueDto>();
        if (string.IsNullOrWhiteSpace(sourceDb)) return result;
        try
        {
            var view = $"V_UNIT_VALUES_{SafeId(scenarioId)}";
            using var cmd = SnowflakeDb.CreateCommand(
                $"SELECT Unit_Name AS UNIT_NAME, Exp_Fac AS EXP_FAC, Exp_NonFac AS EXP_NONFAC, " +
                $"EFTSL_Dom AS EFTSL_DOM, EFTSL_Int AS EFTSL_INT, EFTSL_CGS, " +
                $"Delivery_Hours AS DELIVERY_HOURS, RevDom AS REVDOM, RevInt AS REVINT, RevCGS AS REVCGS " +
                $"FROM {view}", conn);
            var dt = SnowflakeDb.Fill(cmd);
            foreach (DataRow row in dt.Rows)
                result.Add(new ProgramUnitValueDto
                {
                    UnitName = GetString(row, "UNIT_NAME"),
                    FacultyExpense = GetDouble(row, "EXP_FAC"),
                    NonFacultyExpense = GetDouble(row, "EXP_NONFAC"),
                    DomesticEftsl = GetDouble(row, "EFTSL_DOM"),
                    InternationalEftsl = GetDouble(row, "EFTSL_INT"),
                    CgsEftsl = GetDouble(row, "EFTSL_CGS"),
                    DomesticRevenue = GetDouble(row, "REVDOM"),
                    InternationalRevenue = GetDouble(row, "REVINT"),
                    CgsRevenue = GetDouble(row, "REVCGS"),
                    DeliveryHours = GetDouble(row, "DELIVERY_HOURS")
                });
        }
        catch { /* view not yet created */ }
        return result;
    }

    private static List<ModelValueDto> GetModelValues(SnowflakeDbConnection conn, Guid scenarioId, string sourceDb)
    {
        var result = new List<ModelValueDto>();
        if (string.IsNullOrWhiteSpace(sourceDb)) return result;
        try
        {
            var view = $"V_COURSE_VALUES_{SafeId(scenarioId)}";
            using var cmd = SnowflakeDb.CreateCommand(
                $"SELECT School AS SCHOOL, Career AS CAREER, Course_Name AS COURSE_NAME, " +
                $"Exp_Fac AS EXP_FAC, Exp_NonFac AS EXP_NONFAC, " +
                $"EFTSL_Dom AS EFTSL_DOM, EFTSL_Int AS EFTSL_INT, EFTSL_CGS, " +
                $"Delivery_Hours AS DELIVERY_HOURS, RevDom AS REVDOM, RevInt AS REVINT, RevCGS AS REVCGS " +
                $"FROM {view}", conn);
            var dt = SnowflakeDb.Fill(cmd);
            foreach (DataRow row in dt.Rows)
                result.Add(new ModelValueDto
                {
                    School = GetString(row, "SCHOOL"),
                    Career = GetString(row, "CAREER"),
                    CourseName = GetString(row, "COURSE_NAME"),
                    FacultyExpense = GetDouble(row, "EXP_FAC"),
                    NonFacultyExpense = GetDouble(row, "EXP_NONFAC"),
                    DomesticEftsl = GetDouble(row, "EFTSL_DOM"),
                    InternationalEftsl = GetDouble(row, "EFTSL_INT"),
                    CgsEftsl = GetDouble(row, "EFTSL_CGS"),
                    DomesticRevenue = GetDouble(row, "REVDOM"),
                    InternationalRevenue = GetDouble(row, "REVINT"),
                    CgsRevenue = GetDouble(row, "REVCGS"),
                    DeliveryHours = GetDouble(row, "DELIVERY_HOURS")
                });
        }
        catch { /* view not yet created */ }
        return result;
    }

    private static string SafeId(Guid id) => id.ToString().Replace("-", "_");

    private static List<ConstantOptionDto> GetConstants(SnowflakeDbConnection conn, string type)
    {
        var result = new List<ConstantOptionDto>();
        using var cmd = SnowflakeDb.CreateCommand("SELECT NAME, VALUE1 FROM CONSTANTS WHERE TYPE = :Type ORDER BY NAME", conn);
        SnowflakeDb.AddParam(cmd, "Type", type);
        var dt = SnowflakeDb.Fill(cmd);
        foreach (DataRow row in dt.Rows)
        {
            result.Add(new ConstantOptionDto
            {
                Name = GetString(row, "NAME"),
                Value = GetDouble(row, "VALUE1")
            });
        }
        return result;
    }

    private static void Execute(SnowflakeDbConnection conn, string sql, params (string Name, object Value)[] parameters)
    {
        using var cmd = SnowflakeDb.CreateCommand(sql, conn);
        foreach (var (name, value) in parameters)
            SnowflakeDb.AddParam(cmd, name, value);
        cmd.ExecuteNonQuery();
    }

    private static string GetString(DataRow row, string columnName, string defaultValue = "")
    {
        if (!row.Table.Columns.Contains(columnName)) return defaultValue;
        var value = row[columnName];
        return value == DBNull.Value ? defaultValue : value.ToString() ?? defaultValue;
    }

    private static double GetDouble(DataRow row, string columnName, double defaultValue = 0)
    {
        if (!row.Table.Columns.Contains(columnName)) return defaultValue;
        var value = row[columnName];
        return value == DBNull.Value ? defaultValue : Convert.ToDouble(value);
    }

    private static int GetInt(DataRow row, string columnName, int defaultValue = 0)
    {
        if (!row.Table.Columns.Contains(columnName)) return defaultValue;
        var value = row[columnName];
        return value == DBNull.Value ? defaultValue : Convert.ToInt32(value);
    }

    private static Guid GetGuid(DataRow row, string columnName)
    {
        var value = GetString(row, columnName);
        return Guid.TryParse(value, out var id) ? id : Guid.Empty;
    }

    private static DateTime? GetDate(DataRow row, string columnName)
    {
        if (!row.Table.Columns.Contains(columnName) || row[columnName] == DBNull.Value) return null;
        return Convert.ToDateTime(row[columnName]);
    }

    private static double? GetDoubleNullable(DataRow row, string columnName)
    {
        if (!row.Table.Columns.Contains(columnName)) return null;
        var value = row[columnName];
        return value == DBNull.Value ? null : Convert.ToDouble(value);
    }
}
