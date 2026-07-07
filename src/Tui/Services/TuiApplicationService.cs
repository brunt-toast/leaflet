using System.Text;
using Core.Dto;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Spectre.Console;
using Spectre.Console.Rendering;
using Tui.Configuration;

namespace Tui.Services;

internal sealed class TuiApplicationService
{
    private static readonly TimeSpan s_inputPollInterval = TimeSpan.FromMilliseconds(40);
    private readonly TuiAppConfig _config;
    private readonly MessagingApiClientService _apiClient;
    private readonly ServerDiscoveryService _serverDiscoveryService;
    private readonly IChatCryptoService _cryptoService;
    private readonly IAnsiConsole _console;
    private readonly ILogger<TuiApplicationService> _logger;

    public TuiApplicationService(
        IOptions<TuiAppConfig> configOptions,
        MessagingApiClientService apiClient,
        ServerDiscoveryService serverDiscoveryService,
        IChatCryptoService cryptoService,
        IAnsiConsole console,
        ILogger<TuiApplicationService> logger)
    {
        _config = configOptions.Value;
        _apiClient = apiClient;
        _serverDiscoveryService = serverDiscoveryService;
        _cryptoService = cryptoService;
        _console = console;
        _logger = logger;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        ServerConfig server = await ResolveServerAsync(cancellationToken);
        IReadOnlyList<RoomListEntry> roomEntries = BuildRoomEntries(_config.RoomsRoot);
        RoomListEntry? initiallySelectedRoom = roomEntries.FirstOrDefault(static entry => entry.Room is not null);

        if (initiallySelectedRoom?.Room is null)
        {
            _logger.LogWarning("No rooms were configured in the client configuration.");
            _console.MarkupLine("[red]No rooms were configured under [[rooms]].[/]");
            return;
        }

        RoomLeafNode selectedRoom = initiallySelectedRoom.Room;
        StringBuilder composeBuffer = new();
        DateTimeOffset nextRefreshAt = DateTimeOffset.MinValue;
        DateTimeOffset lastRefreshAt = DateTimeOffset.MinValue;
        RoomViewState roomState = RoomViewState.Empty(selectedRoom);
        bool needsRender = true;

        _console.Cursor.Hide();

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (roomState.Room.Path != selectedRoom.Path)
                {
                    roomState = RoomViewState.Empty(selectedRoom);
                    composeBuffer.Clear();
                    nextRefreshAt = DateTimeOffset.MinValue;
                    needsRender = true;
                }

                DateTimeOffset now = DateTimeOffset.UtcNow;
                if (now >= nextRefreshAt)
                {
                    server = await ResolveServerAsync(cancellationToken);
                    roomState = await RefreshRoomAsync(selectedRoom, roomState, server, cancellationToken);
                    lastRefreshAt = DateTimeOffset.UtcNow;
                    nextRefreshAt = lastRefreshAt.AddSeconds(_config.Core.RefreshIntervalSeconds);
                    needsRender = true;
                }

                InputResult inputResult = await HandleInputAsync(
                    roomEntries,
                    selectedRoom,
                    composeBuffer,
                    roomState,
                    nextRefreshAt,
                    server,
                    cancellationToken);

                selectedRoom = inputResult.SelectedRoom;
                roomState = inputResult.RoomState;
                nextRefreshAt = inputResult.NextRefreshAt;
                needsRender |= inputResult.Handled;

                if (needsRender)
                {
                    Render(roomEntries, selectedRoom, roomState, composeBuffer, server, lastRefreshAt);
                    needsRender = false;
                }

                await Task.Delay(s_inputPollInterval, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            _console.Cursor.Show();
            _console.Clear();
        }
    }

    private async Task<ServerConfig> ResolveServerAsync(CancellationToken cancellationToken)
    {
        if (await _serverDiscoveryService.IsDiscoverableAsync(_config.Servers.Main, cancellationToken))
        {
            return _config.Servers.Main;
        }

        _logger.LogWarning("Primary server {ServerUrl} is unavailable. Checking backups.", _config.Servers.Main.Url);
        foreach (ServerConfig backup in _config.Servers.Backups)
        {
            if (await _serverDiscoveryService.IsDiscoverableAsync(backup, cancellationToken))
            {
                _logger.LogInformation("Selected backup server {ServerUrl}.", backup.Url);
                return backup;
            }
        }

        _logger.LogWarning("No discoverable backup servers were found. Falling back to primary server {ServerUrl}.", _config.Servers.Main.Url);
        return _config.Servers.Main;
    }

