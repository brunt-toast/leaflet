using Api.Entities;
using Api.Services;
using AppDbContext = Api.Context.AppContext;
using Core.Dto;
using Core.Requests.Messages;
using Core.Responses.Messages;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

[ApiController]
[Route("api/messages")]
public sealed class MessagesController(
    AppDbContext appContext,
    IMessageErasureCodingService messageErasureCodingService,
    IMessageIdentityService messageIdentityService) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<GetMessagesResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<GetMessagesResponse>> GetMessagesAsync(
        [FromQuery] GetMessagesRequest request,
        CancellationToken cancellationToken)
    {
        await messageErasureCodingService.EnsureMessagesAvailableAsync(
            request.RoomHash,
            request.MaxId,
            request.NumberToFetch,
            cancellationToken);

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
            .Select(static message => new EncryptedMessageEntity
            {
                RoomHash = message.RoomHash,
                SenderPublicKey = message.SenderPublicKey,
                Nonce = message.Nonce,
                CypherText = message.CypherText,
                Signature = message.Signature,
            })
            .ToArray();

        foreach (EncryptedMessageEntity entity in entities)
        {
            entity.Id = await messageIdentityService.CreateMessageIdAsync(entity.RoomHash, cancellationToken);
        }

        await appContext.EncryptedMessages.AddRangeAsync(entities, cancellationToken);
        await appContext.SaveChangesAsync(cancellationToken);
        await messageErasureCodingService.DistributeMessageShardsAsync(
            entities.Select(entity => new EncryptedMessageDto
            {
                Id = entity.Id,
                RoomHash = entity.RoomHash,
                SenderPublicKey = entity.SenderPublicKey,
                Nonce = entity.Nonce,
                CypherText = entity.CypherText,
                Signature = entity.Signature,
            }),
            cancellationToken);

        return Ok(new CreateMessagesResponse());
    }
}
