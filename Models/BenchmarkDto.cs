namespace ProgramInsights.Models;

public class BenchmarkFilterOptionsDto
{
    public List<string> DegreeLevels { get; set; } = new();
    public List<string> FoeCodes { get; set; } = new();
    public List<string> PeriodNames { get; set; } = new();
}

public class BenchmarkMeasureDto
{
    public string Measure { get; set; } = "";
    public double? Min { get; set; }
    public double? Max { get; set; }
    public double? WeightedMean { get; set; }
    public double? Median { get; set; }
    public int BenchmarkN { get; set; }
    public int ConfidenceBenchmarkN { get; set; }
    public string Confidence { get; set; } = "";
}
