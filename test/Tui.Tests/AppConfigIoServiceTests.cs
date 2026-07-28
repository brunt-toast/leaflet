using Newtonsoft.Json.Linq;
using Tui.Services;

namespace Tui.Tests;

[TestClass]
public sealed class AppConfigIoServiceTests
{
    private const string ConfigPassword = "correct horse battery staple";
    private const string DuressPassword = "open sesame";
    private const string ConfigContent = """
        [identities]

        [servers.main]
        url = "http://localhost:5011"

        [rooms]
        """;

    [TestMethod]
    public async Task ReadAsync_WithConfigPassword_ReturnsConfig()
    {
        string configPath = CreateConfigPath();
        try
        {
            AppConfigIoService service = new(configPath, null!);
            await service.WriteAsync(ConfigContent, ConfigPassword, DuressPassword);

            string content = await service.ReadAsync(ConfigPassword);

            Assert.AreEqual(ConfigContent, content);
            Assert.IsTrue(File.Exists(configPath));
        }
        finally
        {
            DeleteConfigDirectory(configPath);
        }
    }

    [TestMethod]
    public async Task ReadAsync_WithDuressPassword_DeletesConfig()
    {
        string configPath = CreateConfigPath();
        try
        {
            AppConfigIoService service = new(configPath, null!);
            await service.WriteAsync(ConfigContent, ConfigPassword, DuressPassword);

            try
            {
                await service.ReadAsync(DuressPassword);
                Assert.Fail("Expected the duress password to delete the config and stop loading.");
            }
            catch (InvalidOperationException ex)
            {
                StringAssert.Contains(ex.Message, "Duress password accepted");
            }

            Assert.IsFalse(File.Exists(configPath));
        }
        finally
        {
            DeleteConfigDirectory(configPath);
        }
    }

    [TestMethod]
    public async Task WriteAsync_WithoutDuressPassword_PreservesDuressPasswordChecksum()
    {
        string configPath = CreateConfigPath();
        try
        {
            AppConfigIoService service = new(configPath, null!);
            await service.WriteAsync(ConfigContent, ConfigPassword, DuressPassword);
            string originalChecksum = ReadDuressPasswordChecksum(configPath);

            await service.WriteAsync($"{ConfigContent}{Environment.NewLine}", ConfigPassword);

            Assert.AreEqual(originalChecksum, ReadDuressPasswordChecksum(configPath));
            try
            {
                await service.ReadAsync(DuressPassword);
                Assert.Fail("Expected the preserved duress password to delete the config and stop loading.");
            }
            catch (InvalidOperationException ex)
            {
                StringAssert.Contains(ex.Message, "Duress password accepted");
            }

            Assert.IsFalse(File.Exists(configPath));
        }
        finally
        {
            DeleteConfigDirectory(configPath);
        }
    }

    [TestMethod]
    public async Task WriteAsync_WithoutDuressPassword_StoresRandomDuressPasswordChecksum()
    {
        string firstConfigPath = CreateConfigPath();
        string secondConfigPath = CreateConfigPath();
        try
        {
            AppConfigIoService firstService = new(firstConfigPath, null!);
            AppConfigIoService secondService = new(secondConfigPath, null!);

            await firstService.WriteAsync(ConfigContent, ConfigPassword);
            await secondService.WriteAsync(ConfigContent, ConfigPassword);

            string firstChecksum = ReadDuressPasswordChecksum(firstConfigPath);
            string secondChecksum = ReadDuressPasswordChecksum(secondConfigPath);

            Assert.AreNotEqual(firstChecksum, secondChecksum);
        }
        finally
        {
            DeleteConfigDirectory(firstConfigPath);
            DeleteConfigDirectory(secondConfigPath);
        }
    }

    [TestMethod]
    public async Task AddRoomAsync_WithExistingIdentity_AppendsRoom()
    {
        string configPath = CreateConfigPath();
        try
        {
            await File.WriteAllTextAsync(configPath, """
                [identities.IdentityA]
                name = "IdentityA"
                public_key = "public"
                private_key = "private"

                [servers.main]
                url = "http://localhost:5011"

                [rooms]
                """);

            AppConfigIoService service = new(configPath, null!);

            await service.AddRoomAsync(
                new NewRoomConfig
                {
                    PathSegments = ["Group1", "Room1"],
                    Key = "quoted \"room\" key",
                    IdentityName = "IdentityA"
                },
                identity: null);

            string updatedContent = await File.ReadAllTextAsync(configPath);
            StringAssert.Contains(updatedContent, "[rooms.Group1.Room1]");
            StringAssert.Contains(updatedContent, "key = \"quoted \\\"room\\\" key\"");
            StringAssert.Contains(updatedContent, "identity = \"IdentityA\"");
        }
        finally
        {
            DeleteConfigDirectory(configPath);
        }
    }

    [TestMethod]
    public async Task AddRoomAsync_WithGeneratedIdentity_AppendsIdentityAndRoom()
    {
        string configPath = CreateConfigPath();
        try
        {
            await File.WriteAllTextAsync(configPath, """
                [identities.IdentityA]
                name = "IdentityA"
                public_key = "public"
                private_key = "private"

                [servers.main]
                url = "http://localhost:5011"

                [rooms]
                """);

            GeneratedIdentity generatedIdentity = new()
            {
                Name = "IdentityB",
                PublicKey = "generated-public",
                PrivateKey = "generated-private"
            };
            AppConfigIoService service = new(configPath, null!);

            await service.AddRoomAsync(
                new NewRoomConfig
                {
                    PathSegments = ["Group1", "Room2"],
                    Key = "room key",
                    IdentityName = "IdentityB"
                },
                generatedIdentity);

            string updatedContent = await File.ReadAllTextAsync(configPath);
            StringAssert.Contains(updatedContent, "[identities.IdentityB]");
            StringAssert.Contains(updatedContent, "public_key = '''generated-public'''");
            StringAssert.Contains(updatedContent, "[rooms.Group1.Room2]");
            StringAssert.Contains(updatedContent, "identity = \"IdentityB\"");
        }
        finally
        {
            DeleteConfigDirectory(configPath);
        }
    }

    private static string CreateConfigPath()
    {
        string directoryPath = Path.Combine(GetSolutionRoot(), ".codex", "temp", "Tui.Tests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(directoryPath);
        return Path.Combine(directoryPath, "config.toml");
    }

    private static void DeleteConfigDirectory(string configPath)
    {
        string? directoryPath = Path.GetDirectoryName(configPath);
        if (directoryPath is not null && Directory.Exists(directoryPath))
        {
            Directory.Delete(directoryPath, recursive: true);
        }
    }

    private static string ReadDuressPasswordChecksum(string configPath)
    {
        string persistedContent = File.ReadAllText(configPath);
        string envelopeJson = persistedContent["MESSAGING2-CONFIG-ENC-V1".Length..].Trim();
        string? checksum = JObject.Parse(envelopeJson)["duress_password_checksum"]?.Value<string>();

        Assert.IsFalse(string.IsNullOrWhiteSpace(checksum));
        return checksum;
    }

    private static string GetSolutionRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Messaging-2.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate the solution root.");
    }
}
