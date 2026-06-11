using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WebApplication1.Services;

public interface IProjectRiskAnalysisClient
{
    Task<ProjectRiskAnalysisResult> GetLatestProjectRisksAsync(
        int clientId,
        string? agentId,
        bool debug,
        CancellationToken cancellationToken);
}

public sealed class ProjectRiskAnalysisClient : IProjectRiskAnalysisClient
{
    private const string GraphQlApiUrl = "https://meshstage.smsassist.com/rehab-query-service/ui/graphql/";
    private const string DefaultRiskAgentId = "8810e02e-27d0-44a6-be8a-bf6471f441b0";

    private const string ProjectListQuery = @"
query getProjectList($input: ProjectListInput!) {
  projectList(input: $input) {
    id
  }
  projectListCount(input: $input)
}";

    private const string ProjectDetailsByIdsQuery = @"
query getProjectDetailsByIds($input: ProjectListInput!) {
  projectList(input: $input) {
    id
    name
    projectNum
    remark
    createDate
    estimateScheduledDate
    estimatedSubmittedDate
    committedStartDate
    actualProjectStartDate
    expectedCompletionDate
    completedDate
    estimateStartDate
    estimateEndDate
    moveInDate
    moveOutDate
    escalation {
      description
      escalationType {
        name
      }
    }
    type {
      name
    }
    status {
      name
    }
    openDuration
    leftDuration
    modifyDate
  }
}";

    private readonly IConversationApiClient _conversationApiClient;
    private readonly HttpClient _httpClient;
    private readonly ILogger<ProjectRiskAnalysisClient> _logger;

    public ProjectRiskAnalysisClient(
        IConversationApiClient conversationApiClient,
        HttpClient httpClient,
        ILogger<ProjectRiskAnalysisClient> logger)
    {
        _conversationApiClient = conversationApiClient;
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<ProjectRiskAnalysisResult> GetLatestProjectRisksAsync(
        int clientId,
        string? agentId,
        bool debug,
        CancellationToken cancellationToken)
    {
        var tokenResult = await _conversationApiClient.GetTokenAsync(cancellationToken);
        if (tokenResult is null)
        {
            return ProjectRiskAnalysisResult.Error("Failed to get bearer token.");
        }

        var projectList = await CallGraphQlApiAsync(
            tokenResult.Token,
            ProjectListQuery,
            new JsonObject
            {
                ["input"] = BuildProjectListInput(clientId)
            },
            cancellationToken);

        if (projectList is null)
        {
            return ProjectRiskAnalysisResult.Error("Failed to fetch project list.");
        }

        var projectDetails = await FetchProjectDetailsAsync(
            tokenResult.Token,
            projectList,
            cancellationToken);

        var riskInput = BuildRiskInput(projectList, projectDetails, clientId);
        var riskAgentId = string.IsNullOrWhiteSpace(agentId) ? DefaultRiskAgentId : agentId;
        var riskAnalysis = await CallConversationApiAsync(
            riskAgentId,
            clientId,
            riskInput,
            cancellationToken);
        JsonNode extractedRiskCards = new JsonObject();
        var fallbackUsed = riskAnalysis is null || !TryExtractRiskCards(riskAnalysis, out extractedRiskCards);

        if (debug)
        {
            return ProjectRiskAnalysisResult.SuccessJson(JsonSerializer.Serialize(new JsonObject
            {
                ["success"] = true,
                ["clientId"] = clientId,
                ["agentId"] = riskAgentId,
                ["projectList"] = projectList.DeepClone(),
                ["projectDetails"] = projectDetails.DeepClone(),
                ["riskInput"] = riskInput.DeepClone(),
                ["riskAnalysis"] = riskAnalysis?.DeepClone(),
                ["fallbackUsed"] = fallbackUsed
            }));
        }

        if (!fallbackUsed)
        {
            return ProjectRiskAnalysisResult.SuccessJson(extractedRiskCards.ToJsonString());
        }

        return ProjectRiskAnalysisResult.SuccessJson(BuildFallbackRiskCards(riskInput).ToJsonString());
    }

    private async Task<JsonNode?> CallGraphQlApiAsync(
        string token,
        string query,
        JsonObject variables,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, GraphQlApiUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = JsonContent.Create(new JsonObject
        {
            ["query"] = query,
            ["variables"] = variables
        });

        try
        {
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var content = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Project list GraphQL API failed with status {StatusCode}: {Content}", (int)response.StatusCode, content);
                return null;
            }

            var node = JsonNode.Parse(content);
            if (node?["errors"] is JsonArray errors && errors.Count > 0)
            {
                _logger.LogError("Project list GraphQL API returned errors: {Errors}", errors.ToJsonString());
                return null;
            }

            return node;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogError(ex, "Project list GraphQL API call failed.");
            return null;
        }
    }

