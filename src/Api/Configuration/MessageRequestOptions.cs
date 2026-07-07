namespace Api.Configuration;

public sealed class MessageRequestOptions
{
    public const string SectionName = "MessageRequests";

    public int MaxMessagesPerRequest { get; init; }
    public int MaxMessageContentBytes { get; init; }
}
