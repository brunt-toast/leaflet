using System.Threading.Channels;
using Core.Dto;

namespace Api.Services;

internal sealed class MessageShardDistributionQueue : IMessageShardDistributionQueue
{
    private readonly Channel<IReadOnlyCollection<EncryptedMessageDto>> _channel;

    public MessageShardDistributionQueue()
    {
        _channel = Channel.CreateUnbounded<IReadOnlyCollection<EncryptedMessageDto>>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
    }

    public ValueTask QueueAsync(IReadOnlyCollection<EncryptedMessageDto> messages, CancellationToken cancellationToken)
    {
        return _channel.Writer.WriteAsync(messages, cancellationToken);
    }

    public IAsyncEnumerable<IReadOnlyCollection<EncryptedMessageDto>> ReadAllAsync(CancellationToken cancellationToken)
    {
        return _channel.Reader.ReadAllAsync(cancellationToken);
    }
}
