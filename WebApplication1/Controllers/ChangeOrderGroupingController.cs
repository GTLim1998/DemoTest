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
        [FromQuery] int? changeOrderId,
        [FromQuery] bool debug,
        CancellationToken cancellationToken)
    {
        var result = await _changeOrderGroupingClient.GetGroupingSuggestionAsync(
            projectId,
            changeOrderId,
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
