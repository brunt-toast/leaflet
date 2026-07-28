using Core.Dto;

namespace Core.Responses.Servers;

public sealed class GetKnownServersResponse
{
    public required KnownServerDto[] Servers { get; init; } = [];
}
