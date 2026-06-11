using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WebApplication1.Services;

public interface IChangeOrderGroupingClient
{
    Task<ChangeOrderGroupingResult> GetGroupingSuggestionAsync(
        int projectId,
        int? scopeId,
        bool debug,
        CancellationToken cancellationToken);
}

public sealed class ChangeOrderGroupingClient : IChangeOrderGroupingClient
{
    private const string GraphQlApiUrl = "https://meshstage.smsassist.com/rehab-query-service/ui/graphql/";
    private const string InstructionBaseUrl = "https://meshstage.lessen.com/onebrain/instruct";
    private const string GroupingAgentId = "35b7a6bd-1272-4ab9-8231-4b3c2f9a48d7";

    private const string ProjectScopeListQuery = @"
query getProjectScopeList($projectId: Int!) {
  project(id: $projectId) {
    id
    expectedCompletionDate
    isLessenEstimate
    scope {
      id
      name
      status {
        id
        name
        __typename
      }
      channel {
        id
        __typename
      }
      createDate
      vendorCost
      clientNetPrice
      profit
      margin
      __typename
    }
    changeOrderList {
      id
      description
      channel {
        id
        __typename
      }
      addedDays
      status {
        id
        name
        __typename
      }
      vendorCost
      clientNetPrice
      margin
      profit
      reason {
        id
        name
        __typename
      }
      createDate
      changeOrderSource {
        id
        __typename
      }
      orderNum
      __typename
    }
    __typename
  }
}";

    private const string ChangeOrderDetailQuery = @"
query getChangeOrderDetail($projectId: Int!, $id: Int!) {
  project(id: $projectId) {
    id
    __typename
  }
  changeOrder(id: $id) {
    id
    orderNum
    description
    changeOrderLineItemList {
      id
      description
      qty
      uom
      scopeArea {
        id
        name
        __typename
      }
      serviceCombo {
        serviceCategoryName
        serviceTypeName
        serviceCodeName
        tradeName
        __typename
      }
      __typename
    }
    __typename
  }
}";

    private readonly IConversationApiClient _conversationApiClient;
    private readonly HttpClient _httpClient;
    private readonly ILogger<ChangeOrderGroupingClient> _logger;