    private async Task<JsonNode?> CallConversationApiAsync(
        string agentId,
        int clientId,
        JsonObject riskInput,
        CancellationToken cancellationToken)
    {
        var text = "PROJECT_RISK_INPUT_JSON:\n" + riskInput.ToJsonString();
        var states = JsonSerializer.SerializeToElement(new JsonArray
        {
            new JsonObject { ["key"] = "clientId", ["value"] = clientId },
            new JsonObject { ["key"] = "projectRiskInput", ["value"] = riskInput.DeepClone() }
        });

        try
        {
            var result = await _conversationApiClient.CallConversationAsync(
                $"project-risk-client-{clientId}-{Guid.NewGuid():N}",
                text,
                states,
                agentId,
                cancellationToken);

            return result switch
            {
                null => null,
                JsonNode node => node.DeepClone(),
                string content => JsonValue.Create(content),
                _ => JsonSerializer.SerializeToNode(result)
            };
        }
        catch (Exception ex) when (ex is TaskCanceledException or JsonException)
        {
            _logger.LogError(ex, "Project risk conversation API call failed.");
            return null;
        }
    }

    private async Task<JsonArray> FetchProjectDetailsAsync(
        string token,
        JsonNode projectList,
        CancellationToken cancellationToken)
    {
        var details = new JsonArray();
        var projectIds = projectList["data"]?["projectList"]?.AsArray()
            .Select(project => project?["id"]?.GetValue<int?>())
            .Where(projectId => projectId.HasValue)
            .Select(projectId => projectId!.Value)
            .ToList() ?? new List<int>();

        if (projectIds.Count == 0)
        {
            return details;
        }

        var detail = await CallGraphQlApiAsync(
            token,
            ProjectDetailsByIdsQuery,
            new JsonObject { ["input"] = BuildProjectDetailsInput(projectIds) },
            cancellationToken);

        foreach (var project in detail?["data"]?["projectList"]?.AsArray() ?? new JsonArray())
        {
            details.Add(new JsonObject
            {
                ["data"] = new JsonObject
                {
                    ["project"] = project?.DeepClone()
                }
            });
        }

        return details;
    }

    private static JsonObject BuildProjectListInput(int clientId)
    {
        return new JsonObject
        {
            ["statusIds"] = new JsonArray(),
            ["id"] = null,
            ["locationAddress"] = string.Empty,
            ["states"] = new JsonArray(),
            ["marketIds"] = new JsonArray(),
            ["projectTypeIds"] = new JsonArray(),
            ["projectSupervisorIds"] = new JsonArray(),
            ["filedProjectManagerIds"] = new JsonArray(),
            ["clientIds"] = new JsonArray(clientId),
            ["isIncludeTestProject"] = false,
            ["page"] = 1,
            ["pageSize"] = 4,
            ["orderBy"] = "createDate",
            ["asc"] = true
        };
    }

    private static JsonObject BuildProjectDetailsInput(IEnumerable<int> projectIds)
    {
        var ids = new JsonArray();
        foreach (var projectId in projectIds)
        {
            ids.Add(projectId);
        }

        return new JsonObject
        {
            ["ids"] = ids,
            ["isIncludeTestProject"] = false,
            ["page"] = 1,
            ["pageSize"] = ids.Count
        };
    }

