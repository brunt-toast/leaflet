using Api.Entities;
using AppDbContext = Api.Context.AppContext;
using Core.Dto;
using Core.Requests.Messages;
using Core.Responses.Messages;
using Microsoft.AspNetCore.Mvc;

namespace Api.Controllers;

[ApiController]
[Route("api/messages")]
public sealed class MessagesController(AppDbContext appContext) : ControllerBase
{
    [HttpPost]
    [ProducesResponseType<CreateMessagesResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<CreateMessagesResponse>> CreateMessagesAsync(
        [FromBody] CreateMessagesRequest request,
        CancellationToken cancellationToken)
    {
        EncryptedMessageEntity[] entities = request.Messages
            .Select(message => new EncryptedMessageEntity
            {
                RoomHash = message.RoomHash,
                SenderPublicKey = message.SenderPublicKey,
                Nonce = message.Nonce,
                CypherText = message.CypherText,
                Signature = message.Signature,
            })
            .ToArray();

        await appContext.EncryptedMessages.AddRangeAsync(entities, cancellationToken);
        await appContext.SaveChangesAsync(cancellationToken);

        return Ok(new CreateMessagesResponse());
    }
}
