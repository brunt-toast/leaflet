using Api.Dto;

namespace Api.Requests;

public sealed class StoreMessageShardsRequest
{
    public required MessageShardDto[] MessageShards { get; init; } = [];
}
