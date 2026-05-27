namespace ProgramInsights.Models;

public class ProgramDetailsDto
{
    public Guid Id { get; set; }
    public Guid ScenarioId { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public DateTime? SaveDate { get; set; }
    public int StartYear { get; set; } = DateTime.Today.Year;

    public string ScenarioName { get; set; } = "";
    public string InsightsModel { get; set; } = "";
    public DateTime? ScenarioSaveDate { get; set; }
    public string SourceDb { get; set; } = "";

    public string RevCgsCluster { get; set; } = "Cluster 1";
    public string RevCgsSelect { get; set; } = "Custom";
    public double RevCgsCustom { get; set; }
    public string RevDomSelect { get; set; } = "Custom";
    public double RevDomCustom { get; set; }
    public string RevIntSelect { get; set; } = "Custom";
    public double RevIntCustom { get; set; }
    public string CphSelect { get; set; } = "Custom";
    public double CphCustom { get; set; }
    public string NfcSelect { get; set; } = "Custom";
    public double NfcCustom { get; set; }

    public string SchoolSelect { get; set; } = "";
    public string CourseSelect { get; set; } = "";
    public string CareerSelect { get; set; } = "";
    public string Profile { get; set; } = "Standard";

    public List<ProgramYearSettingDto> Settings { get; set; } = new();
    public List<ProgramUnitDto> Units { get; set; } = new();
    public List<ProgramProfileDto> Profiles { get; set; } = new();
    public List<ProgramUnitOptionDto> UnitOptions { get; set; } = new();
    public List<ProgramUnitValueDto> UnitValues { get; set; } = new();
    public List<ModelValueDto> ModelValues { get; set; } = new();
    public List<ConstantOptionDto> CgsClusters { get; set; } = new();
}

public class ProgramYearSettingDto
{
    public int Year { get; set; }
    public double CgsEftsl { get; set; }
    public double DomesticEftsl { get; set; }
    public double InternationalEftsl { get; set; }
    public double FixedExpense { get; set; }
    public double RevenueCpi { get; set; }
    public double ExpenseCpi { get; set; }
}

public class ProgramUnitDto
{
    public string Unit { get; set; } = "";
    public string Profile { get; set; } = "";
    public int ProgramYear { get; set; } = 1;

    // Custom profile values — only used when Profile == "Custom"
    public double? CustomTutSize { get; set; }
    public double? CustomLectHours { get; set; }
    public double? CustomTutHours { get; set; }
    public double? CustomPrepHours { get; set; }
    public double? CustomAdminHours { get; set; }
    public double? CustomStudentHours { get; set; }
}

public class ProgramProfileDto
{
    public string Name { get; set; } = "";
    public double TutSize { get; set; }
    public double LectureHours { get; set; }
    public double TutorialHours { get; set; }
    public double PreparationHours { get; set; }
    public double AdministrationHours { get; set; }
    public double StudentHours { get; set; }
}

public class ProgramUnitOptionDto
{
    public string Name { get; set; } = "";
    public string School { get; set; } = "";
}

public class ProgramUnitValueDto
{
    public string UnitName { get; set; } = "";
    public double FacultyExpense { get; set; }
    public double NonFacultyExpense { get; set; }
    public double DomesticEftsl { get; set; }
    public double InternationalEftsl { get; set; }
    public double CgsEftsl { get; set; }
    public double DomesticRevenue { get; set; }
    public double InternationalRevenue { get; set; }
    public double CgsRevenue { get; set; }
    public double DeliveryHours { get; set; }

    public double TotalEftsl => DomesticEftsl + InternationalEftsl + CgsEftsl;
    public double FacultyCostPerHour => DeliveryHours == 0 ? 0 : FacultyExpense / DeliveryHours;
    public double NonFacultyCostPerEftsl => TotalEftsl == 0 ? 0 : NonFacultyExpense / TotalEftsl;
    public double DomesticRevenuePerEftsl => DomesticEftsl == 0 ? 0 : DomesticRevenue / DomesticEftsl;
    public double InternationalRevenuePerEftsl => InternationalEftsl == 0 ? 0 : InternationalRevenue / InternationalEftsl;
    public double CgsRevenuePerEftsl => CgsEftsl == 0 ? 0 : CgsRevenue / CgsEftsl;
}

public class ModelValueDto
{
    public string School { get; set; } = "";
    public string Career { get; set; } = "";
    public string CourseName { get; set; } = "";
    public double FacultyExpense { get; set; }
    public double NonFacultyExpense { get; set; }
    public double DomesticEftsl { get; set; }
    public double InternationalEftsl { get; set; }
    public double CgsEftsl { get; set; }
    public double DomesticRevenue { get; set; }
    public double InternationalRevenue { get; set; }
    public double CgsRevenue { get; set; }
    public double DeliveryHours { get; set; }
}

public class ModelValueSummaryDto
{
    public string School { get; set; } = "";
    public string Career { get; set; } = "";
    public double CostPerHour { get; set; }
    public double NonCostPerEftsl { get; set; }
    public double RevCgsPerEftsl { get; set; }
    public double RevDomPerEftsl { get; set; }
    public double RevIntPerEftsl { get; set; }
    public double DeliveryHours { get; set; }
    public double DomesticEftsl { get; set; }
    public double InternationalEftsl { get; set; }
}

public class ConstantOptionDto
{
    public string Name { get; set; } = "";
    public double Value { get; set; }
}
