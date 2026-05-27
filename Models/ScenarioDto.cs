namespace ProgramInsights.Models;

public record ScenarioDto(Guid Id, string Name, string SourceDb, int ProgramCount = 0, bool HasMappingConfig = false);
