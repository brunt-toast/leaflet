using Api.Services;
using Core.Responses.Servers;
using Microsoft.AspNetCore.Mvc;

namespace Api.Controllers;

[ApiController]
[Route("api/servers")]
public sealed class ServersController(IPeerSyncService peerSyncService) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<GetKnownServersResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<GetKnownServersResponse>> GetKnownServersAsync(
        [FromQuery] string? requesterUrl,
        [FromQuery] bool suppressCallback,
        CancellationToken cancellationToken)
    {
        if (!suppressCallback && !string.IsNullOrWhiteSpace(requesterUrl))
        {
            await peerSyncService.PollRequesterAsync(requesterUrl, cancellationToken);
        }

        return Ok(new GetKnownServersResponse
        {
            Servers = (await peerSyncService.GetOnlineServersAsync(cancellationToken)).ToArray(),
        });
    }
}
