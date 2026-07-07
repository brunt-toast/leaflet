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

internal sealed class MessageErasureCodingService : IMessageErasureCodingService
{
    private const string MessageShardsEndpointPath = "/api/internal/message-shards";
    private readonly AppDbContext _appContext;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IMessageIdentityService _messageIdentityService;
    private readonly PeerSyncOptions _peerSyncOptions;
    private readonly ErasureCodingOptions _erasureCodingOptions;
    private readonly ILogger<MessageErasureCodingService> _logger;

    public MessageErasureCodingService(
        AppDbContext appContext,
        IHttpClientFactory httpClientFactory,
        IMessageIdentityService messageIdentityService,
        IOptions<PeerSyncOptions> peerSyncOptions,
        IOptions<ErasureCodingOptions> erasureCodingOptions,
        ILogger<MessageErasureCodingService> logger)
    {
        _appContext = appContext;
        _httpClientFactory = httpClientFactory;
        _messageIdentityService = messageIdentityService;
        _peerSyncOptions = peerSyncOptions.Value;
        _erasureCodingOptions = erasureCodingOptions.Value;
        _logger = logger;
    }

    public async Task EnsureMessagesAvailableAsync(string roomHash, long maxId, int numberToFetch, CancellationToken cancellationToken)
    {
        int localCount = await _appContext.EncryptedMessages
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

        HashSet<long> existingMessageIds = await _appContext.EncryptedMessages
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

        await _messageIdentityService.ObserveMessageIdsAsync(
            recoveredMessages.Select(message => (message.RoomHash, message.Id)),
            cancellationToken);
        await _appContext.EncryptedMessages.AddRangeAsync(recoveredMessages, cancellationToken);
        await _appContext.SaveChangesAsync(cancellationToken);
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
            _logger.LogWarning("Skipping erasure coding distribution because no active peers are available.");
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

    public async Task RepairShardIntegrityAsync(CancellationToken cancellationToken)
    {
        ValidateErasureCodingOptions();

        int batchSize = Math.Max(1, _erasureCodingOptions.RepairBatchSize);
        MessageCandidate[] candidates = await GetRepairCandidatesAsync(batchSize, cancellationToken);
        if (candidates.Length == 0)
        {
            return;
        }

        List<EncryptedMessageDto> messagesNeedingRepair = [];

        foreach (MessageCandidate candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            EncryptedMessageDto? message = await GetLocalMessageAsync(candidate.RoomHash, candidate.MessageId, cancellationToken);
            MessageShardDto[] localShards = await GetLocalStoredExactMessageShardsAsync(candidate.RoomHash, candidate.MessageId, cancellationToken);
            MessageShardDto[] remoteShards = await FetchRemoteExactMessageShardsAsync(candidate.RoomHash, candidate.MessageId, cancellationToken);
            MessageShardDto[] availableShards = localShards
                .Concat(remoteShards)
                .ToArray();

            if (message is null)
            {
                message = TryReconstructMessage(availableShards);
                if (message is not null)
                {
                    await StoreRecoveredMessageAsync(message, cancellationToken);
                }
            }

            if (message is null)
            {
                continue;
            }

            int desiredShardCount = availableShards.Length == 0
                ? _erasureCodingOptions.DataShards + _erasureCodingOptions.ParityShards
                : availableShards[0].DataShardCount + availableShards[0].ParityShardCount;

            int availableShardCount = availableShards
                .Select(shard => shard.ShardIndex)
                .Distinct()
                .Count();

            if (availableShardCount < desiredShardCount)
            {
                messagesNeedingRepair.Add(message);
            }
        }

        if (messagesNeedingRepair.Count == 0)
        {
            return;
        }

        _logger.LogInformation(
            "Repairing shard redundancy for {MessageCount} messages.",
            messagesNeedingRepair.Count);

        await DistributeMessageShardsAsync(messagesNeedingRepair, cancellationToken);
    }

    public async Task StoreMessageShardsAsync(StoreMessageShardsRequest request, CancellationToken cancellationToken)
    {
        if (request.MessageShards.Length == 0)
        {
            return;
        }

        await _messageIdentityService.ObserveMessageIdsAsync(
            request.MessageShards.Select(shard => (shard.RoomHash, shard.MessageId)),
            cancellationToken);

        string roomHash = request.MessageShards[0].RoomHash;
        long[] messageIds = request.MessageShards
            .Select(shard => shard.MessageId)
            .Distinct()
            .ToArray();

        MessageShardEntity[] existingShards = await _appContext.MessageShards
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

                await _appContext.MessageShards.AddAsync(entity, cancellationToken);
                continue;
            }

            entity.DataShardCount = shard.DataShardCount;
            entity.ParityShardCount = shard.ParityShardCount;
            entity.PaddingSize = shard.PaddingSize;
            entity.ShardData = shard.ShardData;
            entity.StoredAtUtc = DateTime.UtcNow;
        }

