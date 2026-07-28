using Tui.Configuration;
using Tui.Services;

namespace Tui.Tests;

[TestClass]
public sealed class MessageVisibilityFilterTests
{
    [TestMethod]
    public void Apply_HidesMessageWhenBodyMatchesRegex()
    {
        MessageVisibilityFilter filter = new(new TuiMessageFilterConfig
        {
            MessageContentRegexes = ["spoiler"]
        });

        RenderedMessage result = filter.Apply(CreateMessage(body: "contains spoiler text", senderKeyHash: "quiet-amber-otter"));

        Assert.IsTrue(result.IsHidden);
        Assert.AreEqual("[Message from quiet-amber-otter hidden - reason: filtered phrase]", result.Body);
    }

    [TestMethod]
    public void Apply_HidesMessageWhenSenderFriendlyHashMatches()
    {
        MessageVisibilityFilter filter = new(new TuiMessageFilterConfig
        {
            PublicKeyFriendlyHashes = ["quiet-amber-otter"]
        });

        RenderedMessage result = filter.Apply(CreateMessage(body: "hello", senderKeyHash: "quiet-amber-otter"));

        Assert.IsTrue(result.IsHidden);
        Assert.AreEqual("[Message from quiet-amber-otter hidden - reason: filtered user]", result.Body);
    }

    [TestMethod]
    public void Apply_LeavesNonMatchingMessageVisible()
    {
        MessageVisibilityFilter filter = new(new TuiMessageFilterConfig
        {
            MessageContentRegexes = ["spoiler"],
            PublicKeyFriendlyHashes = ["quiet-amber-otter"]
        });

        RenderedMessage message = CreateMessage(body: "hello", senderKeyHash: "brisk-cinder-fox");
        RenderedMessage result = filter.Apply(message);

        Assert.IsFalse(result.IsHidden);
        Assert.AreSame(message, result);
    }

    private static RenderedMessage CreateMessage(string body, string senderKeyHash)
    {
        return new RenderedMessage
        {
            SentAtUtc = new DateTimeOffset(2026, 7, 7, 12, 0, 0, TimeSpan.Zero),
            Sender = "TheLegend27",
            SenderKeyHash = senderKeyHash,
            Body = body,
            IsVerified = true,
            IsError = false,
            IsPending = false,
            DeliveryFailed = false
        };
    }
}
