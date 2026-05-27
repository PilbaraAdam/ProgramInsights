namespace ProgramInsights.Models;

public class ModelConfigDto
{
    public string ConfigId { get; set; } = "";
    public string ModelId { get; set; } = "";
    public string TenantId { get; set; } = "";
    public string SourceDatabase { get; set; } = "";
    public string SourceSchema { get; set; } = "PUBLIC";
    public string ModelFilter { get; set; } = "";
    public string PeriodFilter { get; set; } = "";
    public List<string> FeeTypesDom { get; set; } = new();
    public List<string> FeeTypesInt { get; set; } = new();
    public List<string> FeeTypesCgs { get; set; } = new();
    public List<string> FacCostColumns { get; set; } = new();
    public List<string> RevDomColumns { get; set; } = new();
    public List<string> RevIntColumns { get; set; } = new();
    public List<string> RevCgsColumns { get; set; } = new();
    public string FactTable { get; set; } = "FACT_GL_TO_PRODUCTS_TO_COURSES";
    public string FeeTypeTable { get; set; } = "COURSES_OBJECTS";
    public string FeeTypeColumn { get; set; } = "COURSE_FEE_TYPE";
    public string DeliveryHoursCol { get; set; } = "SUPPLIED_AC_HOURS";
    public string TotalExpCol { get; set; } = "EXP";

    // Dimension structure — which CODE columns map to which objects
    /// <summary>Which CODE column in the fact table joins to the course dimension table (CODE2 or CODE3).</summary>
    public string CourseCodeCol { get; set; } = "CODE3";
    /// <summary>Which CODE column joins to the unit/product dimension table (CODE1 or CODE2). Empty = no unit view.</summary>
    public string UnitCodeCol { get; set; } = "CODE2";
    /// <summary>Table name for the unit/product dimension (joined on UnitCodeCol).</summary>
    public string UnitDimTable { get; set; } = "PRODUCTS_OBJECTS";
    /// <summary>Whether to INNER JOIN GL_OBJECTS as a filter dimension.</summary>
    public bool IncludeGlJoin { get; set; } = true;
    /// <summary>Which CODE column joins to GL_OBJECTS (only used when IncludeGlJoin is true).</summary>
    public string GlCodeCol { get; set; } = "CODE1";
}
