using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using WebApplication1.Models;
using WebApplication1.Services;

namespace WebApplication1.Controllers;

[ApiController]
public sealed class ProjectRiskController : ControllerBase
{
    private const string GraphQlApiUrl = "https://meshstage.smsassist.com/rehab-query-service/ui/graphql/";

    // TODO: set the project-risk agent id (placeholder until the dedicated risk agent is available).
    private const string RiskAgentId = "f7c42d9e-1afc-48bb-9e0a-90cce8cd792e";

    private const string ProjectScopeListQuery = @"
query getProjectScopeList($projectId: Int!) {
  project(id: $projectId) {
    id
    scope {
      id
      status {
        id
        name
      }
      createDate
    }
    changeOrderList {
      id
      status {
        id
        name
      }
      createDate
    }
  }
}";

    private readonly IConversationApiClient _conversationApiClient;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ProjectRiskController> _logger;

    public ProjectRiskController(
        IConversationApiClient conversationApiClient,
        IHttpClientFactory httpClientFactory,
        ILogger<ProjectRiskController> logger)
    {
        _conversationApiClient = conversationApiClient;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    [HttpGet("/api/projects/{projectId:int}/risk")]
    public async Task<IActionResult> GetProjectRisk(
        [FromRoute] int projectId,
        [FromQuery] bool debug,
        CancellationToken cancellationToken)
    {
        var tokenResult = await _conversationApiClient.GetTokenAsync(cancellationToken);
        if (tokenResult is null)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, new
            {
                success = false,
                message = "Failed to get bearer token."
            });
        }

        // GraphQL first: fetch project scope + change order list summary.
        var projectScopeList = await CallGraphQlApiAsync(
            tokenResult.Token,
            ProjectScopeListQuery,
            new JsonObject { ["projectId"] = projectId },
            cancellationToken);

