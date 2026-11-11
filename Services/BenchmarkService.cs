using ProgramInsights.Models;
using ProgramInsights.Services.Connections;
using Snowflake.Data.Client;
using System.Data;

namespace ProgramInsights.Services;

public class BenchmarkService
{
    private readonly ISnowflakeConnectionFactory _db;
    private readonly ILogger<BenchmarkService> _log;

    // Shared, cross-database sector benchmark data — deliberately fully qualified, so it
    // does not follow the connection's per-client default database. Each client role
    // needs SELECT on it; if the grant is missing the panels degrade to empty and the
    // capability flag reports why.
    private const string Table = "BENCHMARK_APP.CLIENT.BM_FOE4_SECTOR_AGG_BASE";

    // Key measures used for cost/revenue source calculations
    public const string MeasureStaffSalaryPerEftsl = "Staff Salary per EFTSL";
    public const string MeasureDeliveryHoursPerEftsl = "Delivery Hours per EFTSL";
    public const string MeasureNonSalaryPerEftsl = "Non Salary per EFTSL";
    public const string MeasureCostPerEftsl = "CostPerEFTSL";
    public const string MeasureMarginPerEftsl = "Margin per EFTSL";
    public const string MeasureTotalRevPerEftsl = "Total Survey Rev per EFTSL";

    public BenchmarkService(ISnowflakeConnectionFactory db, ILogger<BenchmarkService> log)
    {
        _db = db;
        _log = log;
    }

    /// <summary>
    /// Returns distinct values for the three peer-group filter dropdowns.
    /// </summary>
    public async Task<BenchmarkFilterOptionsDto> GetFilterOptionsAsync(CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(SnowflakeScope.Shared, ct);
        return await Task.Run(() =>
        {
            var result = new BenchmarkFilterOptionsDto();
            try
            {
                result.DegreeLevels = GetDistinct(conn, "\"Degree Level\"");
                result.FoeCodes = GetDistinct(conn, "FOE");
                result.PeriodNames = GetDistinct(conn, "\"Period Name\"");
            }
            catch (SnowflakeDbException ex)
            {
                _log.LogWarning(ex, "Benchmark filter options unavailable from {Table}", Table);
            }
            return result;
        }, ct);
    }

    /// <summary>
    /// Returns benchmark measures for the given degree level, FOE, and period.
    /// Omit (empty string) any filter to leave it unfiltered.
    /// </summary>
    public async Task<List<BenchmarkMeasureDto>> GetMeasuresAsync(
        string degreeLevel, string foe, string periodName, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(SnowflakeScope.Shared, ct);
        return await Task.Run(() =>
        {
            var result = new List<BenchmarkMeasureDto>();
            try
            {
                var conditions = new List<string>();
                var parameters = new List<(string Name, object Value)>();

                void AddFilter(string col, string paramName, string val)
                {
                    if (!string.IsNullOrWhiteSpace(val))
                    {
                        conditions.Add($"{col} = :{paramName}");
                        parameters.Add((paramName, val));
                    }
                }

                AddFilter("\"Degree Level\"", "degreeLevel", degreeLevel);
                AddFilter("FOE", "foe", foe);
                AddFilter("\"Period Name\"", "periodName", periodName);

                var where = conditions.Count > 0
                    ? "WHERE " + string.Join(" AND ", conditions)
                    : "";

                var sql = $"SELECT \"Measure\", \"Min\", \"Max\", \"Weighted Mean\", \"Median\", " +
                          $"\"Benchmark N\", \"Confidence Benchmark N\", \"Confidence\" " +
                          $"FROM {Table} {where} ORDER BY \"Measure\"";

                using var cmd = SnowflakeDb.CreateCommand(sql, conn);
                foreach (var (name, value) in parameters)
                    SnowflakeDb.AddParam(cmd, name, value);

                var dt = SnowflakeDb.Fill(cmd);
                foreach (DataRow row in dt.Rows)
                    result.Add(new BenchmarkMeasureDto
                    {
                        Measure = GetString(row, "Measure"),
                        Min = GetDoubleNullable(row, "Min"),
                        Max = GetDoubleNullable(row, "Max"),
                        WeightedMean = GetDoubleNullable(row, "Weighted Mean"),
                        Median = GetDoubleNullable(row, "Median"),
                        BenchmarkN = GetInt(row, "Benchmark N"),
                        ConfidenceBenchmarkN = GetInt(row, "Confidence Benchmark N"),
                        Confidence = GetString(row, "Confidence")
                    });
            }
            catch (SnowflakeDbException ex)
            {
                _log.LogWarning(ex, "Benchmark measures unavailable from {Table}", Table);
            }
            return result;
        }, ct);
    }

    // ── private helpers ───────────────────────────────────────────────────────

    private List<string> GetDistinct(SnowflakeDbConnection conn, string column)
    {
        var result = new List<string>();
        try
        {
            using var cmd = SnowflakeDb.CreateCommand(
                $"SELECT DISTINCT {column} FROM {Table} WHERE {column} IS NOT NULL ORDER BY {column}", conn);
            var dt = SnowflakeDb.Fill(cmd);
            foreach (DataRow row in dt.Rows)
            {
                var val = row[0]?.ToString();
                if (!string.IsNullOrWhiteSpace(val))
                    result.Add(val);
            }
        }
        catch (SnowflakeDbException ex)
        {
            _log.LogWarning(ex, "Distinct values for {Column} unavailable from {Table}", column, Table);
        }
        return result;
    }

    private static string GetString(DataRow row, string col, string def = "")
    {
        if (!row.Table.Columns.Contains(col)) return def;
        return row[col] == DBNull.Value ? def : row[col].ToString() ?? def;
    }

    private static double? GetDoubleNullable(DataRow row, string col)
    {
        if (!row.Table.Columns.Contains(col) || row[col] == DBNull.Value) return null;
        try { return Convert.ToDouble(row[col]); } catch { return null; }
    }

    private static int GetInt(DataRow row, string col, int def = 0)
    {
        if (!row.Table.Columns.Contains(col) || row[col] == DBNull.Value) return def;
        try { return Convert.ToInt32(row[col]); } catch { return def; }
    }
}
