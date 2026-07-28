using Microsoft.Extensions.Configuration;
using Tui.Configuration;

namespace Tui.Tests;

[TestClass]
public sealed class ConfigureTuiAppConfigOptionsTests
{
    [TestMethod]
    public void Configure_BindsMessageFilters()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["filters:message_content_regexes:0"] = "spoiler",
                ["filters:public_key_friendly_hashes:0"] = "quiet-amber-otter",
                ["identities:IdentityA:name"] = "TheLegend27",
                ["identities:IdentityA:public_key"] = "public",
                ["identities:IdentityA:private_key"] = "private",
                ["servers:main:url"] = "http://localhost:5011",
                ["rooms:Group1:Room1:key"] = "room-key",
                ["rooms:Group1:Room1:identity"] = "IdentityA"
            })
            .Build();
        ConfigureTuiAppConfigOptions configureOptions = new(configuration);
        TuiAppConfig appConfig = new();

        configureOptions.Configure(appConfig);

        CollectionAssert.AreEqual(new[] { "spoiler" }, appConfig.Filters.MessageContentRegexes.ToArray());
        CollectionAssert.AreEqual(new[] { "quiet-amber-otter" }, appConfig.Filters.PublicKeyFriendlyHashes.ToArray());
    }

    [TestMethod]
    public void Configure_ThrowsForInvalidMessageFilterRegex()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["filters:message_content_regexes:0"] = "[",
                ["identities:IdentityA:name"] = "TheLegend27",
                ["identities:IdentityA:public_key"] = "public",
                ["identities:IdentityA:private_key"] = "private",
                ["servers:main:url"] = "http://localhost:5011",
                ["rooms:Group1:Room1:key"] = "room-key",
                ["rooms:Group1:Room1:identity"] = "IdentityA"
            })
            .Build();
        ConfigureTuiAppConfigOptions configureOptions = new(configuration);

        try
        {
            configureOptions.Configure(new TuiAppConfig());
            Assert.Fail("Expected Configure to reject an invalid regex.");
        }
        catch (InvalidOperationException ex)
        {
            StringAssert.Contains(ex.Message, "Invalid regex");
        }
    }
}