    private static JsonObject BuildRiskInput(JsonNode projectList, JsonArray projectDetails, int clientId)
    {
        var projects = new JsonArray();
        var detailedProjectsById = projectDetails
            .Select(detail => detail?["data"]?["project"])
            .Where(project => project is not null)
            .ToDictionary(
                project => GetString(project?["id"]) ?? string.Empty,
                project => project!,
                StringComparer.OrdinalIgnoreCase);

        foreach (var listProject in projectList["data"]?["projectList"]?.AsArray() ?? new JsonArray())
        {
            var projectId = GetString(listProject?["id"]) ?? string.Empty;
            var project = detailedProjectsById.TryGetValue(projectId, out var detailedProject)
                ? detailedProject
                : listProject;
            projects.Add(new JsonObject
            {
                ["id"] = project?["id"]?.DeepClone(),
                ["name"] = project?["name"]?.DeepClone(),
                ["project_num"] = project?["projectNum"]?.DeepClone(),
                ["status"] = project?["status"]?["name"]?.DeepClone(),
                ["type"] = project?["type"]?["name"]?.DeepClone(),
                ["remark"] = project?["remark"]?.DeepClone(),
                ["create_date"] = project?["createDate"]?.DeepClone(),
                ["estimate_scheduled_date"] = project?["estimateScheduledDate"]?.DeepClone(),
                ["estimated_submitted_date"] = project?["estimatedSubmittedDate"]?.DeepClone(),
                ["committed_start_date"] = project?["committedStartDate"]?.DeepClone(),
                ["actual_start_date"] = project?["actualProjectStartDate"]?.DeepClone(),
                ["expected_completion_date"] = project?["expectedCompletionDate"]?.DeepClone(),
                ["completed_date"] = project?["completedDate"]?.DeepClone(),
                ["estimate_start_date"] = project?["estimateStartDate"]?.DeepClone(),
                ["estimate_end_date"] = project?["estimateEndDate"]?.DeepClone(),
                ["move_in_date"] = project?["moveInDate"]?.DeepClone(),
                ["move_out_date"] = project?["moveOutDate"]?.DeepClone(),
                ["escalation"] = project?["escalation"] is JsonObject escalation
                    ? new JsonObject
                    {
                        ["description"] = escalation["description"]?.DeepClone(),
                        ["type"] = escalation["escalationType"]?["name"]?.DeepClone()
                    }
                    : null,
                ["open_duration"] = project?["openDuration"]?.DeepClone(),
                ["left_duration"] = project?["leftDuration"]?.DeepClone(),
                ["modify_date"] = project?["modifyDate"]?.DeepClone()
            });
        }

        return new JsonObject
        {
            ["client_id"] = clientId,
            ["generated_at"] = DateTimeOffset.UtcNow.ToString("O"),
            ["project_count"] = projectList["data"]?["projectListCount"]?.DeepClone(),
            ["projects"] = projects
        };
    }

    private static JsonObject BuildFallbackRiskCards(JsonObject riskInput)
    {
        var cards = new JsonArray();
        foreach (var projectNode in riskInput["projects"]?.AsArray() ?? new JsonArray())
        {
            if (projectNode is not JsonObject project)
            {
                continue;
            }

            var fallbackRisk = BuildFallbackRiskCard(project);
            if (fallbackRisk is null)
            {
                continue;
            }

            cards.Add(fallbackRisk);
        }

        return new JsonObject
        {
            ["run_id"] = $"run_project_risk_client_{GetString(riskInput["client_id"])}",
            ["agent"] = "project-risk-detection-agent",
            ["generated_at"] = riskInput["generated_at"]?.DeepClone(),
            ["status"] = "completed",
            ["risks"] = cards
        };
    }

