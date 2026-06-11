using Microsoft.AspNetCore.Mvc;
using WebApplication1.Services;

namespace WebApplication1.Controllers;

[ApiController]
public sealed class ProjectRiskAnalysisController : ControllerBase
{
    private readonly IProjectRiskAnalysisClient _projectRiskAnalysisClient;

    public ProjectRiskAnalysisController(IProjectRiskAnalysisClient projectRiskAnalysisClient)
    {
        _projectRiskAnalysisClient = projectRiskAnalysisClient;
    }

    [HttpGet("/api/projects/risk-analysis")]
    public async Task<IActionResult> GetLatestProjectRisks(
        [FromQuery] int clientId = 1534,
        [FromQuery] string? agentId = null,
        [FromQuery] bool debug = false,
        CancellationToken cancellationToken = default)
    {
        var result = await _projectRiskAnalysisClient.GetLatestProjectRisksAsync(
            clientId,
            agentId,
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
