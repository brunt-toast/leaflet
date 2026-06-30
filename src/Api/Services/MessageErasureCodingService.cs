using System.Text.Json;
using Api.Configuration;
using Api.Dto;
using Api.Entities;
using Api.Requests;
using Api.Responses;
using AppDbContext = Api.Context.AppContext;
using Core.Dto;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Witteborn.ReedSolomon;

namespace Api.Services;

internal sealed class MessageErasureCodingService(
    AppDbContext appContext,
    IHttpClientFactory httpClientFactory,
    IOptions<PeerSyncOptions> peerSyncOptions,
    IOptions<ErasureCodingOptions> erasureCodingOptions,
    ILogger<MessageErasureCodingService> logger) : IMessageErasureCodingService
{
    private const string MessageShardsEndpointPath = "/api/internal/message-shards";
    private readonly PeerSyncOptions _peerSyncOptions = peerSyncOptions.Value;
    private readonly ErasureCodingOptions _erasureCodingOptions = erasureCodingOptions.Value;

    public async Task EnsureMessagesAvailableAsync(string roomHash, long maxId, int numberToFetch, CancellationToken cancellationToken)
    {
        int localCount = await appContext.EncryptedMessages
            .Where(message => message.RoomHash == roomHash && message.Id <= maxId)
            .CountAsync(cancellationToken);

        if (localCount >= numberToFetch)
        {
            return;
        }

        MessageShardDto[] localShards = await GetLocalStoredMessageShardsAsync(roomHash, maxId, numberToFetch, cancellationToken);
        MessageShardDto[] remoteShards = await FetchRemoteShardsAsync(roomHash, maxId, numberToFetch, cancellationToken);
        MessageShardDto[] availableShards = localShards
            .Concat(remoteShards)
            .ToArray();

        if (availableShards.Length == 0)
        {
            return;
        }

        HashSet<long> existingMessageIds = await appContext.EncryptedMessages
            .Where(message => message.RoomHash == roomHash && message.Id <= maxId)
            .Select(message => message.Id)
            .ToHashSetAsync(cancellationToken);

        List<EncryptedMessageEntity> recoveredMessages = [];

        foreach (IGrouping<long, MessageShardDto> group in availableShards
            .GroupBy(shard => shard.MessageId)
            .OrderByDescending(group => group.Key))
        {
            if (existingMessageIds.Contains(group.Key))
            {
                continue;
            }

            EncryptedMessageDto? recoveredMessage = TryReconstructMessage(group);
            if (recoveredMessage is null)
            {
                continue;
            }

            EncryptedMessageEntity entity = new()
            {
                Id = recoveredMessage.Id,
                RoomHash = recoveredMessage.RoomHash,
                SenderPublicKey = recoveredMessage.SenderPublicKey,
                Nonce = recoveredMessage.Nonce,
                CypherText = recoveredMessage.CypherText,
                Signature = recoveredMessage.Signature,
            };

            recoveredMessages.Add(entity);
            existingMessageIds.Add(entity.Id);

            if (existingMessageIds.Count >= numberToFetch)
            {
                break;
            }
        }

        if (recoveredMessages.Count == 0)
        {
            return;
        }

        await appContext.EncryptedMessages.AddRangeAsync(recoveredMessages, cancellationToken);
        await appContext.SaveChangesAsync(cancellationToken);
    }

    public async Task DistributeMessageShardsAsync(IEnumerable<EncryptedMessageDto> messages, CancellationToken cancellationToken)
    {
        ValidateErasureCodingOptions();

        EncryptedMessageDto[] messageArray = messages.ToArray();
        if (messageArray.Length == 0)
        {
            return;
        }

        List<KnownServerEntity> candidatePeers = await GetShardPlacementPeersAsync(cancellationToken);
        if (candidatePeers.Count == 0)
        {
            logger.LogWarning("Skipping erasure coding distribution because no active peers are available.");
            return;
        }

        int totalShardCount = _erasureCodingOptions.DataShards + _erasureCodingOptions.ParityShards;
        string[] orderedPeerUrls = OrderPeersForShardPlacement(candidatePeers)
            .Select(peer => peer.Url)
            .ToArray();

        Dictionary<string, List<MessageShardDto>> shardsByPeerUrl = [];

        foreach (EncryptedMessageDto dto in messageArray)
        {
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(dto);

            ReedSolomon reedSolomon = new(_erasureCodingOptions.DataShards, _erasureCodingOptions.ParityShards);
            int paddingSize = reedSolomon.GetPaddingSize(payload.Length);
            byte[][] shards = reedSolomon.ManagedEncode(payload);

            for (int shardIndex = 0; shardIndex < totalShardCount; shardIndex++)
            {
                string peerUrl = orderedPeerUrls[shardIndex % orderedPeerUrls.Length];
                if (!shardsByPeerUrl.TryGetValue(peerUrl, out List<MessageShardDto>? peerShards))
                {
                    peerShards = [];
                    shardsByPeerUrl[peerUrl] = peerShards;
                }

                peerShards.Add(new MessageShardDto
                {
                    RoomHash = dto.RoomHash,
                    MessageId = dto.Id,
                    DataShardCount = _erasureCodingOptions.DataShards,
                    ParityShardCount = _erasureCodingOptions.ParityShards,
                    PaddingSize = paddingSize,
                    ShardIndex = shardIndex,
                    ShardData = shards[shardIndex],
                });
            }
        }

        foreach ((string peerUrl, List<MessageShardDto> peerShards) in shardsByPeerUrl)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await PushShardsToPeerAsync(peerUrl, peerShards, cancellationToken);
        }
    }

    public async Task StoreMessageShardsAsync(StoreMessageShardsRequest request, CancellationToken cancellationToken)
    {
        if (request.MessageShards.Length == 0)
        {
            return;
        }

        string roomHash = request.MessageShards[0].RoomHash;
        long[] messageIds = request.MessageShards
            .Select(shard => shard.MessageId)
            .Distinct()
            .ToArray();

        MessageShardEntity[] existingShards = await appContext.MessageShards
            .Where(shard => shard.RoomHash == roomHash && messageIds.Contains(shard.MessageId))
            .ToArrayAsync(cancellationToken);

        foreach (MessageShardDto shard in request.MessageShards)
        {
            MessageShardEntity? entity = existingShards.SingleOrDefault(existing =>
                existing.RoomHash == shard.RoomHash &&
                existing.MessageId == shard.MessageId &&
                existing.ShardIndex == shard.ShardIndex);

            if (entity is null)
            {
                entity = new MessageShardEntity
                {
                    RoomHash = shard.RoomHash,
                    MessageId = shard.MessageId,
                    DataShardCount = shard.DataShardCount,
                    ParityShardCount = shard.ParityShardCount,
                    PaddingSize = shard.PaddingSize,
                    ShardIndex = shard.ShardIndex,
                    ShardData = shard.ShardData,
                    StoredAtUtc = DateTime.UtcNow,
                };

                await appContext.MessageShards.AddAsync(entity, cancellationToken);
                continue;
            }

            entity.DataShardCount = shard.DataShardCount;
            entity.ParityShardCount = shard.ParityShardCount;
            entity.PaddingSize = shard.PaddingSize;
            entity.ShardData = shard.ShardData;
            entity.StoredAtUtc = DateTime.UtcNow;
        }

        await appContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<GetMessageShardsResponse> GetStoredMessageShardsAsync(
        string roomHash,
        long maxId,
        int numberToFetch,
        CancellationToken cancellationToken)
    {
        int totalShardCount = _erasureCodingOptions.DataShards + _erasureCodingOptions.ParityShards;

        long[] messageIds = await appContext.MessageShards
            .Where(shard => shard.RoomHash == roomHash && shard.MessageId <= maxId)
            .Select(shard => shard.MessageId)
            .Distinct()
            .OrderByDescending(messageId => messageId)
            .Take(numberToFetch)
            .ToArrayAsync(cancellationToken);

        MessageShardDto[] shards = await appContext.MessageShards
            .Where(shard => shard.RoomHash == roomHash && messageIds.Contains(shard.MessageId))
            .OrderByDescending(shard => shard.MessageId)
            .ThenBy(shard => shard.ShardIndex)
            .Take(numberToFetch * totalShardCount)
            .Select(shard => new MessageShardDto
            {
                RoomHash = shard.RoomHash,
                MessageId = shard.MessageId,
                DataShardCount = shard.DataShardCount,
                ParityShardCount = shard.ParityShardCount,
                PaddingSize = shard.PaddingSize,
                ShardIndex = shard.ShardIndex,
                ShardData = shard.ShardData,
            })
            .ToArrayAsync(cancellationToken);

        return new GetMessageShardsResponse
        {
            MessageShards = shards,
        };
    }

    private async Task<MessageShardDto[]> FetchRemoteShardsAsync(string roomHash, long maxId, int numberToFetch, CancellationToken cancellationToken)
    {
        string? publicUrl = TryNormalizeUrl(_peerSyncOptions.PublicUrl);
        string[] peerUrls = await appContext.KnownServers
            .Where(peer => peer.IsActive && peer.Url != publicUrl)
            .OrderBy(peer => peer.Url)
            .Select(peer => peer.Url)
            .ToArrayAsync(cancellationToken);

        List<MessageShardDto> shards = [];

        foreach (string peerUrl in peerUrls)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                HttpClient client = httpClientFactory.CreateClient(nameof(MessageErasureCodingService));
                string requestUri =
                    $"{peerUrl}{MessageShardsEndpointPath}?roomHash={Uri.EscapeDataString(roomHash)}&maxId={maxId}&numberToFetch={numberToFetch}";

                GetMessageShardsResponse? response = await client.GetFromJsonAsync<GetMessageShardsResponse>(requestUri, cancellationToken);
                if (response?.MessageShards is not null)
                {
                    shards.AddRange(response.MessageShards);
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                logger.LogWarning(ex, "Failed to fetch message shards from peer '{PeerUrl}'.", peerUrl);
            }
        }

        return shards.ToArray();
    }

    private async Task<MessageShardDto[]> GetLocalStoredMessageShardsAsync(
        string roomHash,
        long maxId,
        int numberToFetch,
        CancellationToken cancellationToken)
    {
        return (await GetStoredMessageShardsAsync(roomHash, maxId, numberToFetch, cancellationToken)).MessageShards;
    }

    private async Task PushShardsToPeerAsync(string peerUrl, IReadOnlyCollection<MessageShardDto> shards, CancellationToken cancellationToken)
    {
        try
        {
            HttpClient client = httpClientFactory.CreateClient(nameof(MessageErasureCodingService));
            using HttpResponseMessage response = await client.PostAsJsonAsync(
                $"{peerUrl}{MessageShardsEndpointPath}",
                new StoreMessageShardsRequest
                {
                    MessageShards = shards.ToArray(),
                },
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Failed to store message shards on peer '{PeerUrl}'. Status code: {StatusCode}.", peerUrl, response.StatusCode);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Failed to store message shards on peer '{PeerUrl}'.", peerUrl);
        }
    }

    private async Task<List<KnownServerEntity>> GetShardPlacementPeersAsync(CancellationToken cancellationToken)
    {
        string? publicUrl = TryNormalizeUrl(_peerSyncOptions.PublicUrl);

        return await appContext.KnownServers
            .Where(peer => peer.IsActive && peer.Url != publicUrl)
            .OrderBy(peer => peer.FirstSeenAtUtc)
            .ToListAsync(cancellationToken);
    }

    private List<KnownServerEntity> OrderPeersForShardPlacement(IReadOnlyList<KnownServerEntity> peers)
    {
        List<KnownServerEntity> remainingPeers = peers
            .OrderBy(peer => peer.FirstSeenAtUtc)
            .ToList();

        if (remainingPeers.Count <= 1)
        {
            return remainingPeers;
        }

        List<KnownServerEntity> selectedPeers = [remainingPeers[0]];
        remainingPeers.RemoveAt(0);

        while (remainingPeers.Count > 0)
        {
            KnownServerEntity nextPeer = remainingPeers
                .OrderByDescending(peer => selectedPeers.Min(selected =>
                    Math.Abs((peer.FirstSeenAtUtc - selected.FirstSeenAtUtc).Ticks)))
                .ThenBy(peer => peer.FirstSeenAtUtc)
                .First();

            selectedPeers.Add(nextPeer);
            remainingPeers.Remove(nextPeer);
        }

        return selectedPeers;
    }

    private EncryptedMessageDto? TryReconstructMessage(IEnumerable<MessageShardDto> shards)
    {
        MessageShardDto[] shardArray = shards.ToArray();
        if (shardArray.Length == 0)
        {
            return null;
        }

        MessageShardDto metadata = shardArray[0];
        int totalShardCount = metadata.DataShardCount + metadata.ParityShardCount;
        if (shardArray
            .Select(shard => shard.ShardIndex)
            .Distinct()
            .Count() < metadata.DataShardCount)
        {
            return null;
        }

        byte[]?[] shardBuffers = new byte[totalShardCount][];
        foreach (MessageShardDto shard in shardArray
            .GroupBy(shard => shard.ShardIndex)
            .Select(group => group.First()))
        {
            if (shard.ShardIndex >= 0 && shard.ShardIndex < totalShardCount)
            {
                shardBuffers[shard.ShardIndex] = shard.ShardData;
            }
        }

        ReedSolomon reedSolomon = new(metadata.DataShardCount, metadata.ParityShardCount);
        byte[] decodedBytes = reedSolomon.ManagedDecode(shardBuffers, paddingSize: metadata.PaddingSize);
        return JsonSerializer.Deserialize<EncryptedMessageDto>(decodedBytes);
    }

    private void ValidateErasureCodingOptions()
    {
        if (_erasureCodingOptions.DataShards <= 0 || _erasureCodingOptions.ParityShards <= 0)
        {
            throw new InvalidOperationException("ErasureCoding options require positive DataShards and ParityShards values.");
        }
    }
    private static string? TryNormalizeUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
        {
            return null;
        }

        return uri.AbsoluteUri.TrimEnd('/');
    }
}
