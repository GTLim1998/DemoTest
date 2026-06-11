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

        var compactChangeOrderDetails = BuildCompactChangeOrderDetails(changeOrderDetails);
        var groupingDecision = await CallConversationApiAsync(
            projectId,
            scopeId,
            BuildInstructionPayload(projectId, scopeId, compactChangeOrderDetails),
            cancellationToken);

        JsonNode? frontendPayload = null;
        if (groupingDecision is not null &&
            TryExtractGroupingDecision(groupingDecision, out var aiDecision))
        {
            frontendPayload = BuildFrontendPayload(projectId, scopeId, compactChangeOrderDetails, aiDecision);
        }

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
                ["compactChangeOrderDetails"] = compactChangeOrderDetails.DeepClone(),
                ["groupingDecision"] = groupingDecision?.DeepClone(),
                ["frontendPayload"] = frontendPayload?.DeepClone()
            }));
        }

        if (frontendPayload is not null)
        {
            return ChangeOrderGroupingResult.SuccessJson(frontendPayload.ToJsonString());
        }

        return ChangeOrderGroupingResult.Error(
            "Grouping agent responded, but the response could not be parsed into the bundle decision format.",
            StatusCodes.Status502BadGateway,
            groupingDecision);
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
        JsonArray compactChangeOrderDetails)
    {
        var aiInput = BuildAiInput(projectId, scopeId, compactChangeOrderDetails);
        var aiInputJson = aiInput.ToJsonString();

        return new JsonObject
        {
            ["instruction"] = null,
            ["template"] = null,
            ["text"] = "BUNDLING_INPUT_JSON:\n" + aiInputJson,
            ["states"] = new JsonArray
            {
                new JsonObject { ["key"] = "projectId", ["value"] = projectId },
                new JsonObject { ["key"] = "scopeId", ["value"] = scopeId },
                new JsonObject { ["key"] = "bundlingInput", ["value"] = aiInput.DeepClone() }
            }
        };
    }

    private static JsonObject BuildAiInput(
        int projectId,
        int? scopeId,
        JsonArray compactChangeOrderDetails)
    {
        var firstChangeOrder = compactChangeOrderDetails
            .FirstOrDefault()?["changeOrder"];
        var lineItems = new JsonArray();

        foreach (var detail in compactChangeOrderDetails)
        {
            var changeOrder = detail?["changeOrder"];
            if (changeOrder is null)
            {
                continue;
            }

            foreach (var lineItem in changeOrder["lineItems"]?.AsArray() ?? new JsonArray())
            {
                var serviceCombo = lineItem?["serviceCombo"];
                lineItems.Add(new JsonObject
                {
                    ["id"] = GetString(lineItem?["id"]),
                    ["description"] = Truncate(GetString(lineItem?["description"]), 160),
                    ["area"] = GetString(lineItem?["scopeArea"]?["name"]),
                    ["trade"] = GetString(serviceCombo?["tradeName"]),
                    ["category"] = GetString(serviceCombo?["serviceCategoryName"]),
                    ["type"] = GetString(serviceCombo?["serviceTypeName"]),
                    ["code"] = GetString(serviceCombo?["serviceCodeName"]),
                    ["qty"] = lineItem?["qty"]?.DeepClone(),
                    ["uom"] = GetString(lineItem?["uom"])
                });
            }
        }

        return new JsonObject
        {
            ["project_id"] = projectId.ToString(),
            ["scope_id"] = (scopeId ?? GetInt(firstChangeOrder?["id"]) ?? 0).ToString(),
            ["scope_name"] = GetString(firstChangeOrder?["description"])
                ?? $"Change Order {GetString(firstChangeOrder?["orderNum"]) ?? string.Empty}".Trim(),
            ["line_items"] = lineItems
        };
    }

    private static JsonNode BuildFrontendPayload(
        int projectId,
        int? scopeId,
        JsonArray compactChangeOrderDetails,
        JsonObject aiDecision)
    {
        var aiBundles = aiDecision["bundles"]?.AsArray() ?? new JsonArray();
        var aiUnassigned = ReadStringArray(aiDecision["unassigned_line_work_ids"]).ToHashSet();
        var scopes = new JsonArray();
        var allBundleConfidences = new List<double>();
        var totalBundleCount = 0;

        foreach (var detail in compactChangeOrderDetails)
        {
            var changeOrder = detail?["changeOrder"];
            if (changeOrder is null)
            {
                continue;
            }

            var effectiveScopeId = (scopeId ?? GetInt(changeOrder["id"]) ?? 0).ToString();
            var sourceLineItems = changeOrder["lineItems"]?.AsArray() ?? new JsonArray();
            var lineItemsById = sourceLineItems
                .Where(item => item is not null)
                .Select(item => item!.AsObject())
                .Select(item => new { Id = GetString(item["id"]), Item = item })
                .Where(item => !string.IsNullOrWhiteSpace(item.Id))
                .ToDictionary(item => item.Id!, item => item.Item);

            var bundles = new JsonArray();
            var bundledIds = new HashSet<string>();
            var sequence = 1;

            foreach (var aiBundleNode in aiBundles)
            {
                if (aiBundleNode is not JsonObject aiBundle)
                {
                    continue;
                }

                var ids = ReadStringArray(aiBundle["line_work_ids"])
                    .Where(lineItemsById.ContainsKey)
                    .Distinct()
                    .ToList();

                if (ids.Count == 0)
                {
                    continue;
                }

                foreach (var id in ids)
                {
                    bundledIds.Add(id);
                }

                var groupedLineItems = ids.Select(id => lineItemsById[id]).ToList();
                var confidence = GetDouble(aiBundle["confidence"]) ?? 0.8;
                allBundleConfidences.Add(confidence);

                bundles.Add(new JsonObject
                {
                    ["bundle_id"] = GetString(aiBundle["bundle_id"]) ?? $"bundle-{sequence}",
                    ["title"] = GetString(aiBundle["title"]) ?? BuildBundleTitle(groupedLineItems),
                    ["trade"] = Slugify(ResolveTradeDisplay(groupedLineItems, GetString(aiBundle["grouped_by"]))),
                    ["trade_display"] = ResolveTradeDisplay(groupedLineItems, GetString(aiBundle["grouped_by"])),
                    ["line_work_ids"] = ToJsonArray(ids),
                    ["reason"] = GetString(aiBundle["reason"]) ?? "Grouped by similar work signals from the provided line items.",
                    ["grouping_factors"] = BuildGroupingFactors(aiBundle, groupedLineItems),
                    ["confidence"] = Math.Round(confidence, 2),
                    ["estimated_hours"] = EstimateHours(groupedLineItems)
                });

                sequence++;
                totalBundleCount++;
            }

            var unassignedIds = lineItemsById.Keys
                .Where(id => !bundledIds.Contains(id) || aiUnassigned.Contains(id))
                .Distinct()
                .ToList();

            scopes.Add(new JsonObject
            {
                ["scope_id"] = $"change-order-{effectiveScopeId}",
                ["scope_name"] = GetString(changeOrder["description"])
                    ?? $"Change Order {GetString(changeOrder["orderNum"]) ?? string.Empty}".Trim(),
                ["total_line_works"] = lineItemsById.Count,
                ["analyzed_line_works"] = lineItemsById.Count,
                ["coverage"] = new JsonObject
                {
                    ["bundled"] = bundledIds.Count,
                    ["unassigned"] = unassignedIds.Count
                },
                ["bundles"] = bundles,
                ["unassigned_line_work_ids"] = ToJsonArray(unassignedIds)
            });
        }

        var runScopeId = (scopeId ?? GetInt(compactChangeOrderDetails.FirstOrDefault()?["changeOrder"]?["id"]) ?? 0).ToString();
        var averageConfidence = allBundleConfidences.Count == 0
            ? 0
            : Math.Round(allBundleConfidences.Average(), 2);

        return new JsonObject
        {
            ["run_id"] = $"run_project_{projectId}_scope_{runScopeId}",
            ["agent"] = "change-order-bundling-agent",
            ["agent_version"] = "1.0.0",
            ["project_id"] = projectId.ToString(),
            ["trigger"] = "scope_synced",
            ["generated_at"] = "2026-06-10T00:00:00Z",
            ["timezone"] = "UTC",
            ["status"] = "completed",
            ["confidence"] = averageConfidence,
            ["summary"] = $"Recommended {totalBundleCount} bundles for scope change-order-{runScopeId}.",
            ["requires_approval"] = true,
            ["payload"] = new JsonObject
            {
                ["scopes"] = scopes
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

    private static bool TryExtractGroupingDecision(JsonNode node, out JsonObject groupingDecision)
    {
        groupingDecision = new JsonObject();

        if (LooksLikeGroupingDecision(node))
        {
            groupingDecision = node.DeepClone().AsObject();
            return true;
        }

        if (node is JsonValue value && value.TryGetValue<string>(out var text))
        {
            return TryExtractGroupingDecisionFromText(text, out groupingDecision);
        }

        if (node is JsonObject obj)
        {
            foreach (var propertyName in new[] { "apiResult", "data", "text", "content", "message", "response", "result" })
            {
                if (obj[propertyName] is JsonNode child &&
                    TryExtractGroupingDecision(child, out groupingDecision))
                {
                    return true;
                }
            }

            foreach (var child in obj.Select(item => item.Value).Where(valueNode => valueNode is not null))
            {
                if (child is not null && TryExtractGroupingDecision(child, out groupingDecision))
                {
                    return true;
                }
            }
        }

        if (node is JsonArray array)
        {
            foreach (var item in array)
            {
                if (item is not null && TryExtractGroupingDecision(item, out groupingDecision))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TryExtractGroupingDecisionFromText(string text, out JsonObject groupingDecision)
    {
        groupingDecision = new JsonObject();
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
                if (node is not null && LooksLikeGroupingDecision(node))
                {
                    groupingDecision = node.AsObject();
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

    private static bool LooksLikeGroupingDecision(JsonNode node)
    {
        return node is JsonObject obj && obj["bundles"] is JsonArray;
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

            if (value.TryGetValue<long>(out var longValue))
            {
                return longValue.ToString();
            }

            if (value.TryGetValue<double>(out var doubleValue))
            {
                return doubleValue.ToString("0.##");
            }
        }

        return node.ToJsonString();
    }

    private static int? GetInt(JsonNode? node)
    {
        if (node is null)
        {
            return null;
        }

        if (node is JsonValue value)
        {
            if (value.TryGetValue<int>(out var intValue))
            {
                return intValue;
            }

            if (value.TryGetValue<string>(out var stringValue) &&
                int.TryParse(stringValue, out var parsedValue))
            {
                return parsedValue;
            }
        }

        return null;
    }

    private static double? GetDouble(JsonNode? node)
    {
        if (node is null)
        {
            return null;
        }

        if (node is JsonValue value)
        {
            if (value.TryGetValue<double>(out var doubleValue))
            {
                return doubleValue;
            }

            if (value.TryGetValue<decimal>(out var decimalValue))
            {
                return (double)decimalValue;
            }

            if (value.TryGetValue<string>(out var stringValue) &&
                double.TryParse(stringValue, out var parsedValue))
            {
                return parsedValue;
            }
        }

        return null;
    }

    private static string? Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length <= maxLength)
        {
            return value;
        }

        return value[..maxLength].TrimEnd();
    }

    private static List<string> ReadStringArray(JsonNode? node)
    {
        if (node is not JsonArray array)
        {
            return new List<string>();
        }

        return array
            .Select(GetString)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToList();
    }

    private static JsonArray ToJsonArray(IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values)
        {
            array.Add(value);
        }

        return array;
    }

    private static string ResolveTradeDisplay(List<JsonObject> lineItems, string? groupedBy)
    {
        var first = lineItems.FirstOrDefault();
        if (first is null)
        {
            return "Mixed";
        }

        var trades = DistinctNonEmpty(lineItems, item => GetString(item["serviceCombo"]?["tradeName"]));
        var categories = DistinctNonEmpty(lineItems, item => GetString(item["serviceCombo"]?["serviceCategoryName"]));

        if (string.Equals(groupedBy, "category", StringComparison.OrdinalIgnoreCase) && categories.Count == 1)
        {
            return categories[0];
        }

        if (trades.Count == 1)
        {
            return trades[0];
        }

        if (categories.Count == 1)
        {
            return categories[0];
        }

        return "Mixed";
    }

    private static string BuildBundleTitle(List<JsonObject> lineItems)
    {
        var tradeDisplay = ResolveTradeDisplay(lineItems, null);
        var areas = DistinctNonEmpty(lineItems, item => GetString(item["scopeArea"]?["name"]));
        var types = DistinctNonEmpty(lineItems, item => GetString(item["serviceCombo"]?["serviceTypeName"]));
        var titleParts = new List<string> { tradeDisplay };

        if (types.Count == 1)
        {
            titleParts.Add(types[0]);
        }

        if (areas.Count == 1)
        {
            titleParts.Add($"work in {areas[0]}");
        }

        return string.Join(" - ", titleParts);
    }

    private static JsonArray BuildGroupingFactors(JsonObject aiBundle, List<JsonObject> lineItems)
    {
        var factors = new JsonArray();
        var groupedBy = GetString(aiBundle["grouped_by"]);
        if (!string.IsNullOrWhiteSpace(groupedBy))
        {
            factors.Add(groupedBy);
        }

        if (DistinctNonEmpty(lineItems, item => GetString(item["serviceCombo"]?["tradeName"])).Count == 1)
        {
            factors.Add("shared trade ownership");
        }

        if (DistinctNonEmpty(lineItems, item => GetString(item["scopeArea"]?["name"])).Count == 1)
        {
            factors.Add("same work area");
        }

        if (DistinctNonEmpty(lineItems, item => GetString(item["serviceCombo"]?["serviceCategoryName"])).Count == 1)
        {
            factors.Add("similar service category");
        }

        return factors;
    }

    private static double EstimateHours(List<JsonObject> lineItems)
    {
        var total = 0.0;
        foreach (var lineItem in lineItems)
        {
            var uom = GetString(lineItem["uom"]);
            if (uom is null ||
                (!uom.Contains("hour", StringComparison.OrdinalIgnoreCase) &&
                 !uom.Equals("hr", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            total += GetDouble(lineItem["qty"]) ?? 0;
        }

        return Math.Round(total, 2);
    }

    private static List<string> DistinctNonEmpty(
        IEnumerable<JsonObject> items,
        Func<JsonObject, string?> selector)
    {
        return items
            .Select(selector)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string Slugify(string value)
    {
        var chars = value
            .ToLowerInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '-')
            .ToArray();

        var slug = new string(chars);
        while (slug.Contains("--", StringComparison.Ordinal))
        {
            slug = slug.Replace("--", "-", StringComparison.Ordinal);
        }

        return slug.Trim('-');
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