    private async Task<InputResult> HandleInputAsync(
        IReadOnlyList<RoomListEntry> roomEntries,
        RoomLeafNode selectedRoom,
        StringBuilder composeBuffer,
        RoomViewState roomState,
        DateTimeOffset nextRefreshAt,
        ServerConfig server,
        CancellationToken cancellationToken)
    {
        bool handled = false;

        while (Console.KeyAvailable)
        {
            ConsoleKeyInfo keyInfo = Console.ReadKey(intercept: true);

            switch (keyInfo.Key)
            {
                case ConsoleKey.UpArrow:
                    selectedRoom = MoveRoomSelection(roomEntries, selectedRoom, -1);
                    handled = true;
                    break;
                case ConsoleKey.DownArrow:
                    selectedRoom = MoveRoomSelection(roomEntries, selectedRoom, 1);
                    handled = true;
                    break;
                case ConsoleKey.Backspace:
                    if (composeBuffer.Length > 0)
                    {
                        composeBuffer.Length--;
                        handled = true;
                    }

                    break;
                case ConsoleKey.Enter:
                    if (composeBuffer.Length > 0)
                    {
                        roomState = await SendMessageAsync(selectedRoom, roomState, composeBuffer, server, cancellationToken);
                        nextRefreshAt = DateTimeOffset.MinValue;
                        handled = true;
                    }

                    break;
                case ConsoleKey.Escape:
                    if (composeBuffer.Length > 0)
                    {
                        composeBuffer.Clear();
                        roomState = roomState with
                        {
                            Status = "Draft cleared."
                        };
                        handled = true;
                    }

                    break;
                default:
                    if (!char.IsControl(keyInfo.KeyChar))
                    {
                        composeBuffer.Append(keyInfo.KeyChar);
                        handled = true;
                    }

                    break;
            }
        }

        return new InputResult(selectedRoom, roomState, nextRefreshAt, handled);
    }

    private async Task<RoomViewState> RefreshRoomAsync(
        RoomLeafNode room,
        RoomViewState currentState,
        ServerConfig server,
        CancellationToken cancellationToken)
    {
        try
        {
            string roomHash = _cryptoService.ComputeRoomHash(room.Key);
            IReadOnlyList<EncryptedMessageDto> messages = await _apiClient.GetMessagesAsync(
                server,
                roomHash,
                _config.Core.HistoryCount,
                cancellationToken);

            IReadOnlyList<RenderedMessage> renderedMessages = messages
                .Select(message => _cryptoService.TryReadMessage(room, message))
                .ToArray();

            _logger.LogInformation(
                "Refreshed room {RoomPath} from server {ServerUrl} with {MessageCount} messages.",
                room.Path,
                server.Url,
                renderedMessages.Count);
            return new RoomViewState(room, renderedMessages, null, $"Synced {renderedMessages.Count} message(s).");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Refreshing room {RoomPath} from server {ServerUrl} failed.", room.Path, server.Url);
            return currentState with
            {
                Error = ex.Message,
                Status = "Refresh failed."
            };
        }
    }

