using System.Net;
using System.Net.Sockets;
using System.Net.Http.Json;
using Core.Dto;
using Core.Requests.Messages;
using Core.Responses.Messages;

namespace Api.Benchmarks.Infrastructure;

internal sealed class ApiCluster : IAsyncDisposable
{
    private readonly string _rootDirectory;

    private ApiCluster(string rootDirectory, ApiNode[] nodes)
    {
        _rootDirectory = rootDirectory;
        Nodes = nodes;
    }

    public ApiNode[] Nodes { get; }

    public static async Task<ApiCluster> StartAsync(int nodeCount)
    {
        string solutionRoot = FindSolutionRoot();
        string apiDllPath = Path.Combine(solutionRoot, "src", "Api", "bin", "Debug", "net10.0", "Api.dll");
        if (!File.Exists(apiDllPath))
        {
            throw new InvalidOperationException($"Expected API assembly at '{apiDllPath}'. Build the solution first.");
        }

        string rootDirectory = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"messaging-2-benchmarks-{Guid.NewGuid():N}")).FullName;

        ApiNode[] nodes = new ApiNode[nodeCount];

        try
        {
            for (int i = 0; i < nodeCount; i++)
            {
                int port = GetFreeTcpPort();
                string publicUrl = $"http://127.0.0.1:{port}";
                string dbPath = Path.Combine(rootDirectory, $"node-{i + 1}.db");
                nodes[i] = new ApiNode(apiDllPath, $"node-{i + 1}", publicUrl, dbPath, rootDirectory);
                await nodes[i].StartAsync();
            }

            ApiCluster cluster = new(rootDirectory, nodes);
            await cluster.ConnectAllAsync();
            return cluster;
        }
        catch
        {
            foreach (ApiNode node in nodes.Where(node => node is not null))
            {
                await node.DisposeAsync();
            }

            try
            {
                Directory.Delete(rootDirectory, recursive: true);
            }
            catch
            {
            }

            throw;
        }
    }

    public async Task<long> CreateMessageAsync(int nodeIndex, string roomHash)
    {
        CreateMessagesRequest request = new()
        {
            Messages =
            [
                new EncryptedMessageDto
                {
                    Id = 0,
                    RoomHash = roomHash,
                    SenderPublicKey = "sender-public-key",
                    Nonce = "nonce",
                    CypherText = "cipher-text",
                    Signature = "signature",
                },
            ],
        };

        using HttpResponseMessage response = await Nodes[nodeIndex].Client.PostAsJsonAsync("/api/messages", request);
        response.EnsureSuccessStatusCode();

        return await GetMaxMessageIdAsync(nodeIndex, roomHash);
    }

    public async Task<GetMessagesResponse> GetMessagesAsync(int nodeIndex, string roomHash, long maxId)
    {
        GetMessagesResponse? response = await Nodes[nodeIndex].Client.GetFromJsonAsync<GetMessagesResponse>(
            $"/api/messages?roomHash={Uri.EscapeDataString(roomHash)}&maxId={maxId}&numberToFetch=1");

        return response ?? throw new InvalidOperationException("Message response body was null.");
    }

    public async ValueTask DisposeAsync()
    {
        foreach (ApiNode node in Nodes)
        {
            await node.DisposeAsync();
        }

        try
        {
            Directory.Delete(_rootDirectory, recursive: true);
        }
        catch
        {
        }
    }

    private async Task ConnectAllAsync()
    {
        for (int i = 0; i < Nodes.Length; i++)
        {
            for (int j = i + 1; j < Nodes.Length; j++)
            {
                await ConnectPairAsync(Nodes[i], Nodes[j]);
            }
        }
    }

    private static async Task ConnectPairAsync(ApiNode left, ApiNode right)
    {
        using HttpResponseMessage forwardResponse = await right.Client.GetAsync(
            $"/api/servers?requesterUrl={Uri.EscapeDataString(left.PublicUrl)}");
        using HttpResponseMessage reverseResponse = await left.Client.GetAsync(
            $"/api/servers?requesterUrl={Uri.EscapeDataString(right.PublicUrl)}");

        if (forwardResponse.StatusCode != HttpStatusCode.OK || reverseResponse.StatusCode != HttpStatusCode.OK)
        {
            throw new InvalidOperationException(
                $"Failed to connect peers '{left.Name}' and '{right.Name}': {forwardResponse.StatusCode}, {reverseResponse.StatusCode}.");
        }
    }

    private async Task<long> GetMaxMessageIdAsync(int nodeIndex, string roomHash)
    {
        GetMessagesResponse response = await GetMessagesAsync(nodeIndex, roomHash, long.MaxValue);
        if (response.Messages.Length == 0)
        {
            throw new InvalidOperationException("Expected created message to be retrievable.");
        }

        return response.Messages.Max(message => message.Id);
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

    private static string FindSolutionRoot()
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
