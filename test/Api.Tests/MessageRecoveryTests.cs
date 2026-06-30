using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using Api.Responses;
using Core.Dto;
using Core.Requests.Messages;
using Core.Responses.Messages;

namespace Api.Tests;

[TestClass]
[DoNotParallelize]
public sealed class MessageRecoveryTests
{
    [TestMethod]
    public async Task CreateMessages_AssignsUniqueIdsAcrossInstances_ForSameRoom()
    {
        string solutionRoot = GetSolutionRoot();
        string apiDllPath = Path.Combine(solutionRoot, "src", "Api", "bin", "Debug", "net10.0", "Api.dll");
        Assert.IsTrue(File.Exists(apiDllPath), $"Expected API assembly at '{apiDllPath}'.");

        string testRoot = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"messaging-2-tests-{Guid.NewGuid():N}")).FullName;
        List<ApiNode> nodes = [];

        try
        {
            nodes.AddRange(await StartNodesAsync(apiDllPath, testRoot, count: 3));

            await ConnectNodesAsync(nodes[0], nodes[1]);
            await ConnectNodesAsync(nodes[0], nodes[2]);
            await ConnectNodesAsync(nodes[1], nodes[2]);

            await CreateMessageAsync(nodes[0], "room-unique", "cipher-text-a");
            await CreateMessageAsync(nodes[1], "room-unique", "cipher-text-b");

            GetMessagesResponse messages = await GetMessagesAsync(nodes[2], "room-unique", numberToFetch: 2);

            Assert.AreEqual(2, messages.Messages.Length, await nodes[2].FormatFailureAsync("Expected both messages to be retrievable."));
            Assert.AreNotEqual(messages.Messages[0].Id, messages.Messages[1].Id, await nodes[2].FormatFailureAsync("Expected logical-clock message IDs to be unique."));
            Assert.IsTrue(messages.Messages[0].Id < messages.Messages[1].Id, await nodes[2].FormatFailureAsync("Expected message IDs to be sorted ascending."));

            string[] cipherTexts = messages.Messages.Select(message => message.CypherText).OrderBy(value => value).ToArray();
            string[] expectedCipherTexts = ["cipher-text-a", "cipher-text-b"];
            CollectionAssert.AreEqual(expectedCipherTexts, cipherTexts);
        }
        finally
        {
            foreach (ApiNode node in nodes)
            {
                await node.DisposeAsync();
            }

            try
            {
                Directory.Delete(testRoot, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    [TestMethod]
    public async Task CreateMessages_AdvancesRoomLogicalClock_AfterObservingRemoteMessage()
    {
        string solutionRoot = GetSolutionRoot();
        string apiDllPath = Path.Combine(solutionRoot, "src", "Api", "bin", "Debug", "net10.0", "Api.dll");
        Assert.IsTrue(File.Exists(apiDllPath), $"Expected API assembly at '{apiDllPath}'.");

        string testRoot = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"messaging-2-tests-{Guid.NewGuid():N}")).FullName;
        List<ApiNode> nodes = [];

        try
        {
            nodes.AddRange(await StartNodesAsync(apiDllPath, testRoot, count: 2));

            await ConnectNodesAsync(nodes[0], nodes[1]);

            await CreateMessageAsync(nodes[0], "room-clock", "cipher-text-a");
            GetMessagesResponse observed = await GetMessagesAsync(nodes[1], "room-clock", numberToFetch: 1);
            Assert.AreEqual(1, observed.Messages.Length, await nodes[1].FormatFailureAsync("Expected node 2 to observe the first message."));

            long firstMessageId = observed.Messages[0].Id;

            await CreateMessageAsync(nodes[1], "room-clock", "cipher-text-b");
            GetMessagesResponse messages = await GetMessagesAsync(nodes[1], "room-clock", numberToFetch: 2);

            Assert.AreEqual(2, messages.Messages.Length, await nodes[1].FormatFailureAsync("Expected two messages after the second write."));
            Assert.AreEqual(firstMessageId, messages.Messages[0].Id, await nodes[1].FormatFailureAsync("Expected the observed message to remain first."));
            Assert.IsTrue(messages.Messages[1].Id > firstMessageId, await nodes[1].FormatFailureAsync("Expected the new local message ID to advance past the observed remote message."));
            string[] expectedCipherTexts = ["cipher-text-a", "cipher-text-b"];
            CollectionAssert.AreEqual(expectedCipherTexts, messages.Messages.Select(message => message.CypherText).ToArray());
        }
        finally
        {
            foreach (ApiNode node in nodes)
            {
                await node.DisposeAsync();
            }

            try
            {
                Directory.Delete(testRoot, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    [TestMethod]
    public async Task GetMessage_ReconstructsMessageFromRemainingPeers_WhenOriginInstanceIsOffline()
    {
        string solutionRoot = GetSolutionRoot();
        string apiDllPath = Path.Combine(solutionRoot, "src", "Api", "bin", "Debug", "net10.0", "Api.dll");
        Assert.IsTrue(File.Exists(apiDllPath), $"Expected API assembly at '{apiDllPath}'.");

        string testRoot = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"messaging-2-tests-{Guid.NewGuid():N}")).FullName;
        List<ApiNode> nodes = [];

        try
        {
            nodes.AddRange(await StartNodesAsync(apiDllPath, testRoot, count: 4));

            await ConnectNodesAsync(nodes[0], nodes[1]);
            await ConnectNodesAsync(nodes[0], nodes[2]);
            await ConnectNodesAsync(nodes[0], nodes[3]);
            await ConnectNodesAsync(nodes[1], nodes[2]);
            await ConnectNodesAsync(nodes[1], nodes[3]);
            await ConnectNodesAsync(nodes[2], nodes[3]);

            CreateMessagesRequest createRequest = new()
            {
                Messages =
                [
                    new EncryptedMessageDto
                    {
                        Id = 0,
                        RoomHash = "room-a",
                        SenderPublicKey = "sender-public-key",
                        Nonce = "nonce",
                        CypherText = "cipher-text",
                        Signature = "signature",
                    },
                ],
            };

            using HttpResponseMessage postResponse = await nodes[0].Client.PostAsJsonAsync("/api/messages", createRequest);
            Assert.AreEqual(HttpStatusCode.OK, postResponse.StatusCode, await nodes[0].FormatFailureAsync("POST /api/messages failed."));

            await nodes[0].StopAsync();

            GetMessagesResponse? getResponse = await nodes[1].Client.GetFromJsonAsync<GetMessagesResponse>(
                "/api/messages?roomHash=room-a&maxId=9223372036854775807&numberToFetch=1");

            Assert.IsNotNull(getResponse, await nodes[1].FormatFailureAsync("GET /api/messages returned no body."));
            Assert.AreEqual(1, getResponse.Messages.Length, await nodes[1].FormatFailureAsync("Expected exactly one reconstructed message."));

            EncryptedMessageDto message = getResponse.Messages[0];
            Assert.AreEqual("room-a", message.RoomHash);
            Assert.AreEqual("sender-public-key", message.SenderPublicKey);
            Assert.AreEqual("nonce", message.Nonce);
            Assert.AreEqual("cipher-text", message.CypherText);
            Assert.AreEqual("signature", message.Signature);
        }
        finally
        {
            foreach (ApiNode node in nodes)
            {
                await node.DisposeAsync();
            }

            try
            {
                Directory.Delete(testRoot, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    [TestMethod]
    public async Task GetMessage_ReturnsNoMessages_WhenNotEnoughShardHoldersSurvive()
    {
        string solutionRoot = GetSolutionRoot();
        string apiDllPath = Path.Combine(solutionRoot, "src", "Api", "bin", "Debug", "net10.0", "Api.dll");
        Assert.IsTrue(File.Exists(apiDllPath), $"Expected API assembly at '{apiDllPath}'.");

        string testRoot = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"messaging-2-tests-{Guid.NewGuid():N}")).FullName;
        List<ApiNode> nodes = [];

        try
        {
            nodes.AddRange(await StartNodesAsync(apiDllPath, testRoot, count: 6));

            for (int i = 0; i < nodes.Count; i++)
            {
                for (int j = i + 1; j < nodes.Count; j++)
                {
                    await ConnectNodesAsync(nodes[i], nodes[j]);
                }
            }

            CreateMessagesRequest createRequest = new()
            {
                Messages =
                [
                    new EncryptedMessageDto
                    {
                        Id = 0,
                        RoomHash = "room-b",
                        SenderPublicKey = "sender-public-key",
                        Nonce = "nonce",
                        CypherText = "cipher-text",
                        Signature = "signature",
                    },
                ],
            };

            using HttpResponseMessage postResponse = await nodes[0].Client.PostAsJsonAsync("/api/messages", createRequest);
            Assert.AreEqual(HttpStatusCode.OK, postResponse.StatusCode, await nodes[0].FormatFailureAsync("POST /api/messages failed."));

            await nodes[0].StopAsync();
            await nodes[2].StopAsync();
            await nodes[3].StopAsync();
            await nodes[4].StopAsync();

            GetMessagesResponse? getResponse = await nodes[1].Client.GetFromJsonAsync<GetMessagesResponse>(
                "/api/messages?roomHash=room-b&maxId=9223372036854775807&numberToFetch=1");

            Assert.IsNotNull(getResponse, await nodes[1].FormatFailureAsync("GET /api/messages returned no body."));
            Assert.AreEqual(0, getResponse.Messages.Length, await nodes[1].FormatFailureAsync(
                "Expected no messages because fewer than the required shards survived."));
        }
        finally
        {
            foreach (ApiNode node in nodes)
            {
                await node.DisposeAsync();
            }

            try
            {
                Directory.Delete(testRoot, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    [TestMethod]
    public async Task IntegrityRepair_ReSeedsMissingShards_BeforeAnotherPeerFails()
    {
        string solutionRoot = GetSolutionRoot();
        string apiDllPath = Path.Combine(solutionRoot, "src", "Api", "bin", "Debug", "net10.0", "Api.dll");
        Assert.IsTrue(File.Exists(apiDllPath), $"Expected API assembly at '{apiDllPath}'." );

        string testRoot = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"messaging-2-tests-{Guid.NewGuid():N}")).FullName;
        List<ApiNode> nodes = [];

        try
        {
            nodes.AddRange(await StartNodesAsync(apiDllPath, testRoot, count: 6, repairIntervalSeconds: 1));

            for (int i = 0; i < nodes.Count; i++)
            {
                for (int j = i + 1; j < nodes.Count; j++)
                {
                    await ConnectNodesAsync(nodes[i], nodes[j]);
                }
            }

            await CreateMessageAsync(nodes[0], "room-repair", "cipher-text-repair");

            await nodes[0].StopAsync();
            await nodes[4].StopAsync();
            await nodes[5].StopAsync();

            await WaitForAsync(
                async () =>
                {
                    GetMessageShardsResponse? shardResponse = await nodes[3].Client.GetFromJsonAsync<GetMessageShardsResponse>(
                        "/api/internal/message-shards?roomHash=room-repair&maxId=9223372036854775807&numberToFetch=1");
                    return shardResponse?.MessageShards
                        .Where(shard => shard.MessageId > 0)
                        .Select(shard => shard.ShardIndex)
                        .Distinct()
                        .Count() >= 2;
                },
                timeout: TimeSpan.FromSeconds(10),
                failureMessageFactory: () => nodes[1].FormatFailureAsync(
                    "Expected integrity repair to re-seed additional shards onto a surviving peer."));

            await nodes[2].StopAsync();

            GetMessagesResponse repairedResponse = await GetMessagesAsync(nodes[3], "room-repair", numberToFetch: 1);
            Assert.AreEqual(1, repairedResponse.Messages.Length, await nodes[3].FormatFailureAsync(
                "Expected message retrieval to succeed after integrity repair re-seeded shards."));
            Assert.AreEqual("cipher-text-repair", repairedResponse.Messages[0].CypherText);
        }
        finally
        {
            foreach (ApiNode node in nodes)
            {
                await node.DisposeAsync();
            }

            try
            {
                Directory.Delete(testRoot, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static async Task<List<ApiNode>> StartNodesAsync(
        string apiDllPath,
        string testRoot,
        int count,
        int repairIntervalSeconds = 0)
    {
        List<ApiNode> nodes = [];

        for (int i = 0; i < count; i++)
        {
            int port = GetFreeTcpPort();
            string publicUrl = $"http://127.0.0.1:{port}";
            string dbPath = Path.Combine(testRoot, $"node-{i + 1}.db");
            string nodeName = $"node-{i + 1}";

            ApiNode node = new(apiDllPath, nodeName, publicUrl, dbPath, testRoot, repairIntervalSeconds);
            await node.StartAsync();
            nodes.Add(node);
        }

        return nodes;
    }

    private static async Task ConnectNodesAsync(ApiNode requester, ApiNode receiver)
    {
        using HttpResponseMessage forwardResponse = await receiver.Client.GetAsync(
            $"/api/servers?requesterUrl={Uri.EscapeDataString(requester.PublicUrl)}");
        using HttpResponseMessage reverseResponse = await requester.Client.GetAsync(
            $"/api/servers?requesterUrl={Uri.EscapeDataString(receiver.PublicUrl)}");

        Assert.AreEqual(HttpStatusCode.OK, forwardResponse.StatusCode, await receiver.FormatFailureAsync(
            $"Failed to connect '{requester.Name}' to '{receiver.Name}'."));
        Assert.AreEqual(HttpStatusCode.OK, reverseResponse.StatusCode, await requester.FormatFailureAsync(
            $"Failed to connect '{receiver.Name}' to '{requester.Name}'."));
    }

    private static async Task CreateMessageAsync(ApiNode node, string roomHash, string cipherText)
    {
        CreateMessagesRequest createRequest = new()
        {
            Messages =
            [
                new EncryptedMessageDto
                {
                    Id = 0,
                    RoomHash = roomHash,
                    SenderPublicKey = "sender-public-key",
                    Nonce = "nonce",
                    CypherText = cipherText,
                    Signature = "signature",
                },
            ],
        };

        using HttpResponseMessage postResponse = await node.Client.PostAsJsonAsync("/api/messages", createRequest);
        Assert.AreEqual(HttpStatusCode.OK, postResponse.StatusCode, await node.FormatFailureAsync("POST /api/messages failed."));
    }

    private static async Task<GetMessagesResponse> GetMessagesAsync(ApiNode node, string roomHash, int numberToFetch)
    {
        GetMessagesResponse? response = await node.Client.GetFromJsonAsync<GetMessagesResponse>(
            $"/api/messages?roomHash={Uri.EscapeDataString(roomHash)}&maxId={long.MaxValue}&numberToFetch={numberToFetch}");

        Assert.IsNotNull(response, await node.FormatFailureAsync("GET /api/messages returned no body."));
        return response;
    }

    private static int GetFreeTcpPort()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();

        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
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

    private static async Task WaitForAsync(
        Func<Task<bool>> condition,
        TimeSpan timeout,
        Func<Task<string>> failureMessageFactory)
    {
        DateTime deadline = DateTime.UtcNow.Add(timeout);
        Exception? lastError = null;

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (await condition())
                {
                    return;
                }
            }
            catch (Exception ex)
            {
                lastError = ex;
            }

            await Task.Delay(250);
        }

        string failureMessage = await failureMessageFactory();
        if (lastError is not null)
        {
            failureMessage = $"{failureMessage}{Environment.NewLine}Last error: {lastError}";
        }

        Assert.Fail(failureMessage);
    }

    private sealed class ApiNode : IAsyncDisposable
    {
        private readonly string _apiDllPath;
        private readonly string _dbPath;
        private readonly string _workingDirectory;
        private readonly int _repairIntervalSeconds;
        private readonly List<string> _logLines = [];
        private Process? _process;
        private bool _stopped;

        public ApiNode(string apiDllPath, string name, string publicUrl, string dbPath, string workingDirectory, int repairIntervalSeconds)
        {
            _apiDllPath = apiDllPath;
            Name = name;
            PublicUrl = publicUrl;
            _dbPath = dbPath;
            _workingDirectory = workingDirectory;
            _repairIntervalSeconds = repairIntervalSeconds;
            Client = new HttpClient
            {
                BaseAddress = new Uri(publicUrl),
                Timeout = TimeSpan.FromSeconds(10),
            };
        }

        public string Name { get; }
        public string PublicUrl { get; }
        public HttpClient Client { get; }

        public async Task StartAsync()
        {
            ProcessStartInfo startInfo = new("dotnet", $"\"{_apiDllPath}\"")
            {
                WorkingDirectory = _workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            startInfo.Environment["ASPNETCORE_URLS"] = PublicUrl;
            startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
            startInfo.Environment["ConnectionStrings__AppContext"] = $"Data Source={_dbPath}";
            startInfo.Environment["PeerSync__PublicUrl"] = PublicUrl;
            startInfo.Environment["PeerSync__PollIntervalSeconds"] = "1";
            startInfo.Environment["ErasureCoding__DataShards"] = "3";
            startInfo.Environment["ErasureCoding__ParityShards"] = "2";
            startInfo.Environment["ErasureCoding__RepairIntervalSeconds"] = _repairIntervalSeconds.ToString();
            startInfo.Environment["ErasureCoding__RepairBatchSize"] = "100";

            _process = new Process
            {
                StartInfo = startInfo,
                EnableRaisingEvents = true,
            };

            _process.OutputDataReceived += (_, args) =>
            {
                if (args.Data is not null)
                {
                    lock (_logLines)
                    {
                        _logLines.Add($"OUT {args.Data}");
                    }
                }
            };

            _process.ErrorDataReceived += (_, args) =>
            {
                if (args.Data is not null)
                {
                    lock (_logLines)
                    {
                        _logLines.Add($"ERR {args.Data}");
                    }
                }
            };

            Assert.IsTrue(_process.Start(), $"Failed to start process for {Name}.");
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();

            await WaitForHealthyAsync();
        }

        public async Task StopAsync()
        {
            if (_stopped)
            {
                return;
            }

            _stopped = true;

            if (_process is not null && !_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync();
            }
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await StopAsync();
            _process?.Dispose();
        }

        public async Task<string> FormatFailureAsync(string message)
        {
            string processState = _process is null
                ? "process not started"
                : _process.HasExited
                    ? $"process exited with code {_process.ExitCode}"
                    : "process still running";

            string[] logs;
            lock (_logLines)
            {
                logs = _logLines.TakeLast(40).ToArray();
            }

            return $"{message}{Environment.NewLine}{Name}: {processState}{Environment.NewLine}{string.Join(Environment.NewLine, logs)}";
        }

        private async Task WaitForHealthyAsync()
        {
            Exception? lastError = null;

            for (int attempt = 0; attempt < 60; attempt++)
            {
                if (_process is { HasExited: true })
                {
                    Assert.Fail(await FormatFailureAsync("API process exited before becoming healthy."));
                }

                try
                {
                    using HttpResponseMessage response = await Client.GetAsync("/api/servers");
                    if (response.IsSuccessStatusCode)
                    {
                        return;
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex;
                }

                await Task.Delay(250);
            }

            Assert.Fail(await FormatFailureAsync($"API process did not become healthy. Last error: {lastError}"));
        }
    }
}
