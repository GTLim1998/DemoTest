using Microsoft.AspNetCore.Mvc;
using WebApplication1.Services;

namespace WebApplication1.Controllers;

[ApiController]
public sealed class ChangeOrderGroupingController : ControllerBase
{
    private readonly IChangeOrderGroupingClient _changeOrderGroupingClient;

    public ChangeOrderGroupingController(IChangeOrderGroupingClient changeOrderGroupingClient)
    {
        _changeOrderGroupingClient = changeOrderGroupingClient;
    }

    [HttpGet("/api/projects/{projectId:int}/change-orders/grouping-suggestion")]
    public async Task<IActionResult> GetGroupingSuggestion(
        [FromRoute] int projectId,
        [FromQuery] int? scopeId,
        [FromQuery] int? changeOrderId,
        [FromQuery] bool debug,
        CancellationToken cancellationToken)
    {
        var requestedScopeId = scopeId ?? changeOrderId;
        var result = await _changeOrderGroupingClient.GetGroupingSuggestionAsync(
            projectId,
            requestedScopeId,
            debug,
            cancellationToken);

        if (!result.Success)
        {
            return StatusCode(result.StatusCode, new
            {
                success = false,
                message = result.Message,
                data = result.DebugData
            });
        }

        return Content(result.Json, "application/json");
    }

    [HttpGet("/api/projects/{projectId:int}/scopes/{scopeId:int}/grouping-suggestion")]
    public async Task<IActionResult> GetGroupingSuggestionByScope(
        [FromRoute] int projectId,
        [FromRoute] int scopeId,
        [FromQuery] bool debug,
        CancellationToken cancellationToken)
    {
        var result = await _changeOrderGroupingClient.GetGroupingSuggestionAsync(
            projectId,
            scopeId,
            debug,
            cancellationToken);

        if (!result.Success)
        {
            return StatusCode(result.StatusCode, new
            {
                success = false,
                message = result.Message,
                data = result.DebugData
            });
        }

        return Content(result.Json, "application/json");
    }
}
