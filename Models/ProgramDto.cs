namespace ProgramInsights.Models;

public record ProgramDto(
    Guid Id,
    Guid ScenarioId,
    string Name,
    string? Description,
    DateTime? SaveDate,
    int StartYear = 0,
    int UnitCount = 0,
    bool HasCostRevenue = false,
    int YearCount = 0);
