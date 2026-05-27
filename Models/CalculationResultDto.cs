namespace ProgramInsights.Models;

public class ProgramCalculationResultDto
{
    public string ProgramName { get; set; } = "";
    public int StartYear { get; set; }
    public List<CostCalculationDto> CostResults { get; set; } = new();
    public List<HourCalculationDto> HourResults { get; set; } = new();
}

public class CostCalculationDto
{
    public int YearIndex { get; set; }
    public int CalendarYear { get; set; }
    public double FacultyCost { get; set; }
    public double FacultyRevenue { get; set; }
    public double FacultyMargin { get; set; }
    public double FacultyMarginPercent { get; set; }
    public double NonFacultyCost { get; set; }
    public double TotalMargin { get; set; }
    public double TotalMarginPercent { get; set; }
    public double TotalEftsl { get; set; }
    public double TotalTeachingHours { get; set; }
}

public class HourCalculationDto
{
    public int ForecastYearIndex { get; set; }
    public int CalendarYear { get; set; }
    public int CohortStartYearIndex { get; set; }
    public int StudyYear { get; set; }
    public string Unit { get; set; } = "";
    public string Profile { get; set; } = "";
    public double DomesticEftsl { get; set; }
    public double InternationalEftsl { get; set; }
    public double CgsEftsl { get; set; }
    public double StudentsPerUnit { get; set; }
    public double NewUnitHours { get; set; }
    public double NewUnitAdditionalClasses { get; set; }
    public double NewUnitRepeatTutorialHours { get; set; }
    public double CurrentUnitAdditionalClasses { get; set; }
    public double CurrentUnitAdditionalTutorialHours { get; set; }
    public double StudentHours { get; set; }
    public double TotalHours { get; set; }

    // Per-unit financials (computed alongside hours)
    public double FacultyRevenue { get; set; }
    public double FacultyCost { get; set; }
    public double NonFacultyCost { get; set; }
    public double TotalCost { get; set; }
    public double Margin { get; set; }
    public double MarginPercent { get; set; }
}
