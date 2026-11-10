using Microsoft.Extensions.Options;
using ProgramInsights.Models;
using ProgramInsights.Services.Connections;
using System.Text;
using System.Text.RegularExpressions;

namespace ProgramInsights.Services;

public class CortexService
{
    private readonly ISnowflakeConnectionFactory _db;
    private readonly SnowflakeOptions _opts;

    public CortexService(ISnowflakeConnectionFactory db, IOptions<SnowflakeOptions> opts)
    {
        _db = db;
        _opts = opts.Value;
    }

    public async Task<string> GenerateBusinessCaseAsync(
        ProgramDetailsDto program,
        ProgramCalculationResultDto result,
        string model = "mistral-large2",
        CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(SnowflakeScope.Tenant, ct);
        return await Task.Run(() =>
        {
            var prompt = BuildPrompt(program, result);

            using var cmd = SnowflakeDb.CreateCommand(
                "SELECT SNOWFLAKE.CORTEX.COMPLETE(:Model, :Prompt) AS RESPONSE", conn);
            // Cortex completions routinely run 30-60s, well past the driver's default
            // command timeout.
            cmd.CommandTimeout = _opts.CommandTimeoutSeconds;
            SnowflakeDb.AddParam(cmd, "Model", model);
            SnowflakeDb.AddParam(cmd, "Prompt", prompt);

            var response = cmd.ExecuteScalar();
            return response?.ToString() ?? "";
        }, ct);
    }

    private static string BuildPrompt(ProgramDetailsDto program, ProgramCalculationResultDto result)
    {
        var sb = new StringBuilder();

        sb.AppendLine("You are a senior financial analyst specialising in Australian higher education program planning.");
        sb.AppendLine("Based on the program projection data below, produce a professional financial business case.");
        sb.AppendLine("Be specific, data-driven, and use Australian currency (AUD). Format all dollar values with $ and commas.");
        sb.AppendLine();

        // Program header
        sb.AppendLine($"# Program: {program.Name}");
        sb.AppendLine($"Scenario: {program.ScenarioName}");
        sb.AppendLine($"Start Year: {program.StartYear}");
        sb.AppendLine($"Projection Period: {result.CostResults.Count} year(s)");
        sb.AppendLine($"Total Revenue (all years): {result.CostResults.Sum(r => r.FacultyRevenue):C0}");
        sb.AppendLine($"Total Cost (all years): {result.CostResults.Sum(r => r.FacultyCost + r.NonFacultyCost):C0}");
        sb.AppendLine($"Total Margin (all years): {result.CostResults.Sum(r => r.TotalMargin):C0}");
        sb.AppendLine();

        // Year-by-year table
        sb.AppendLine("## Year-by-Year Financial Projection");
        sb.AppendLine("Year | EFTSL | Revenue | Fac. Cost | NFC | Total Cost | Margin | Margin %");
        sb.AppendLine("-----|-------|---------|-----------|-----|------------|--------|--------");
        foreach (var row in result.CostResults)
        {
            var totalCost = row.FacultyCost + row.NonFacultyCost;
            sb.AppendLine(
                $"{row.CalendarYear} | {row.TotalEftsl:F1} | {row.FacultyRevenue:C0} | " +
                $"{row.FacultyCost:C0} | {row.NonFacultyCost:C0} | {totalCost:C0} | " +
                $"{row.TotalMargin:C0} | {row.TotalMarginPercent:P1}");
        }
        sb.AppendLine();

        // Breakeven
        sb.AppendLine("## Breakeven Analysis");
        sb.AppendLine("Year | Projected EFTSL | Breakeven EFTSL | Surplus EFTSL | Achievable?");
        sb.AppendLine("-----|----------------|-----------------|---------------|------------");
        foreach (var be in result.BreakevenResults)
        {
            sb.AppendLine(
                $"{be.CalendarYear} | {be.ProjectedEftsl:F1} | {be.BreakevenEftsl:F1} | " +
                $"{be.EftslSurplus:F1} | {(be.IsBreakevenPossible ? "Yes" : "No")}");
        }
        sb.AppendLine();

        // Revenue config
        sb.AppendLine("## Revenue & Cost Configuration");
        sb.AppendLine($"- Domestic revenue source: {program.RevDomSelect} ({(program.RevDomSelect == "Custom" ? $"{program.RevDomCustom:C0}/EFTSL" : "model-derived")})");
        sb.AppendLine($"- International revenue source: {program.RevIntSelect} ({(program.RevIntSelect == "Custom" ? $"{program.RevIntCustom:C0}/EFTSL" : "model-derived")})");
        sb.AppendLine($"- CGS revenue source: {program.RevCgsSelect}");
        sb.AppendLine($"- Faculty cost source: {program.CphSelect} ({program.CphSelect switch { "Custom" => $"{program.CphCustom:C0}/hr", "Benchmark" => $"sector weighted mean, {program.BmCphValue:C0}/hr", _ => "model-derived" }})");
        sb.AppendLine($"- Non-faculty cost source: {program.NfcSelect} ({program.NfcSelect switch { "Custom" => $"{program.NfcCustom:C0}/EFTSL", "Benchmark" => $"sector weighted mean, {program.BmNfcValue:C0}/EFTSL", _ => "model-derived" }})");
        sb.AppendLine();

        // EFTSL ramp
        if (program.Settings.Count > 0)
        {
            sb.AppendLine("## Commencing EFTSL Ramp (planned intakes)");
            foreach (var (s, i) in program.Settings.Select((s, i) => (s, i)))
            {
                var total = s.DomesticEftsl + s.InternationalEftsl + s.CgsEftsl;
                sb.AppendLine($"Year {program.StartYear + i}: {total:F1} EFTSL total " +
                    $"(Dom {s.DomesticEftsl:F1}, Int {s.InternationalEftsl:F1}, CGS {s.CgsEftsl:F1})");
            }
            sb.AppendLine();
        }

        // Units
        if (program.Units.Count > 0)
        {
            sb.AppendLine("## Program Structure");
            foreach (var grp in program.Units.GroupBy(u => u.ProgramYear).OrderBy(g => g.Key))
            {
                sb.AppendLine($"Study Year {grp.Key}: {string.Join(", ", grp.Select(u => u.Unit))}");
            }
            sb.AppendLine();
        }

        // Instruction
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine("Produce the following sections. Use clear headings and professional language:");
        sb.AppendLine();
        sb.AppendLine("**1. Executive Summary**");
        sb.AppendLine("2-3 paragraphs summarising the program's financial viability, key metrics, and overall recommendation.");
        sb.AppendLine();
        sb.AppendLine("**2. Financial Performance Analysis**");
        sb.AppendLine("Year-by-year narrative of revenue, cost, and margin trajectory. Highlight any inflection points.");
        sb.AppendLine();
        sb.AppendLine("**3. Revenue Analysis**");
        sb.AppendLine("Analyse the student load mix, revenue per EFTSL assumptions, and cumulative revenue outlook.");
        sb.AppendLine();
        sb.AppendLine("**4. Cost Analysis**");
        sb.AppendLine("Break down faculty vs non-faculty cost drivers and their proportional impact on margins.");
        sb.AppendLine();
        sb.AppendLine("**5. Breakeven Analysis**");
        sb.AppendLine("Explain when and whether the program achieves breakeven, the EFTSL headroom/deficit, and implications.");
        sb.AppendLine();
        sb.AppendLine("**6. Key Risks & Sensitivities**");
        sb.AppendLine("Identify the top 3-4 financial risks (e.g. enrolment shortfall, cost escalation) and their impact.");
        sb.AppendLine();
        sb.AppendLine("**7. Recommendation**");
        sb.AppendLine("A clear Go / Conditional Go / No-Go recommendation with supporting rationale from the data.");

        return sb.ToString();
    }

