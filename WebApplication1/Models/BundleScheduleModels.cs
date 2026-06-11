using System.Text.Json;
using System.Text.Json.Serialization;

namespace WebApplication1.Models;

public sealed class BundleScheduleRequest
{
    [JsonPropertyName("bundles")]
    public List<BundleScheduleInputBundle> Bundles { get; init; } = [];

    [JsonPropertyName("states")]
    public JsonElement? States { get; init; }
}

public sealed class BundleScheduleInputBundle
{
    [JsonPropertyName("bundle_id")]
    public string? BundleId { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("trade")]
    public string? Trade { get; init; }

    [JsonPropertyName("trade_display")]
    public string? TradeDisplay { get; init; }

    [JsonPropertyName("line_work_ids")]
    public List<string>? LineWorkIds { get; init; }
}

public sealed record BundleScheduleResponse(
    [property: JsonPropertyName("bundles")] IReadOnlyList<BundleScheduleOutputBundle> Bundles);

public sealed record BundleScheduleOutputBundle(
    [property: JsonPropertyName("bundle_id")] string BundleId,
    [property: JsonPropertyName("confidence")] double Confidence,
    [property: JsonPropertyName("suggested_sequence")] int SuggestedSequence,
    [property: JsonPropertyName("suggested_schedule")] SuggestedSchedule SuggestedSchedule);

public sealed record SuggestedSchedule(
    [property: JsonPropertyName("start_date")] string StartDate,
    [property: JsonPropertyName("end_date")] string EndDate,
    [property: JsonPropertyName("depends_on_bundle")] string? DependsOnBundle,
    [property: JsonPropertyName("reason")] string Reason);

public sealed record ScheduleConstraint(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("detail")] string Detail);
