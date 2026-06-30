namespace Core.Requests.Messages;

public sealed class GetMessagesRequest
{
    public required string RoomHash { get; init; }
    public required long MaxId { get; init; }
    public required int NumberToFetch { get; init; }
}