        await _appContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<GetMessageShardsResponse> GetStoredMessageShardsAsync(
        string roomHash,
        long maxId,
        int numberToFetch,
        CancellationToken cancellationToken)
    {
        int totalShardCount = _erasureCodingOptions.DataShards + _erasureCodingOptions.ParityShards;

        long[] messageIds = await _appContext.MessageShards
            .Where(shard => shard.RoomHash == roomHash && shard.MessageId <= maxId)
            .Select(shard => shard.MessageId)
            .Distinct()
            .OrderByDescending(messageId => messageId)
            .Take(numberToFetch)
            .ToArrayAsync(cancellationToken);

        MessageShardDto[] shards = await _appContext.MessageShards
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
        string[] peerUrls = await _appContext.KnownServers
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
                HttpClient client = _httpClientFactory.CreateClient(nameof(MessageErasureCodingService));
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
                _logger.LogWarning(ex, "Failed to fetch message shards from peer '{PeerUrl}'.", peerUrl);
            }
        }

        return shards.ToArray();
    }

    private async Task<MessageShardDto[]> FetchRemoteExactMessageShardsAsync(
        string roomHash,
        long messageId,
        CancellationToken cancellationToken)
    {
        MessageShardDto[] shards = await FetchRemoteShardsAsync(roomHash, messageId, numberToFetch: 1, cancellationToken);
        return shards
            .Where(shard => shard.MessageId == messageId)
            .ToArray();
    }

    private async Task<MessageShardDto[]> GetLocalStoredMessageShardsAsync(
        string roomHash,
        long maxId,
        int numberToFetch,
        CancellationToken cancellationToken)
    {
        return (await GetStoredMessageShardsAsync(roomHash, maxId, numberToFetch, cancellationToken)).MessageShards;
    }

    private async Task<MessageShardDto[]> GetLocalStoredExactMessageShardsAsync(
        string roomHash,
        long messageId,
        CancellationToken cancellationToken)
    {
        return await _appContext.MessageShards
            .Where(shard => shard.RoomHash == roomHash && shard.MessageId == messageId)
            .OrderBy(shard => shard.ShardIndex)
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
    }

    private async Task<EncryptedMessageDto?> GetLocalMessageAsync(string roomHash, long messageId, CancellationToken cancellationToken)
    {
        return await _appContext.EncryptedMessages
            .Where(message => message.RoomHash == roomHash && message.Id == messageId)
            .Select(message => new EncryptedMessageDto
            {
                Id = message.Id,
                RoomHash = message.RoomHash,
                SenderPublicKey = message.SenderPublicKey,
                Nonce = message.Nonce,
                CypherText = message.CypherText,
                Signature = message.Signature,
            })
            .SingleOrDefaultAsync(cancellationToken);
    }

    private async Task PushShardsToPeerAsync(string peerUrl, IReadOnlyCollection<MessageShardDto> shards, CancellationToken cancellationToken)
    {
        try
        {
            HttpClient client = _httpClientFactory.CreateClient(nameof(MessageErasureCodingService));
            using HttpResponseMessage response = await client.PostAsJsonAsync(
                $"{peerUrl}{MessageShardsEndpointPath}",
                new StoreMessageShardsRequest
                {
                    MessageShards = shards.ToArray(),
                },
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Failed to store message shards on peer '{PeerUrl}'. Status code: {StatusCode}.", peerUrl, response.StatusCode);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "Failed to store message shards on peer '{PeerUrl}'.", peerUrl);
        }
    }

    private async Task<MessageCandidate[]> GetRepairCandidatesAsync(int batchSize, CancellationToken cancellationToken)
    {
        MessageCandidate[] messageCandidates = await _appContext.EncryptedMessages
            .AsNoTracking()
            .OrderByDescending(message => message.Id)
            .Take(batchSize)
            .Select(message => new MessageCandidate(message.RoomHash, message.Id))
            .ToArrayAsync(cancellationToken);

        MessageCandidate[] shardCandidates = await _appContext.MessageShards
            .AsNoTracking()
            .GroupBy(shard => new { shard.RoomHash, shard.MessageId })
            .Select(group => new
            {
                group.Key.RoomHash,
                group.Key.MessageId,
                LastStoredAtUtc = group.Max(shard => shard.StoredAtUtc),
            })
            .OrderByDescending(candidate => candidate.LastStoredAtUtc)
            .Take(batchSize)
            .Select(candidate => new MessageCandidate(candidate.RoomHash, candidate.MessageId))
            .ToArrayAsync(cancellationToken);

        return messageCandidates
            .Concat(shardCandidates)
            .Distinct()
            .Take(batchSize)
            .ToArray();
    }

    private async Task<List<KnownServerEntity>> GetShardPlacementPeersAsync(CancellationToken cancellationToken)
    {
        string? publicUrl = TryNormalizeUrl(_peerSyncOptions.PublicUrl);

        return await _appContext.KnownServers
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

    private async Task StoreRecoveredMessageAsync(EncryptedMessageDto message, CancellationToken cancellationToken)
    {
        bool exists = await _appContext.EncryptedMessages
            .AnyAsync(existing => existing.RoomHash == message.RoomHash && existing.Id == message.Id, cancellationToken);
        if (exists)
        {
            return;
        }

        await _messageIdentityService.ObserveMessageIdAsync(message.RoomHash, message.Id, cancellationToken);
        await _appContext.EncryptedMessages.AddAsync(new EncryptedMessageEntity
        {
            Id = message.Id,
            RoomHash = message.RoomHash,
            SenderPublicKey = message.SenderPublicKey,
            Nonce = message.Nonce,
            CypherText = message.CypherText,
            Signature = message.Signature,
        }, cancellationToken);
        await _appContext.SaveChangesAsync(cancellationToken);
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

internal sealed record MessageCandidate
{
    public MessageCandidate(string roomHash, long messageId)
    {
        RoomHash = roomHash;
        MessageId = messageId;
    }

    public string RoomHash { get; init; }

    public long MessageId { get; init; }
}
