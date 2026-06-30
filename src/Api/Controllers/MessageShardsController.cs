using Api.Requests;
using Api.Responses;
using Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace Api.Controllers;

[ApiController]
[ApiExplorerSettings(IgnoreApi = true)]
[Route("api/internal/message-shards")]
public sealed class MessageShardsController(IMessageErasureCodingService messageErasureCodingService) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<GetMessageShardsResponse>> GetMessageShardsAsync(
        [FromQuery] string roomHash,
        [FromQuery] long maxId,
        [FromQuery] int numberToFetch,
        CancellationToken cancellationToken)
    {
        return Ok(await messageErasureCodingService.GetStoredMessageShardsAsync(
            roomHash,
            maxId,
            numberToFetch,
            cancellationToken));
    }

    [HttpPost]
    public async Task<IActionResult> StoreMessageShardsAsync(
        [FromBody] StoreMessageShardsRequest request,
        CancellationToken cancellationToken)
    {
        await messageErasureCodingService.StoreMessageShardsAsync(request, cancellationToken);
        return Ok();
    }
}
