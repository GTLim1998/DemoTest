using System.Text.Json.Serialization;

namespace WebApplication1.Models;

public sealed record ProjectRiskResponse(
    [property: JsonPropertyName("GeneralSummary")] string? GeneralSummary,
    [property: JsonPropertyName("NotableInsights")] string? NotableInsights,
    [property: JsonPropertyName("RiskAnalysis")] string? RiskAnalysis,
    [property: JsonPropertyName("RiskStatus")] int? RiskStatus);