        if (projectScopeList is null)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new
            {
                success = false,
                message = "Failed to fetch project scope and change order list."
            });
        }

        // Early return when there are no change orders: do not call the AI.
        var changeOrderIds = ExtractChangeOrderIds(projectScopeList);
        //if (changeOrderIds.Count == 0)
        //{
        //    return Ok(new
        //    {
        //        success = true,
        //        message = "Project scope list fetched successfully, but no change orders were found.",
        //        projectId,
        //        projectScope = projectScopeList
        //    });
        //}

        // AI second: pass the project scope list JSON directly as the conversation text.
        var text = projectScopeList.ToJsonString();
        var conversationId = $"project-{projectId}-risk-{Guid.NewGuid():N}";

        var riskDecision = await _conversationApiClient.CallConversationAsync(
            conversationId,
            text,
            default,
            RiskAgentId,
            cancellationToken);

        if (riskDecision is null)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, new
            {
                success = false,
                message = "Risk conversation API call failed."
            });
        }

        if (debug)
        {
            return Content(new JsonObject
            {
                ["success"] = true,
                ["message"] = "Successfully fetched project data and generated risk assessment.",
                ["projectId"] = projectId,
                ["projectScope"] = projectScopeList.DeepClone(),
                ["prompt"] = text,
                ["riskDecision"] = NormalizeToJsonNode(riskDecision)
            }.ToJsonString(), "application/json");
        }

        if (!TryConvertToProjectRiskResponse(riskDecision, out var riskResponse))
        {
            return StatusCode(StatusCodes.Status502BadGateway, new
            {
                success = false,
                message = "Conversation API response could not be converted into ProjectRiskResponse.",
                rawResult = riskDecision
            });
        }

        return Ok(riskResponse);
    }

    private async Task<JsonNode?> CallGraphQlApiAsync(
        string token,
        string query,
        JsonObject variables,
        CancellationToken cancellationToken)
    {
        var httpClient = _httpClientFactory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, GraphQlApiUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = JsonContent.Create(new JsonObject
        {
            ["query"] = query,
            ["variables"] = variables
        });

        try
        {
            using var response = await httpClient.SendAsync(request, cancellationToken);
            var content = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("GraphQL API failed with status {StatusCode}: {Content}", (int)response.StatusCode, content);
                return null;
            }

            var node = JsonNode.Parse(content);
            if (node?["errors"] is JsonArray errors && errors.Count > 0)
            {
                _logger.LogError("GraphQL API returned errors: {Errors}", errors.ToJsonString());
                return null;
            }

            return node;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogError(ex, "GraphQL API call failed.");
            return null;
        }
    }

    private static List<int> ExtractChangeOrderIds(JsonNode projectScopeList)
    {
        var ids = new List<int>();
        var changeOrderList = projectScopeList["data"]?["project"]?["changeOrderList"]?.AsArray();
        if (changeOrderList is null)
        {
            return ids;
        }

        foreach (var changeOrder in changeOrderList)
        {
            if (changeOrder?["id"]?.GetValue<int?>() is int id)
            {
                ids.Add(id);
            }
        }

        return ids;
    }

    private static JsonNode? NormalizeToJsonNode(object result)
    {
        return result switch
        {
            null => null,
            JsonNode node => node.DeepClone(),
            string content => JsonValue.Create(content),
            _ => JsonSerializer.SerializeToNode(result)
        };
    }

    private static bool TryConvertToProjectRiskResponse(
        object result,
        out ProjectRiskResponse? riskResponse)
    {
        riskResponse = null;

        // Take only the conversation wrapper's "text" payload; ignore the rest of the envelope.
        var node = NormalizeToJsonNode(result);
        var text = node is JsonObject wrapper
            ? ReadString(GetPropertyValue(wrapper, "text"))
            : ReadString(node);

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        foreach (var candidate in EnumerateJsonCandidates(text.Trim()))
        {
            try
            {
                if (JsonNode.Parse(candidate) is JsonObject risk &&
                    TryBuildRiskResponse(risk, out riskResponse))
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

    private static bool TryBuildRiskResponse(
        JsonObject risk,
        out ProjectRiskResponse? riskResponse)
    {
        riskResponse = null;

        var generalSummary = ReadTextValue(GetPropertyValue(risk, "GeneralSummary"));
        var notableInsights = ReadTextValue(GetPropertyValue(risk, "NotableInsights"));
        var riskAnalysis = ReadTextValue(GetPropertyValue(risk, "RiskAnalysis"));
        var riskStatus = ReadIntValue(GetPropertyValue(risk, "RiskStatus"));

        if (string.IsNullOrWhiteSpace(generalSummary) ||
            string.IsNullOrWhiteSpace(notableInsights) ||
            string.IsNullOrWhiteSpace(riskAnalysis) ||
            riskStatus is null)
        {
            return false;
        }

        riskResponse = new ProjectRiskResponse(generalSummary, notableInsights, riskAnalysis, riskStatus);
        return true;
    }

    // The agent returns these fields either as a single string or as an array of bullet strings.
    private static string? ReadTextValue(JsonNode? node)
    {
        if (node is JsonArray array)
        {
            var joined = string.Join(
                " ",
                array.Select(ReadString).Where(part => !string.IsNullOrWhiteSpace(part)));
            return string.IsNullOrWhiteSpace(joined) ? null : joined;
        }

        return ReadString(node);
    }

    private static string? ReadString(JsonNode? node)
    {
        return node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    }

    private static int? ReadIntValue(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue<int>(out var number))
        {
            return number;
        }

        if (value.TryGetValue<string>(out var text) && int.TryParse(text, out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static JsonNode? GetPropertyValue(JsonObject obj, string propertyName)
    {
        if (obj.TryGetPropertyValue(propertyName, out var value))
        {
            return value;
        }

        foreach (var property in obj)
        {
            if (string.Equals(property.Key, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value;
            }
        }

        return null;
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
}