    private static JsonObject? BuildFallbackRiskCard(JsonObject project)
    {
        var status = GetString(project["status"]) ?? "Unknown";
        var expectedCompletion = GetString(project["expected_completion_date"]);
        var committedStart = GetString(project["committed_start_date"]);
        var actualStart = GetString(project["actual_start_date"]);
        var escalation = project["escalation"] as JsonObject;
        var projectName = GetString(project["name"]) ?? GetString(project["project_num"]);

        string? riskLevel = null;
        string? title = null;
        string? description = null;
        string? suggestion = null;

        if (IsPastDue(expectedCompletion))
        {
            riskLevel = "1";
            title = "Project may miss completion date";
            description = $"Expected completion date has passed while status is {status}.";
            suggestion = "Confirm the updated completion plan and notify stakeholders if the date has changed.";
        }
        else if (IsPastDue(committedStart) && string.IsNullOrWhiteSpace(actualStart))
        {
            riskLevel = "1";
            title = "Committed start date missed";
            description = $"Committed start date has passed, but actual start date is still missing.";
            suggestion = "Confirm whether work has started and update the project start date.";
        }
        else if (escalation is not null)
        {
            riskLevel = "1";
            title = "Project has escalation";
            description = GetString(escalation["description"]) ?? "Project has an active escalation.";
            suggestion = "Review the escalation and assign the next owner for resolution.";
        }
        else if (string.IsNullOrWhiteSpace(expectedCompletion) &&
            (status.Contains("Schedule", StringComparison.OrdinalIgnoreCase) ||
             status.Contains("Pending Client Scope", StringComparison.OrdinalIgnoreCase) ||
             status.Contains("NTP", StringComparison.OrdinalIgnoreCase)))
        {
            riskLevel = "2";
            title = "Project missing completion date";
            description = $"Project is in {status} status but has no expected completion date.";
            suggestion = "Set an expected completion date once the next project milestone is confirmed.";
        }

        if (riskLevel is null)
        {
            return null;
        }

        return new JsonObject
        {
            ["risk_level"] = riskLevel,
            ["project_id"] = GetString(project["id"]),
            ["project_name"] = projectName,
            ["risk_title"] = title,
            ["risk_description"] = description,
            ["suggestion"] = suggestion
        };
    }

    private static bool TryExtractRiskCards(JsonNode node, out JsonNode riskCards)
    {
        riskCards = new JsonObject();
        if (LooksLikeRiskCards(node))
        {
            riskCards = node.DeepClone();
            return true;
        }

        if (node is JsonValue value && value.TryGetValue<string>(out var text))
        {
            return TryExtractRiskCardsFromText(text, out riskCards);
        }

        if (node is JsonObject obj)
        {
            foreach (var propertyName in new[] { "apiResult", "data", "text", "content", "message", "response", "result" })
            {
                if (obj[propertyName] is JsonNode child && TryExtractRiskCards(child, out riskCards))
                {
                    return true;
                }
            }

            foreach (var child in obj.Select(item => item.Value).Where(child => child is not null))
            {
                if (child is not null && TryExtractRiskCards(child, out riskCards))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TryExtractRiskCardsFromText(string text, out JsonNode riskCards)
    {
        riskCards = new JsonObject();
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return false;
        }

        try
        {
            var node = JsonNode.Parse(text[start..(end + 1)]);
            if (node is not null && LooksLikeRiskCards(node))
            {
                riskCards = node;
                return true;
            }
        }
        catch (JsonException)
        {
            return false;
        }

        return false;
    }

    private static bool LooksLikeRiskCards(JsonNode node)
    {
        return node is JsonObject obj &&
            (obj["risks"] is JsonArray || obj["risk_cards"] is JsonArray);
    }

    private static bool IsPastDue(string? dateText)
    {
        return DateTimeOffset.TryParse(dateText, out var date) &&
            date.Date < DateTimeOffset.UtcNow.Date;
    }

    private static string? GetString(JsonNode? node)
    {
        if (node is null)
        {
            return null;
        }

        if (node is JsonValue value)
        {
            if (value.TryGetValue<string>(out var stringValue))
            {
                return stringValue;
            }

            if (value.TryGetValue<int>(out var intValue))
            {
                return intValue.ToString();
            }

            if (value.TryGetValue<decimal>(out var decimalValue))
            {
                return decimalValue.ToString("0.##");
            }
        }

        return node.ToJsonString();
    }

    private static string JoinAddress(params string?[] parts)
    {
        return string.Join(", ", parts.Where(part => !string.IsNullOrWhiteSpace(part)));
    }
}

public sealed record ProjectRiskAnalysisResult(
    bool Success,
    int StatusCode,
    string Message,
    string Json,
    object? DebugData)
{
    public static ProjectRiskAnalysisResult SuccessJson(string json)
    {
        return new ProjectRiskAnalysisResult(true, StatusCodes.Status200OK, string.Empty, json, null);
    }

    public static ProjectRiskAnalysisResult Error(
        string message,
        int statusCode = StatusCodes.Status500InternalServerError,
        object? debugData = null)
    {
        return new ProjectRiskAnalysisResult(false, statusCode, message, "{}", debugData);
    }
}