    public ChangeOrderGroupingClient(
        IConversationApiClient conversationApiClient,
        HttpClient httpClient,
        ILogger<ChangeOrderGroupingClient> logger)
    {
        _conversationApiClient = conversationApiClient;
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<ChangeOrderGroupingResult> GetGroupingSuggestionAsync(
        int projectId,
        int? scopeId,
        bool debug,
        CancellationToken cancellationToken)
    {
        var tokenResult = await _conversationApiClient.GetTokenAsync(cancellationToken);
        if (tokenResult is null)
        {
            return ChangeOrderGroupingResult.Error("Failed to get bearer token.");
        }

        var projectScopeList = await CallGraphQlApiAsync(
            tokenResult.Token,
            ProjectScopeListQuery,
            new JsonObject { ["projectId"] = projectId },
            cancellationToken);

        if (projectScopeList is null)
        {
            return ChangeOrderGroupingResult.Error("Failed to fetch project scope and change order list.");
        }

        var changeOrderIds = ExtractChangeOrderIds(projectScopeList, scopeId);
        if (changeOrderIds.Count == 0)
        {
            return ChangeOrderGroupingResult.SuccessJson(JsonSerializer.Serialize(new
            {
                success = true,
                message = "Project scope list fetched successfully, but no change orders were found.",
                projectId,
                projectScopeList,
                changeOrderDetails = Array.Empty<object>(),
                groupingSuggestion = (object?)null
            }));
        }

        var changeOrderDetails = new JsonArray();
        foreach (var id in changeOrderIds)
        {
            var detail = await CallGraphQlApiAsync(
                tokenResult.Token,
                ChangeOrderDetailQuery,
                new JsonObject
                {
                    ["projectId"] = projectId,
                    ["id"] = id
                },
                cancellationToken);

            if (detail is not null)
            {
                changeOrderDetails.Add(detail.DeepClone());
            }
        }

        if (changeOrderDetails.Count == 0)
        {
            return ChangeOrderGroupingResult.Error(
                "Project scope list fetched successfully, but all change order detail requests failed.",
                StatusCodes.Status502BadGateway,
                new JsonObject
                {
                    ["projectId"] = projectId,
                    ["changeOrderIds"] = JsonSerializer.SerializeToNode(changeOrderIds),
                    ["projectScopeList"] = projectScopeList.DeepClone()
                });
        }

        var groupingSuggestion = await CallConversationApiAsync(
            projectId,
            scopeId,
            BuildInstructionPayload(projectId, scopeId, projectScopeList, changeOrderDetails),
            cancellationToken);

        if (debug)
        {
            return ChangeOrderGroupingResult.SuccessJson(JsonSerializer.Serialize(new JsonObject
            {
                ["success"] = true,
                ["message"] = "Successfully fetched project/change order data and generated grouping suggestion.",
                ["projectId"] = projectId,
                ["scopeId"] = scopeId,
                ["changeOrderIds"] = JsonSerializer.SerializeToNode(changeOrderIds),
                ["projectScopeList"] = projectScopeList.DeepClone(),
                ["changeOrderDetails"] = changeOrderDetails.DeepClone(),
                ["groupingSuggestion"] = groupingSuggestion?.DeepClone()
            }));
        }

        if (groupingSuggestion is not null &&
            TryExtractFrontendPayload(groupingSuggestion, out var frontendPayload))
        {
            return ChangeOrderGroupingResult.SuccessJson(frontendPayload.ToJsonString());
        }

        return ChangeOrderGroupingResult.Error(
            "Grouping agent responded, but the response could not be parsed into the frontend bundle JSON format.",
            StatusCodes.Status502BadGateway,
            groupingSuggestion);
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

    private async Task<JsonNode?> CallConversationApiAsync(
        int projectId,
        int? scopeId,
        JsonObject payload,
        CancellationToken cancellationToken)
    {
        try
        {
            var text = payload["text"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            var statesNode = payload["states"] ?? new JsonArray();
            var states = JsonSerializer.SerializeToElement(statesNode);
            var conversationId = $"project-{projectId}-scope-{scopeId?.ToString() ?? "all"}-{Guid.NewGuid():N}";

            var result = await _conversationApiClient.CallConversationAsync(
                conversationId,
                text,
                states,
                GroupingAgentId,
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
            _logger.LogError(ex, "Conversation API call failed.");
            return null;
        }
    }

    private static JsonObject BuildInstructionPayload(
        int projectId,
        int? scopeId,
        JsonNode projectScopeList,
        JsonArray changeOrderDetails)
    {
        var compactChangeOrderDetails = BuildCompactChangeOrderDetails(changeOrderDetails);
        var compactChangeOrderDetailsJson = compactChangeOrderDetails.ToJsonString();

        return new JsonObject
        {
            ["instruction"] = null,
            ["template"] = null,
            ["text"] =
                "PROJECT_ID:\n" +
                projectId + "\n\n" +
                "SCOPE_ID:\n" +
                (scopeId?.ToString() ?? string.Empty) + "\n\n" +
                "CHANGE_ORDER_DETAILS_JSON:\n" +
                compactChangeOrderDetailsJson,
            ["states"] = new JsonArray
            {
                new JsonObject { ["key"] = "projectId", ["value"] = projectId },
                new JsonObject { ["key"] = "scopeId", ["value"] = scopeId },
                new JsonObject { ["key"] = "changeOrderDetails", ["value"] = compactChangeOrderDetails.DeepClone() }
            }
        };
    }

    private static JsonNode BuildCompactProjectScopeList(JsonNode projectScopeList)
    {
        var project = projectScopeList["data"]?["project"];
        var output = new JsonObject
        {
            ["project"] = new JsonObject
            {
                ["id"] = project?["id"]?.DeepClone(),
                ["expectedCompletionDate"] = project?["expectedCompletionDate"]?.DeepClone(),
                ["isLessenEstimate"] = project?["isLessenEstimate"]?.DeepClone(),
                ["scope"] = project?["scope"]?.DeepClone(),
                ["changeOrderList"] = new JsonArray()
            }
        };

        var changeOrderList = project?["changeOrderList"]?.AsArray();
        if (changeOrderList is null)
        {
            return output;
        }

        var compactList = output["project"]?["changeOrderList"]?.AsArray();
        foreach (var changeOrder in changeOrderList)
        {
            compactList?.Add(new JsonObject
            {
                ["id"] = changeOrder?["id"]?.DeepClone(),
                ["description"] = changeOrder?["description"]?.DeepClone(),
                ["orderNum"] = changeOrder?["orderNum"]?.DeepClone(),
                ["status"] = changeOrder?["status"]?["name"]?.DeepClone(),
                ["vendorCost"] = changeOrder?["vendorCost"]?.DeepClone(),
                ["clientNetPrice"] = changeOrder?["clientNetPrice"]?.DeepClone(),
                ["profit"] = changeOrder?["profit"]?.DeepClone(),
                ["margin"] = changeOrder?["margin"]?.DeepClone()
            });
        }

        return output;
    }

    private static JsonArray BuildCompactChangeOrderDetails(JsonArray changeOrderDetails)
    {
        var output = new JsonArray();

        foreach (var detail in changeOrderDetails)
        {
            var changeOrder = detail?["data"]?["changeOrder"];
            if (changeOrder is null)
            {
                continue;
            }

            var lineItems = new JsonArray();
            foreach (var lineItem in changeOrder["changeOrderLineItemList"]?.AsArray() ?? new JsonArray())
            {
                lineItems.Add(new JsonObject
                {
                    ["id"] = lineItem?["id"]?.DeepClone(),
                    ["description"] = lineItem?["description"]?.DeepClone(),
                    ["qty"] = lineItem?["qty"]?.DeepClone(),
                    ["uom"] = lineItem?["uom"]?.DeepClone(),
                    ["scopeArea"] = new JsonObject
                    {
                        ["id"] = lineItem?["scopeArea"]?["id"]?.DeepClone(),
                        ["name"] = lineItem?["scopeArea"]?["name"]?.DeepClone()
                    },
                    ["serviceCombo"] = new JsonObject
                    {
                        ["serviceCategoryName"] = lineItem?["serviceCombo"]?["serviceCategoryName"]?.DeepClone(),
                        ["serviceTypeName"] = lineItem?["serviceCombo"]?["serviceTypeName"]?.DeepClone(),
                        ["serviceCodeName"] = lineItem?["serviceCombo"]?["serviceCodeName"]?.DeepClone(),
                        ["tradeName"] = lineItem?["serviceCombo"]?["tradeName"]?.DeepClone()
                    }
                });
            }

            output.Add(new JsonObject
            {
                ["changeOrder"] = new JsonObject
                {
                    ["id"] = changeOrder["id"]?.DeepClone(),
                    ["orderNum"] = changeOrder["orderNum"]?.DeepClone(),
                    ["description"] = changeOrder["description"]?.DeepClone(),
                    ["lineItems"] = lineItems
                }
            });
        }

        return output;
    }

    private static List<int> ExtractChangeOrderIds(JsonNode projectScopeList, int? requestedChangeOrderId)
    {
        if (requestedChangeOrderId.HasValue)
        {
            return new List<int> { requestedChangeOrderId.Value };
        }

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

    private static bool TryExtractFrontendPayload(JsonNode node, out JsonNode frontendPayload)
    {
        frontendPayload = new JsonObject();

        if (LooksLikeFrontendPayload(node))
        {
            frontendPayload = node.DeepClone();
            return true;
        }

        if (node is JsonValue value && value.TryGetValue<string>(out var text))
        {
            return TryExtractFrontendPayloadFromText(text, out frontendPayload);
        }

        if (node is JsonObject obj)
        {
            foreach (var propertyName in new[] { "data", "text", "content", "message", "response", "result" })
            {
                if (obj[propertyName] is JsonNode child &&
                    TryExtractFrontendPayload(child, out frontendPayload))
                {
                    return true;
                }
            }

            foreach (var child in obj.Select(item => item.Value).Where(valueNode => valueNode is not null))
            {
                if (child is not null && TryExtractFrontendPayload(child, out frontendPayload))
                {
                    return true;
                }
            }
        }

        if (node is JsonArray array)
        {
            foreach (var item in array)
            {
                if (item is not null && TryExtractFrontendPayload(item, out frontendPayload))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TryExtractFrontendPayloadFromText(string text, out JsonNode frontendPayload)
    {
        frontendPayload = new JsonObject();
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        text = text.Trim();
        foreach (var candidate in EnumerateJsonCandidates(text))
        {
            try
            {
                var node = JsonNode.Parse(candidate);
                if (node is not null && LooksLikeFrontendPayload(node))
                {
                    frontendPayload = node;
                    return true;
                }
            }
            catch (JsonException)
            {
                // Keep checking candidates.
            }
        }

        return false;
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

    private static bool LooksLikeFrontendPayload(JsonNode node)
    {
        return node is JsonObject obj &&
            obj["run_id"] is not null &&
            obj["agent"] is not null &&
            obj["payload"]?["scopes"] is JsonArray;
    }
}

public sealed record ChangeOrderGroupingResult(
    bool Success,
    int StatusCode,
    string Message,
    string Json,
    object? DebugData)
{
    public static ChangeOrderGroupingResult SuccessJson(string json)
    {
        return new ChangeOrderGroupingResult(true, StatusCodes.Status200OK, string.Empty, json, null);
    }

    public static ChangeOrderGroupingResult Error(
        string message,
        int statusCode = StatusCodes.Status500InternalServerError,
        object? debugData = null)
    {
        return new ChangeOrderGroupingResult(false, statusCode, message, "{}", debugData);
    }
}
