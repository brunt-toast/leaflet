using Core.Dto;

namespace Api.Services;

public interface IMessageShardDistributionQueue
{
    ValueTask QueueAsync(IReadOnlyCollection<EncryptedMessageDto> messages, CancellationToken cancellationToken);
    IAsyncEnumerable<IReadOnlyCollection<EncryptedMessageDto>> ReadAllAsync(CancellationToken cancellationToken);
}
