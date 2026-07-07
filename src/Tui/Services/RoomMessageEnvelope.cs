using Newtonsoft.Json;

namespace Tui.Services;

internal sealed class RoomMessageEnvelope
{
    [JsonProperty("content")]
    public required RoomMessageContent Content { get; init; }

    [JsonProperty("metadata")]
    public required RoomMessageMetadata Metadata { get; init; }
}

internal sealed class RoomMessageContent
{
    [JsonProperty("text")]
    public required string Text { get; init; }
}

internal sealed class RoomMessageMetadata
{
    [JsonProperty("sender_name")]
    public required string SenderName { get; init; }

    [JsonProperty("sent_at_utc")]
    public required DateTimeOffset SentAtUtc { get; init; }
}

internal sealed class LegacyRoomMessagePayload
{
    [JsonProperty("sender_name")]
    public required string SenderName { get; init; }

    [JsonProperty("text")]
    public required string Text { get; init; }

    [JsonProperty("sent_at_utc")]
    public required DateTimeOffset SentAtUtc { get; init; }
}
