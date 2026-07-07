using System.Text.RegularExpressions;
using Tui.Configuration;

namespace Tui.Services;

internal sealed class MessageVisibilityFilter
{
    private readonly IReadOnlyList<Regex> _messageContentRegexes;
    private readonly HashSet<string> _publicKeyFriendlyHashes;

    public MessageVisibilityFilter(TuiMessageFilterConfig config)
    {
        _messageContentRegexes = config.MessageContentRegexes
            .Where(static pattern => !string.IsNullOrWhiteSpace(pattern))
            .Select(static pattern => new Regex(pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant))
            .ToArray();
        _publicKeyFriendlyHashes = new HashSet<string>(
            config.PublicKeyFriendlyHashes.Where(static hash => !string.IsNullOrWhiteSpace(hash)),
            StringComparer.OrdinalIgnoreCase);
    }

    public RenderedMessage Apply(RenderedMessage message)
    {
        string? reason = TryGetHideReason(message);
        if (reason is null)
        {
            return message;
        }

        string friendlyHash = string.IsNullOrWhiteSpace(message.SenderKeyHash)
            ? "unknown"
            : message.SenderKeyHash;

        return new RenderedMessage
        {
            LocalId = message.LocalId,
            SentAtUtc = message.SentAtUtc,
            Sender = message.Sender,
            SenderKeyHash = message.SenderKeyHash,
            Body = $"[Message from {friendlyHash} hidden - reason: filtered {reason}]",
            IsVerified = message.IsVerified,
            IsError = message.IsError,
            IsPending = message.IsPending,
            DeliveryFailed = message.DeliveryFailed,
            IsHidden = true
        };
    }

    private string? TryGetHideReason(RenderedMessage message)
    {
        if (_messageContentRegexes.Any(regex => regex.IsMatch(message.Body)))
        {
            return "phrase";
        }

        if (!string.IsNullOrWhiteSpace(message.SenderKeyHash) && _publicKeyFriendlyHashes.Contains(message.SenderKeyHash))
        {
            return "user";
        }

        return null;
    }
}
