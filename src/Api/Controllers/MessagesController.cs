using Api.Configuration;
using Api.Entities;
using Api.Services;
using AppDbContext = Api.Context.AppContext;
using Core.Dto;
using Core.Requests.Messages;
using Core.Responses.Messages;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Buffers;

namespace Api.Controllers;

[ApiController]
[Route("api/messages")]
public sealed class MessagesController : ControllerBase
{
    private readonly AppDbContext _appContext;
    private readonly IMessageErasureCodingService _messageErasureCodingService;
    private readonly IMessageShardDistributionQueue _messageShardDistributionQueue;
    private readonly IMessageIdentityService _messageIdentityService;
    private readonly IOptions<MessageRequestOptions> _messageRequestOptions;

    public MessagesController(
        AppDbContext appContext,
        IMessageErasureCodingService messageErasureCodingService,
        IMessageShardDistributionQueue messageShardDistributionQueue,
        IMessageIdentityService messageIdentityService,
        IOptions<MessageRequestOptions> messageRequestOptions)
    {
        _appContext = appContext;
        _messageErasureCodingService = messageErasureCodingService;
        _messageShardDistributionQueue = messageShardDistributionQueue;
        _messageIdentityService = messageIdentityService;
        _messageRequestOptions = messageRequestOptions;
    }

    [HttpGet]
    [ProducesResponseType<GetMessagesResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<GetMessagesResponse>> GetMessagesAsync(
        [FromQuery] GetMessagesRequest request,
        CancellationToken cancellationToken)
    {
        await _messageErasureCodingService.EnsureMessagesAvailableAsync(
            request.RoomHash,
            request.MaxId,
            request.NumberToFetch,
            request.SinceId,
            cancellationToken);

        IQueryable<EncryptedMessageEntity> query = _appContext.EncryptedMessages
            .Where(message => message.RoomHash == request.RoomHash && message.Id <= request.MaxId);

        if (request.SinceId.HasValue)
        {
            long sinceId = request.SinceId.Value;
            query = query.Where(message => message.Id > sinceId);
        }

        EncryptedMessageDto[] messages = await query
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
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<CreateMessagesResponse>> CreateMessagesAsync(
        [FromBody] CreateMessagesRequest request,
        CancellationToken cancellationToken)
    {
        MessageRequestOptions options = _messageRequestOptions.Value;
        int maxMessagesPerRequest = options.MaxMessagesPerRequest;
        if (maxMessagesPerRequest > 0 && request.Messages.Length > maxMessagesPerRequest)
        {
            return Problem(
                detail: $"This server accepts a maximum of {maxMessagesPerRequest} messages per request.",
                statusCode: StatusCodes.Status400BadRequest,
                title: "Too many messages.");
        }

        int maxMessageContentBytes = options.MaxMessageContentBytes;
        if (maxMessageContentBytes > 0)
        {
            foreach (EncryptedMessageDto message in request.Messages)
            {
                if (!TryGetDecodedByteCount(message.CypherText, out int decodedByteCount))
                {
                    return Problem(
                        detail: "Message content must be valid base64.",
                        statusCode: StatusCodes.Status400BadRequest,
                        title: "Invalid message content.");
                }

                if (decodedByteCount > maxMessageContentBytes)
                {
                    return Problem(
                        detail: $"Individual message content must not exceed {maxMessageContentBytes} bytes.",
                        statusCode: StatusCodes.Status400BadRequest,
                        title: "Message content too large.");
                }
            }
        }

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
            entity.Id = await _messageIdentityService.CreateMessageIdAsync(entity.RoomHash, cancellationToken);
        }

        await _appContext.EncryptedMessages.AddRangeAsync(entities, cancellationToken);
        await _appContext.SaveChangesAsync(cancellationToken);

        EncryptedMessageDto[] createdMessages = entities.Select(entity => new EncryptedMessageDto
            {
                Id = entity.Id,
                RoomHash = entity.RoomHash,
                SenderPublicKey = entity.SenderPublicKey,
                Nonce = entity.Nonce,
                CypherText = entity.CypherText,
                Signature = entity.Signature,
            })
            .ToArray();
        await _messageShardDistributionQueue.QueueAsync(createdMessages, cancellationToken);

        return Ok(new CreateMessagesResponse());
    }

    private static bool TryGetDecodedByteCount(string base64Value, out int decodedByteCount)
    {
        decodedByteCount = 0;
        byte[] rentedBuffer = ArrayPool<byte>.Shared.Rent(base64Value.Length);

        try
        {
            if (!Convert.TryFromBase64String(base64Value, rentedBuffer, out decodedByteCount))
            {
                decodedByteCount = 0;
                return false;
            }

            return true;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rentedBuffer);
        }
    }
}
