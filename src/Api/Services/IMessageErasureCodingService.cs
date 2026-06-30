using Api.Dto;
using Api.Requests;
using Api.Responses;
using Core.Dto;

namespace Api.Services;

public interface IMessageErasureCodingService
{
    Task EnsureMessagesAvailableAsync(string roomHash, long maxId, int numberToFetch, CancellationToken cancellationToken);
    Task DistributeMessageShardsAsync(IEnumerable<EncryptedMessageDto> messages, CancellationToken cancellationToken);
    Task RepairShardIntegrityAsync(CancellationToken cancellationToken);
    Task StoreMessageShardsAsync(StoreMessageShardsRequest request, CancellationToken cancellationToken);
    Task<GetMessageShardsResponse> GetStoredMessageShardsAsync(string roomHash, long maxId, int numberToFetch, CancellationToken cancellationToken);
}
