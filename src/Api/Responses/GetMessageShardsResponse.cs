using Api.Dto;

namespace Api.Responses;

public sealed class GetMessageShardsResponse
{
    public required MessageShardDto[] MessageShards { get; init; } = [];
}
