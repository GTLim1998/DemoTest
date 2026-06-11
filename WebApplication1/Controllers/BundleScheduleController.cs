using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using WebApplication1.Models;
using WebApplication1.Services;

namespace WebApplication1.Controllers;

[ApiController]
public sealed class BundleScheduleController : ControllerBase
{
    private const string ScheduleAgentId = "8184cf82-016b-4688-9534-8314bde6f384";
    private const string ScheduleConversationId = "865b2494-e69d-41ce-bb19-980f1cc234a0";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IConversationApiClient _conversationApiClient;

    public BundleScheduleController(IConversationApiClient conversationApiClient)
    {
        _conversationApiClient = conversationApiClient;
    }

    [HttpPost("/api/bundles/schedule-suggestion")]
    public async Task<IActionResult> GetScheduleSuggestion(
        [FromBody] BundleScheduleRequest? request,
        CancellationToken cancellationToken)
    {
        if (request?.Bundles is null || request.Bundles.Count == 0)
        {
            return BadRequest(new
            {
                status = "error",
                message = "At least one bundle is required."
            });
        }

        if (request.Bundles.Any(bundle => string.IsNullOrWhiteSpace(bundle.BundleId)))
        {
            return BadRequest(new
            {
                status = "error",
                message = "Each bundle must include a bundle_id."
            });
        }

        var text = BuildConversationText(request);
        var result = await _conversationApiClient.CallConversationAsync(
            ScheduleConversationId,
            text,
            request.States.GetValueOrDefault(),
            ScheduleAgentId,
            cancellationToken);

        if (result is null)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, new
            {
                status = "error",
                message = "External conversation API call failed."
            });
        }

        if (!TryConvertToBundleScheduleResponse(result, out var scheduleResponse))
        {
            return StatusCode(StatusCodes.Status502BadGateway, new
            {
                status = "error",
                message = "Conversation API response could not be converted into BundleScheduleResponse.",
                rawResult = result
            });
        }

        return Ok(scheduleResponse);
    }

    private static string BuildConversationText(BundleScheduleRequest request)
    {
        var inputJson = JsonSerializer.Serialize(new { bundles = request.Bundles }, JsonOptions);

        return
            "Create a construction bundle schedule suggestion from the input JSON. " +
            "Return valid JSON only. Do not include markdown or explanatory text. " +
            "The response must match this top-level shape exactly: " +
            "{\"bundles\":[{\"bundle_id\":\"string\",\"confidence\":0.0,\"suggested_sequence\":1,\"suggested_schedule\":{\"start_date\":\"yyyy-MM-dd\",\"end_date\":\"yyyy-MM-dd\",\"depends_on_bundle\":null,\"constraints_considered\":[{\"type\":\"string\",\"detail\":\"string\"}],\"reason\":\"string\"}}]}. " +
            "Input JSON:\n" +
            inputJson;
    }

    private static bool TryConvertToBundleScheduleResponse(
        object result,
        out BundleScheduleResponse? scheduleResponse)
    {
        scheduleResponse = null;

        if (result is BundleScheduleResponse typedResponse)
        {
            scheduleResponse = typedResponse;
            return HasBundles(scheduleResponse);
        }

        if (result is string text)
        {
            return TryConvertTextToBundleScheduleResponse(text, out scheduleResponse);
        }

        if (result is JsonNode node)
        {
            return TryConvertJsonNodeToBundleScheduleResponse(node, out scheduleResponse);
        }

        if (result is JsonElement element)
        {
            return TryConvertTextToBundleScheduleResponse(element.GetRawText(), out scheduleResponse);
        }

        try
        {
            var json = JsonSerializer.Serialize(result, JsonOptions);
            return TryConvertTextToBundleScheduleResponse(json, out scheduleResponse);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryConvertJsonNodeToBundleScheduleResponse(
        JsonNode node,
        out BundleScheduleResponse? scheduleResponse)
    {
        scheduleResponse = null;

        if (LooksLikeBundleScheduleResponse(node) &&
            TryDeserializeBundleScheduleResponse(node, out scheduleResponse))
        {
            return true;
        }

        if (node is JsonValue value &&
            value.TryGetValue<string>(out var text) &&
            TryConvertTextToBundleScheduleResponse(text, out scheduleResponse))
        {
            return true;
        }

        if (node is JsonObject obj)
        {
            foreach (var propertyName in new[] { "data", "text", "content", "message", "response", "result", "apiResult" })
            {
                if (TryGetPropertyValue(obj, propertyName, out var child) &&
                    child is not null &&
                    TryConvertJsonNodeToBundleScheduleResponse(child, out scheduleResponse))
                {
                    return true;
                }
            }

            foreach (var child in obj.Select(property => property.Value).Where(childNode => childNode is not null))
            {
                if (child is not null &&
                    TryConvertJsonNodeToBundleScheduleResponse(child, out scheduleResponse))
                {
                    return true;
                }
            }
        }

        if (node is JsonArray array)
        {
            foreach (var child in array.Where(childNode => childNode is not null))
            {
                if (child is not null &&
                    TryConvertJsonNodeToBundleScheduleResponse(child, out scheduleResponse))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TryConvertTextToBundleScheduleResponse(
        string text,
        out BundleScheduleResponse? scheduleResponse)
    {
        scheduleResponse = null;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        foreach (var candidate in EnumerateJsonCandidates(text.Trim()))
        {
            try
            {
                var node = JsonNode.Parse(candidate);
                if (node is not null &&
                    TryConvertJsonNodeToBundleScheduleResponse(node, out scheduleResponse))
                {
                    return true;
                }
            }
            catch (JsonException)
            {
                // Keep checking the next candidate.
            }
        }

        return false;
    }

    private static bool TryDeserializeBundleScheduleResponse(
        JsonNode node,
        out BundleScheduleResponse? scheduleResponse)
    {
        scheduleResponse = null;

        try
        {
            scheduleResponse = JsonSerializer.Deserialize<BundleScheduleResponse>(node.ToJsonString(), JsonOptions);
            return HasBundles(scheduleResponse);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static IEnumerable<string> EnumerateJsonCandidates(string text)
    {
        yield return text;

        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start >= 0 && end > start)
        {
            yield return text[start..(end + 1)];
        }
    }

    private static bool LooksLikeBundleScheduleResponse(JsonNode node)
    {
        return node is JsonObject obj &&
            TryGetPropertyValue(obj, "bundles", out var bundlesNode) &&
            bundlesNode is JsonArray;
    }

    private static bool TryGetPropertyValue(
        JsonObject obj,
        string propertyName,
        out JsonNode? value)
    {
        if (obj.TryGetPropertyValue(propertyName, out value))
        {
            return true;
        }

        foreach (var property in obj)
        {
            if (string.Equals(property.Key, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = null;
        return false;
    }

    private static bool HasBundles(BundleScheduleResponse? response)
    {
        return response?.Bundles is not null && response.Bundles.Count > 0;
    }
}
