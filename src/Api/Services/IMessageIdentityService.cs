namespace Api.Services;

public interface IMessageIdentityService
{
    Task<long> CreateMessageIdAsync(string roomHash, CancellationToken cancellationToken);
    Task ObserveMessageIdAsync(string roomHash, long messageId, CancellationToken cancellationToken);
    Task ObserveMessageIdsAsync(IEnumerable<(string RoomHash, long MessageId)> observedMessages, CancellationToken cancellationToken);
}
