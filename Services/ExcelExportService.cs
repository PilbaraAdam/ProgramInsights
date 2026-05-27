using ClosedXML.Excel;
using ProgramInsights.Models;

namespace ProgramInsights.Services;

public class ExcelExportService
{
    public byte[] GenerateResultsWorkbook(ProgramCalculationResultDto result, ProgramDetailsDto program)
    {
        using var wb = new XLWorkbook();

        AddSummarySheet(wb, result, program);
        AddEftslRampupSheet(wb, result, program);
        AddUnitHoursSheet(wb, result, program);

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    private static void AddSummarySheet(XLWorkbook wb, ProgramCalculationResultDto result, ProgramDetailsDto program)
    {
        var ws = wb.Worksheets.Add("Summary");

        string[] headers = ["Year", "Total EFTSL", "Teaching Hours", "Faculty Cost", "Faculty Revenue",
            "Faculty Margin", "Faculty Margin %", "Non-Faculty Cost", "Total Margin", "Total Margin %"];

        for (var c = 0; c < headers.Length; c++)
            ws.Cell(1, c + 1).Value = headers[c];

        StyleHeaderRow(ws.Row(1));

        for (var r = 0; r < result.CostResults.Count; r++)
        {
            var row = result.CostResults[r];
            var exRow = r + 2;
            ws.Cell(exRow, 1).Value = row.CalendarYear;
            ws.Cell(exRow, 2).Value = row.TotalEftsl;
            ws.Cell(exRow, 3).Value = row.TotalTeachingHours;
            ws.Cell(exRow, 4).Value = row.FacultyCost;
            ws.Cell(exRow, 5).Value = row.FacultyRevenue;
            ws.Cell(exRow, 6).Value = row.FacultyMargin;
            ws.Cell(exRow, 7).Value = row.FacultyMarginPercent;
            ws.Cell(exRow, 8).Value = row.NonFacultyCost;
            ws.Cell(exRow, 9).Value = row.TotalMargin;
            ws.Cell(exRow, 10).Value = row.TotalMarginPercent;

            FormatCurrencyCells(ws, exRow, [4, 5, 6, 8, 9]);
            FormatPercentCells(ws, exRow, [7, 10]);
            FormatNumberCells(ws, exRow, [2, 3]);
        }

        ws.Columns().AdjustToContents();
    }

    private static void AddEftslRampupSheet(XLWorkbook wb, ProgramCalculationResultDto result, ProgramDetailsDto program)
    {
        var ws = wb.Worksheets.Add("EFTSL Ramp-up");

        var forecastYears = result.CostResults.OrderBy(c => c.YearIndex).ToList();
        var cohortStartIndices = result.HourResults
            .Select(h => h.CohortStartYearIndex)
            .Distinct()
            .OrderBy(i => i)
            .ToList();

        ws.Cell(1, 1).Value = "Forecast Year";
        for (var c = 0; c < cohortStartIndices.Count; c++)
        {
            var intakeYear = program.StartYear + cohortStartIndices[c];
            ws.Cell(1, c + 2).Value = $"Intake {intakeYear}";
        }
        ws.Cell(1, cohortStartIndices.Count + 2).Value = "Total EFTSL";

        StyleHeaderRow(ws.Row(1));

        for (var r = 0; r < forecastYears.Count; r++)
        {
            var fy = forecastYears[r];
            var exRow = r + 2;
            ws.Cell(exRow, 1).Value = fy.CalendarYear;

            for (var c = 0; c < cohortStartIndices.Count; c++)
            {
                var cohortIdx = cohortStartIndices[c];
                var eftsl = result.HourResults
                    .Where(h => h.ForecastYearIndex == fy.YearIndex && h.CohortStartYearIndex == cohortIdx)
                    .Sum(h => h.DomesticEftsl + h.InternationalEftsl + h.CgsEftsl);

                var studyYear = fy.YearIndex - cohortIdx + 1;
                var label = eftsl > 0 ? $"{eftsl:N2} (Yr{studyYear})" : "—";
                ws.Cell(exRow, c + 2).Value = label;
            }

            ws.Cell(exRow, cohortStartIndices.Count + 2).Value = fy.TotalEftsl;
            ws.Cell(exRow, cohortStartIndices.Count + 2).Style.NumberFormat.Format = "#,##0.00";
        }

        ws.Columns().AdjustToContents();
    }

    private static void AddUnitHoursSheet(XLWorkbook wb, ProgramCalculationResultDto result, ProgramDetailsDto program)
    {
        var ws = wb.Worksheets.Add("Unit Detail");

        string[] headers = ["Forecast Year", "Intake Cohort", "Study Year", "Unit", "Profile",
            "Dom EFTSL", "Int EFTSL", "CGS EFTSL", "Students/Unit",
            "New Unit Hours", "Student Hours", "Total Hours",
            "Revenue", "Faculty Cost", "Non-Fac Cost", "Total Cost", "Margin", "Margin %"];

        for (var c = 0; c < headers.Length; c++)
            ws.Cell(1, c + 1).Value = headers[c];

        StyleHeaderRow(ws.Row(1));

        var rows = result.HourResults
            .OrderBy(h => h.ForecastYearIndex)
            .ThenBy(h => h.CohortStartYearIndex)
            .ThenBy(h => h.Unit)
            .ToList();

        for (var r = 0; r < rows.Count; r++)
        {
            var h = rows[r];
            var exRow = r + 2;
            ws.Cell(exRow, 1).Value = program.StartYear + h.ForecastYearIndex;
            ws.Cell(exRow, 2).Value = program.StartYear + h.CohortStartYearIndex;
            ws.Cell(exRow, 3).Value = h.StudyYear;
            ws.Cell(exRow, 4).Value = h.Unit;
            ws.Cell(exRow, 5).Value = h.Profile;
            ws.Cell(exRow, 6).Value = h.DomesticEftsl;
            ws.Cell(exRow, 7).Value = h.InternationalEftsl;
            ws.Cell(exRow, 8).Value = h.CgsEftsl;
            ws.Cell(exRow, 9).Value = h.StudentsPerUnit;
            ws.Cell(exRow, 10).Value = h.NewUnitHours;
            ws.Cell(exRow, 11).Value = h.StudentHours;
            ws.Cell(exRow, 12).Value = h.TotalHours;
            ws.Cell(exRow, 13).Value = h.FacultyRevenue;
            ws.Cell(exRow, 14).Value = h.FacultyCost;
            ws.Cell(exRow, 15).Value = h.NonFacultyCost;
            ws.Cell(exRow, 16).Value = h.TotalCost;
            ws.Cell(exRow, 17).Value = h.Margin;
            ws.Cell(exRow, 18).Value = h.MarginPercent;

            FormatNumberCells(ws, exRow, [6, 7, 8, 9, 10, 11, 12]);
            FormatCurrencyCells(ws, exRow, [13, 14, 15, 16, 17]);
            FormatPercentCells(ws, exRow, [18]);
        }

        ws.Columns().AdjustToContents();
    }

    private static void StyleHeaderRow(IXLRow row)
    {
        row.Style.Font.Bold = true;
        row.Style.Fill.BackgroundColor = XLColor.FromHtml("#1e3a5f");
        row.Style.Font.FontColor = XLColor.White;
    }

    private static void FormatCurrencyCells(IXLWorksheet ws, int row, int[] cols)
    {
        foreach (var c in cols)
            ws.Cell(row, c).Style.NumberFormat.Format = "$#,##0";
    }

    private static void FormatPercentCells(IXLWorksheet ws, int row, int[] cols)
    {
        foreach (var c in cols)
            ws.Cell(row, c).Style.NumberFormat.Format = "0.0%";
    }

    private static void FormatNumberCells(IXLWorksheet ws, int row, int[] cols)
    {
        foreach (var c in cols)
            ws.Cell(row, c).Style.NumberFormat.Format = "#,##0.00";
    }
}
