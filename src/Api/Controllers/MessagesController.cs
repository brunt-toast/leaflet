using Api.Entities;
using AppDbContext = Api.Context.AppContext;
using Core.Dto;
using Core.Requests.Messages;
using Core.Responses.Messages;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

[ApiController]
[Route("api/messages")]
public sealed class MessagesController(AppDbContext appContext) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<GetMessagesResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<GetMessagesResponse>> GetMessagesAsync(
        [FromQuery] GetMessagesRequest request,
        CancellationToken cancellationToken)
    {
        EncryptedMessageDto[] messages = await appContext.EncryptedMessages
            .Where(message => message.RoomHash == request.RoomHash && message.Id <= request.MaxId)
            .OrderByDescending(message => message.Id)
            .Take(request.NumberToFetch)
            .Select(message => new EncryptedMessageDto
            {
                Id = message.Id,
                RoomHash = message.RoomHash,
                SenderPublicKey = message.SenderPublicKey,
                Nonce = message.Nonce,
                CypherText = message.CypherText,
                Signature = message.Signature,
            })
            .ToArrayAsync(cancellationToken);

        Array.Sort(messages, static (left, right) => left.Id.CompareTo(right.Id));

        return Ok(new GetMessagesResponse
        {
            Messages = messages,
        });
    }

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
