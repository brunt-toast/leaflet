using Api.Services;
using Core.Responses.Servers;
using Microsoft.AspNetCore.Mvc;

namespace Api.Controllers;

[ApiController]
[Route("api/servers")]
public sealed class ServersController : ControllerBase
{
    private readonly IPeerSyncService _peerSyncService;

    public ServersController(IPeerSyncService peerSyncService)
    {
        _peerSyncService = peerSyncService;
    }

    [HttpGet]
    [ProducesResponseType<GetKnownServersResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<GetKnownServersResponse>> GetKnownServersAsync(
        [FromQuery] string? requesterUrl,
        [FromQuery] bool suppressCallback,
        CancellationToken cancellationToken)
    {
        if (!suppressCallback && !string.IsNullOrWhiteSpace(requesterUrl))
        {
            await _peerSyncService.PollRequesterAsync(requesterUrl, cancellationToken);
        }

        return Ok(new GetKnownServersResponse
        {
            Servers = (await _peerSyncService.GetOnlineServersAsync(cancellationToken)).ToArray(),
        });
    }
}
