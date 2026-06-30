using Api.Configuration;
using Api.Entities;
using AppDbContext = Api.Context.AppContext;
using Core.Dto;
using Core.Responses.Servers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Api.Services;

internal sealed class PeerSyncService(
    AppDbContext appContext,
    IHttpClientFactory httpClientFactory,
    IOptions<PeerSyncOptions> options,
    ILogger<PeerSyncService> logger) : IPeerSyncService
{
    private const string KnownServersEndpointPath = "/api/servers";
    private readonly PeerSyncOptions _peerSyncOptions = options.Value;

    public async Task<IReadOnlyCollection<KnownServerDto>> GetOnlineServersAsync(CancellationToken cancellationToken)
    {
        List<KnownServerDto> servers = [];

        string? publicUrl = TryNormalizeUrl(_peerSyncOptions.PublicUrl);
        if (publicUrl is not null)
        {
            servers.Add(new KnownServerDto
            {
                Url = publicUrl,
            });
        }

        KnownServerDto[] peers = await appContext.KnownServers
            .Where(server => server.IsActive)
            .OrderBy(server => server.Url)
            .Select(server => new KnownServerDto
            {
                Url = server.Url,
            })
            .ToArrayAsync(cancellationToken);

        servers.AddRange(peers.Where(peer => !string.Equals(peer.Url, publicUrl, StringComparison.OrdinalIgnoreCase)));
        return servers;
    }

    public async Task PollRequesterAsync(string requesterUrl, CancellationToken cancellationToken)
    {
        await UpsertPeerAsync(requesterUrl, isActive: true, cancellationToken);
        await PollPeerAsync(requesterUrl, suppressCallback: true, cancellationToken);
    }

    public async Task PollKnownPeersAsync(CancellationToken cancellationToken)
    {
        string? publicUrl = TryNormalizeUrl(_peerSyncOptions.PublicUrl);

        string[] peers = await appContext.KnownServers
            .Where(server => server.Url != publicUrl)
            .Select(server => server.Url)
            .ToArrayAsync(cancellationToken);

        foreach (string peer in peers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await PollPeerAsync(peer, suppressCallback: false, cancellationToken);
        }
    }

    private async Task PollPeerAsync(string peerUrl, bool suppressCallback, CancellationToken cancellationToken)
    {
        string? normalizedPeerUrl = TryNormalizeUrl(peerUrl);
        if (normalizedPeerUrl is null)
        {
            logger.LogWarning("Skipping invalid peer URL '{PeerUrl}'.", peerUrl);
            return;
        }

        HttpClient client = httpClientFactory.CreateClient(nameof(PeerSyncService));
        Uri requestUri = BuildKnownServersUri(normalizedPeerUrl, suppressCallback);

        try
        {
            using HttpResponseMessage response = await client.GetAsync(requestUri, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Polling peer '{PeerUrl}' failed with status code {StatusCode}.", normalizedPeerUrl, response.StatusCode);
                await UpsertPeerAsync(normalizedPeerUrl, isActive: false, cancellationToken);
                return;
            }

            GetKnownServersResponse? payload = await response.Content.ReadFromJsonAsync<GetKnownServersResponse>(cancellationToken);
            await UpsertPeerAsync(normalizedPeerUrl, isActive: true, cancellationToken);

            if (payload?.Servers is null)
            {
                return;
            }

            foreach (KnownServerDto server in payload.Servers)
            {
                await UpsertPeerAsync(server.Url, isActive: true, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Polling peer '{PeerUrl}' failed.", normalizedPeerUrl);
            await UpsertPeerAsync(normalizedPeerUrl, isActive: false, cancellationToken);
        }
    }

    private Uri BuildKnownServersUri(string peerUrl, bool suppressCallback)
    {
        string requestUri = $"{peerUrl}{KnownServersEndpointPath}";
        string? publicUrl = TryNormalizeUrl(_peerSyncOptions.PublicUrl);

        List<string> queryParts = [];
        if (publicUrl is not null)
        {
            queryParts.Add($"requesterUrl={Uri.EscapeDataString(publicUrl)}");
        }

        if (suppressCallback)
        {
            queryParts.Add("suppressCallback=true");
        }

        if (queryParts.Count > 0)
        {
            requestUri = $"{requestUri}?{string.Join("&", queryParts)}";
        }

        return new Uri(requestUri, UriKind.Absolute);
    }

    private async Task UpsertPeerAsync(string peerUrl, bool isActive, CancellationToken cancellationToken)
    {
        string? normalizedPeerUrl = TryNormalizeUrl(peerUrl);
        string? publicUrl = TryNormalizeUrl(_peerSyncOptions.PublicUrl);

        if (normalizedPeerUrl is null || string.Equals(normalizedPeerUrl, publicUrl, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        DateTime now = DateTime.UtcNow;

        for (int attempt = 0; attempt < 2; attempt++)
        {
            KnownServerEntity? entity = await appContext.KnownServers
                .SingleOrDefaultAsync(server => server.Url == normalizedPeerUrl, cancellationToken);

            if (entity is null)
            {
                entity = new KnownServerEntity
                {
                    Url = normalizedPeerUrl,
                    IsActive = isActive,
                    FirstSeenAtUtc = now,
                    LastSeenAtUtc = isActive ? now : null,
                };

                await appContext.AddAsync(entity, cancellationToken);
            }
            else
            {
                entity.IsActive = isActive;
                if (isActive)
                {
                    entity.LastSeenAtUtc = now;
                }
            }

            try
            {
                await appContext.SaveChangesAsync(cancellationToken);
                return;
            }
            catch (DbUpdateException) when (attempt == 0)
            {
                foreach (var entry in appContext.ChangeTracker.Entries<KnownServerEntity>()
                    .Where(entry => entry.Entity.Url == normalizedPeerUrl))
                {
                    entry.State = EntityState.Detached;
                }
            }
        }
    }

    private static string? TryNormalizeUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
        {
            return null;
        }

        return uri.AbsoluteUri.TrimEnd('/');
    }
}
