using Microsoft.AspNetCore.Mvc;
using WebApplication1.Models;
using WebApplication1.Services;

namespace WebApplication1.Controllers;

[ApiController]
public sealed class OneBrainController : ControllerBase
{
    private readonly IConversationApiClient _conversationApiClient;

    public OneBrainController(IConversationApiClient conversationApiClient)
    {
        _conversationApiClient = conversationApiClient;
    }

    [HttpPost("/api/auth/token")]
    public async Task<IActionResult> GetToken(CancellationToken cancellationToken)
    {
        var tokenResult = await _conversationApiClient.GetTokenAsync(cancellationToken);
        if (tokenResult is null)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, new
            {
                status = "error",
                message = "Failed to get token"
            });
        }

        return Ok(new
        {
            status = "success",
            token = tokenResult.Token,
            expiresAt = tokenResult.ExpiresAt,
            fromCache = tokenResult.FromCache,
            rawResponse = tokenResult.RawResponse
        });
    }

    [HttpPost("/api/conversation/{agentId}/{conversation_id}")]
    public async Task<IActionResult> CallConversation(
        [FromRoute] string agentId,
        [FromRoute(Name = "conversation_id")] string conversationId,
        [FromBody] ConversationRequest? request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(agentId))
        {
            return BadRequest(new
            {
                status = "error",
                message = "agentId is required"
            });
        }

        if (string.IsNullOrWhiteSpace(conversationId))
        {
            return BadRequest(new
            {
                status = "error",
                message = "conversation_id is required"
            });
        }

        if (request is null)
        {
            return BadRequest(new
            {
                status = "error",
                message = "JSON body is required",
                example = new
                {
                    text = "Hello, API!",
                    states = Array.Empty<object>()
                }
            });
        }

        var text = request.GetText();
        if (string.IsNullOrWhiteSpace(text))
        {
            return BadRequest(new
            {
                status = "error",
                message = "No 'text' field provided or text is empty",
                example = new
                {
                    text = "Hello, API!",
                    states = Array.Empty<object>()
                }
            });
        }

        var result = await _conversationApiClient.CallConversationAsync(
            conversationId,
            text,
            request.States.GetValueOrDefault(),
            agentId,
            cancellationToken);

        if (result is null)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, new
            {
                status = "error",
                message = "External conversation API call failed"
            });
        }

        return Ok(new
        {
            status = "success",
            apiResult = result
        });
    }
}
