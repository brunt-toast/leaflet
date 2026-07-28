using Core.Dto;

namespace Api.Services;

public interface IPeerSyncService
{
    Task<IReadOnlyCollection<KnownServerDto>> GetOnlineServersAsync(CancellationToken cancellationToken);
    Task PollRequesterAsync(string requesterUrl, CancellationToken cancellationToken);
    Task PollKnownPeersAsync(CancellationToken cancellationToken);
}