    // Lightweight markdown → HTML for rendering Cortex responses.
    public static Microsoft.AspNetCore.Components.MarkupString RenderMarkdown(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new Microsoft.AspNetCore.Components.MarkupString("");

        var html = System.Net.WebUtility.HtmlEncode(text);

        // Headers (process longer patterns first)
        html = Regex.Replace(html, @"^### (.+)$", "<h4 class='intel-h4'>$1</h4>", RegexOptions.Multiline);
        html = Regex.Replace(html, @"^## (.+)$", "<h3 class='intel-h3'>$1</h3>", RegexOptions.Multiline);
        html = Regex.Replace(html, @"^# (.+)$", "<h2 class='intel-h2'>$1</h2>", RegexOptions.Multiline);

        // Bold
        html = Regex.Replace(html, @"\*\*(.+?)\*\*", "<strong>$1</strong>");

        // Horizontal rule
        html = Regex.Replace(html, @"^---$", "<hr class='intel-rule'>", RegexOptions.Multiline);

        // Numbered list
        html = Regex.Replace(html, @"^(\d+)\. (.+)$", "<div class='intel-li'><span class='intel-li-num'>$1.</span> $2</div>", RegexOptions.Multiline);

        // Bullet list
        html = Regex.Replace(html, @"^[-*] (.+)$", "<div class='intel-li'>• $1</div>", RegexOptions.Multiline);

        // Paragraphs — blank lines become paragraph breaks
        html = Regex.Replace(html, @"\n\n+", "</p><p class='intel-p'>", RegexOptions.Multiline);
        html = "<p class='intel-p'>" + html + "</p>";

        // Remaining newlines inside paragraphs
        html = html.Replace("\n", "<br>");

        return new Microsoft.AspNetCore.Components.MarkupString(html);
    }
}