    private async Task<RoomViewState> SendMessageAsync(
        RoomLeafNode room,
        RoomViewState currentState,
        StringBuilder composeBuffer,
        ServerConfig server,
        CancellationToken cancellationToken)
    {
        if (!_config.Identities.TryGetValue(room.IdentityName, out IdentityConfig? identity))
        {
            throw new InvalidOperationException($"Room '{room.Path}' references unknown identity '{room.IdentityName}'.");
        }

        string text = composeBuffer.ToString().Trim();
        if (string.IsNullOrEmpty(text))
        {
            return currentState with
            {
                Status = "Message cannot be empty."
            };
        }

        try
        {
            server = await ResolveServerAsync(cancellationToken);
            EncryptedMessageDto dto = _cryptoService.CreateEncryptedMessage(room, identity, text);
            await _apiClient.SendMessageAsync(server, dto, cancellationToken);
            _logger.LogInformation("Sent message for room {RoomPath} via server {ServerUrl}.", room.Path, server.Url);
            composeBuffer.Clear();
            return currentState with
            {
                Status = "Message sent.",
                Error = null
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Sending message for room {RoomPath} via server {ServerUrl} failed.", room.Path, server.Url);
            return currentState with
            {
                Error = ex.Message,
                Status = "Send failed."
            };
        }
    }

    private void Render(
        IReadOnlyList<RoomListEntry> roomEntries,
        RoomLeafNode selectedRoom,
        RoomViewState roomState,
        StringBuilder composeBuffer,
        ServerConfig server,
        DateTimeOffset lastRefreshAt)
    {
        IdentityConfig identity = _config.Identities[selectedRoom.IdentityName];

        Layout root = new("Root");
        root.SplitColumns(
            new Layout("Rooms").Size(32),
            new Layout("Main"));

        root["Rooms"].Update(BuildRoomsPanel(roomEntries, selectedRoom));

        Layout mainLayout = root["Main"];
        mainLayout.SplitRows(
            new Layout("Header").Size(5),
            new Layout("Messages"),
            new Layout("Compose").Size(7));

        mainLayout["Header"].Update(BuildHeaderPanel(selectedRoom, identity, server, roomState, lastRefreshAt));
        mainLayout["Messages"].Update(BuildMessagesPanel(roomState));
        mainLayout["Compose"].Update(BuildComposePanel(composeBuffer));

        _console.Clear();
        _console.Write(root);
    }

    private Panel BuildRoomsPanel(IReadOnlyList<RoomListEntry> roomEntries, RoomLeafNode selectedRoom)
    {
        List<IRenderable> rows = [];

        foreach (RoomListEntry roomEntry in roomEntries)
        {
            string indent = new(' ', roomEntry.Depth * 2);

            if (roomEntry.Room is null)
            {
                rows.Add(new Text(indent + roomEntry.Name, new Style(Color.Aqua)));
                continue;
            }

            bool isSelected = roomEntry.Room.Path == selectedRoom.Path;
            string selectedPrefix = isSelected ? "> " : "  ";
            Style rowStyle = isSelected
                ? new Style(Color.Black, Color.Yellow)
                : Style.Plain;
            rows.Add(new Text(selectedPrefix + indent + roomEntry.Name, rowStyle));
        }

        return new Panel(new Rows(rows.ToArray()))
        {
            Header = new PanelHeader("Rooms"),
            Border = BoxBorder.Rounded,
            Expand = true
        };
    }

    private Panel BuildHeaderPanel(
        RoomLeafNode selectedRoom,
        IdentityConfig identity,
        ServerConfig server,
        RoomViewState roomState,
        DateTimeOffset lastRefreshAt)
    {
        string refreshText = lastRefreshAt == DateTimeOffset.MinValue
            ? "pending"
            : lastRefreshAt.ToLocalTime().ToString("u");

        Grid grid = new();
        grid.AddColumn();
        grid.AddColumn();
        grid.AddRow(
            new Markup($"[bold]{Markup.Escape(selectedRoom.Path["rooms.".Length..])}[/]"),
            new Markup($"[grey]Identity:[/] {Markup.Escape(identity.Name)}"));
        grid.AddRow(
            new Markup($"[grey]Server:[/] {Markup.Escape(server.Name)}"),
            new Markup($"[grey]Refresh:[/] {Markup.Escape(refreshText)}"));

        if (!string.IsNullOrWhiteSpace(roomState.Status))
        {
            grid.AddRow(
                new Markup($"[grey]Status:[/] {Markup.Escape(roomState.Status)}"),
                string.IsNullOrWhiteSpace(roomState.Error)
                    ? new Markup("[grey]Auto-refresh is on[/]")
                    : new Markup($"[red]{Markup.Escape(roomState.Error)}[/]"));
        }

        return new Panel(grid)
        {
            Header = new PanelHeader("Room"),
            Border = BoxBorder.Rounded,
            Expand = true
        };
    }

    private Panel BuildMessagesPanel(RoomViewState roomState)
    {
        if (!string.IsNullOrWhiteSpace(roomState.Error))
        {
            return new Panel(new Markup($"[red]{Markup.Escape(roomState.Error)}[/]"))
            {
                Header = new PanelHeader("Chat"),
                Border = BoxBorder.Rounded,
                Expand = true
            };
        }

        if (roomState.Messages.Count == 0)
        {
            return new Panel(new Markup("[grey]No messages yet.[/]"))
            {
                Header = new PanelHeader("Chat"),
                Border = BoxBorder.Rounded,
                Expand = true
            };
        }

        int senderColumnWidth = roomState.Messages.Max(static message => message.Sender.Length);
        Grid grid = new();
        grid.AddColumn(new GridColumn().NoWrap());
        grid.AddColumn(new GridColumn().NoWrap());
        grid.AddColumn();

        foreach (RenderedMessage message in roomState.Messages)
        {
            grid.AddRow(
                new Markup($"[grey]{Markup.Escape(FormatMessageTimestamp(message.SentAtUtc))}[/]"),
                new Text(PadSender(message.Sender, senderColumnWidth), new Style(Color.Blue)),
                BuildMessageBody(message));
        }

        return new Panel(grid)
        {
            Header = new PanelHeader("Chat"),
            Border = BoxBorder.Rounded,
            Expand = true
        };
    }

    private Panel BuildComposePanel(StringBuilder composeBuffer)
    {
        string draft = composeBuffer.Length == 0
            ? "Type a message and press Enter to send. Use Up/Down to change rooms."
            : composeBuffer.ToString();

        string style = composeBuffer.Length == 0 ? "grey" : "white";
        return new Panel(new Markup($"[{style}]{Markup.Escape(draft)}[/]"))
        {
            Header = new PanelHeader("Compose"),
            Border = BoxBorder.Rounded,
            Expand = true
        };
    }

    private static IReadOnlyList<RoomListEntry> BuildRoomEntries(RoomGroupNode root)
    {
        List<RoomListEntry> entries = [];
        foreach (RoomTreeNode child in root.Children)
        {
            AddEntries(child, depth: 0, entries);
        }

        return entries;
    }

    private static void AddEntries(RoomTreeNode node, int depth, IList<RoomListEntry> entries)
    {
        switch (node)
        {
            case RoomGroupNode group:
            {
                entries.Add(new RoomListEntry(group.Name, depth, null));
                foreach (RoomTreeNode child in group.Children)
                {
                    AddEntries(child, depth + 1, entries);
                }

                return;
            }
            case RoomLeafNode room:
                entries.Add(new RoomListEntry(room.Name, depth, room));
                break;
        }
    }

    private static RoomLeafNode MoveRoomSelection(
        IReadOnlyList<RoomListEntry> roomEntries,
        RoomLeafNode selectedRoom,
        int direction)
    {
        List<RoomLeafNode> rooms = roomEntries
            .Where(entry => entry.Room is not null)
            .Select(entry => entry.Room!)
            .ToList();

        int selectedIndex = rooms.FindIndex(room => room.Path == selectedRoom.Path);
        int nextIndex = Math.Clamp(selectedIndex + direction, 0, rooms.Count - 1);
        return rooms[nextIndex];
    }

    private static string PadSender(string sender, int senderColumnWidth)
    {
        return sender.PadLeft(senderColumnWidth);
    }

    private static string FormatMessageTimestamp(DateTimeOffset sentAtUtc)
    {
        return sentAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    }

    private static IRenderable BuildMessageBody(RenderedMessage message)
    {
        if (message.IsError)
        {
            return new Markup($"[red]{Markup.Escape(message.Body)}[/]");
        }

        if (!message.IsVerified)
        {
            return new Markup($"[yellow]{Markup.Escape(message.Body)}[/]");
        }

        return new Markup($"[white]{Markup.Escape(message.Body)}[/]");
    }
}

internal sealed record RoomListEntry
{
    public RoomListEntry(string name, int depth, RoomLeafNode? room)
    {
        Name = name;
        Depth = depth;
        Room = room;
    }

    public string Name { get; init; }

    public int Depth { get; init; }

    public RoomLeafNode? Room { get; init; }
}

internal sealed record RoomViewState
{
    public RoomViewState(
        RoomLeafNode room,
        IReadOnlyList<RenderedMessage> messages,
        string? error,
        string status)
    {
        Room = room;
        Messages = messages;
        Error = error;
        Status = status;
    }

    public RoomLeafNode Room { get; init; }

    public IReadOnlyList<RenderedMessage> Messages { get; init; }

    public string? Error { get; init; }

    public string Status { get; init; }

    public static RoomViewState Empty(RoomLeafNode room) => new(room, [], null, "Ready.");
}

internal sealed record InputResult
{
    public InputResult(
        RoomLeafNode selectedRoom,
        RoomViewState roomState,
        DateTimeOffset nextRefreshAt,
        bool handled)
    {
        SelectedRoom = selectedRoom;
        RoomState = roomState;
        NextRefreshAt = nextRefreshAt;
        Handled = handled;
    }

    public RoomLeafNode SelectedRoom { get; init; }

    public RoomViewState RoomState { get; init; }

    public DateTimeOffset NextRefreshAt { get; init; }

    public bool Handled { get; init; }
}
