using Snowflake.Data.Client;
using System.Data;
using System.Text.RegularExpressions;

namespace ProgramInsights.Services;

/// <summary>
/// SQL helpers shared by the data services. Pure translation and parameter plumbing —
/// it deliberately does NOT open connections.
///
/// Connections come from <see cref="Connections.ISnowflakeConnectionFactory"/>, because
/// under per-user OAuth every connection needs a token acquired at that moment and a
/// database resolved from the signed-in user's Snowflake role. A static
/// OpenConnection(connectionString) has no way to do either, so it was removed rather
/// than left as a trap.
/// </summary>
public static class SnowflakeDb
{
    public static SnowflakeDbCommand CreateCommand(string sql, SnowflakeDbConnection conn)
    {
        var cmd = new SnowflakeDbCommand(conn)
        {
            CommandText = TranslateSql(sql),
            CommandType = CommandType.Text
        };
        return cmd;
    }

    public static void AddParam(SnowflakeDbCommand cmd, string name, object? value)
    {
        var p = new SnowflakeDbParameter
        {
            ParameterName = name.TrimStart('@'),
            Value = value ?? DBNull.Value
        };
        cmd.Parameters.Add(p);
    }

    public static DataTable Fill(SnowflakeDbCommand cmd)
    {
        var dt = new DataTable();
        using var reader = cmd.ExecuteReader();
        dt.Load(reader);
        return dt;
    }

    // Translates SQL Server syntax to Snowflake syntax (mirrors SfDb.vb TranslateSql)
    private static string TranslateSql(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql)) return sql;
        sql = Regex.Replace(sql, @"@(\w+)", ":$1");
        sql = Regex.Replace(sql, @"\bGETDATE\s*\(\s*\)", "CURRENT_TIMESTAMP()", RegexOptions.IgnoreCase);
        sql = Regex.Replace(sql, @"\bISNULL\s*\(", "COALESCE(", RegexOptions.IgnoreCase);
        var topMatch = Regex.Match(sql, @"SELECT\s+TOP\s+(\d+)\s+", RegexOptions.IgnoreCase);
        if (topMatch.Success)
        {
            var n = topMatch.Groups[1].Value;
            sql = Regex.Replace(sql, @"SELECT\s+TOP\s+\d+\s+", "SELECT ", RegexOptions.IgnoreCase);
            sql = sql.TrimEnd().TrimEnd(';') + $" LIMIT {n}";
        }
        sql = Regex.Replace(sql, @"\[dbo\]\.", "", RegexOptions.IgnoreCase);
        sql = Regex.Replace(sql, @"\[(\w+)\]", "$1");
        return sql;
    }
}
