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
    private static readonly TimeSpan s_minServerDiscoveryCacheDuration = TimeSpan.FromSeconds(30);
    private const int RoomsPanelWidth = 32;
    private const int HeaderPanelHeight = 5;
    private const int ComposePanelHeight = 7;
    private const int PanelBorderPaddingWidth = 4;
    private const int MessageBodyIndentWidth = 4;
    private readonly TuiAppConfig _config;
    private readonly MessageVisibilityFilter _messageVisibilityFilter;
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
        _messageVisibilityFilter = new MessageVisibilityFilter(_config.Filters);
        _apiClient = apiClient;
        _serverDiscoveryService = serverDiscoveryService;
        _cryptoService = cryptoService;
        _console = console;
        _logger = logger;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        ServerConfig server = await ResolveServerAsync(cancellationToken);
        DateTimeOffset nextServerRefreshAt = DateTimeOffset.UtcNow.Add(GetServerDiscoveryCacheDuration());
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
        List<PendingLocalMessage> pendingMessages = [];
        List<PendingSendOperation> pendingSendOperations = [];
        bool needsRender = true;
        TerminalSize terminalSize = GetTerminalSize();

        _console.Cursor.Hide();

        try
        {
            Layout initialLayout = BuildRootLayout(
                roomEntries,
                selectedRoom,
                roomState,
                composeBuffer,
                server,
                lastRefreshAt,
                pendingMessages);

            await _console
                .Live(initialLayout)
                .AutoClear(false)
                .StartAsync(async context =>
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    TerminalSize currentTerminalSize = GetTerminalSize();
                    if (!terminalSize.Equals(currentTerminalSize))
                    {
                        terminalSize = currentTerminalSize;
                        needsRender = true;
                    }

                    if (roomState.Room.Path != selectedRoom.Path)
                    {
                        roomState = RoomViewState.Empty(selectedRoom);
                        composeBuffer.Clear();
                        nextRefreshAt = DateTimeOffset.MinValue;
                        needsRender = true;
                    }

                    DateTimeOffset now = DateTimeOffset.UtcNow;
                    if (now >= nextServerRefreshAt)
                    {
                        server = await ResolveServerAsync(cancellationToken);
                        nextServerRefreshAt = DateTimeOffset.UtcNow.Add(GetServerDiscoveryCacheDuration());
                    }

                    if (now >= nextRefreshAt)
                    {
                        roomState = await RefreshRoomAsync(selectedRoom, roomState, server, cancellationToken);
                        if (!string.IsNullOrWhiteSpace(roomState.Error))
                        {
                            nextServerRefreshAt = DateTimeOffset.MinValue;
                        }

                        lastRefreshAt = DateTimeOffset.UtcNow;
                        nextRefreshAt = lastRefreshAt.AddSeconds(_config.Core.RefreshIntervalSeconds);
                        needsRender = true;
                    }

                    SendProcessingResult sendProcessingResult = await ProcessCompletedSendOperationsAsync(
                        selectedRoom,
                        roomState,
                        pendingMessages,
                        pendingSendOperations,
                        cancellationToken);
                    roomState = sendProcessingResult.RoomState;
                    if (sendProcessingResult.ShouldRefreshImmediately)
                    {
                        nextRefreshAt = DateTimeOffset.MinValue;
                    }

                    if (sendProcessingResult.ShouldRefreshServer)
                    {
                        nextServerRefreshAt = DateTimeOffset.MinValue;
                    }

                    needsRender |= sendProcessingResult.Handled;

                    InputResult inputResult = await HandleInputAsync(
                        roomEntries,
                        selectedRoom,
                        composeBuffer,
                        roomState,
                        nextRefreshAt,
                        server,
                        pendingMessages,
                        pendingSendOperations,
                        cancellationToken);

                    selectedRoom = inputResult.SelectedRoom;
                    roomState = inputResult.RoomState;
                    nextRefreshAt = inputResult.NextRefreshAt;
                    needsRender |= inputResult.Handled;

                    if (needsRender)
                    {
                        Render(
                            context,
                            roomEntries,
                            selectedRoom,
                            roomState,
                            composeBuffer,
                            server,
                            lastRefreshAt,
                            pendingMessages);
                        needsRender = false;
                    }

                    await Task.Delay(s_inputPollInterval, cancellationToken);
                }
            });
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
        IList<PendingLocalMessage> pendingMessages,
        IList<PendingSendOperation> pendingSendOperations,
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
                        SendStartResult sendStartResult = StartSendMessage(
                            selectedRoom,
                            roomState,
                            composeBuffer,
                            server,
                            pendingMessages,
                            pendingSendOperations,
                            cancellationToken);
                        roomState = sendStartResult.RoomState;
                        nextRefreshAt = sendStartResult.NextRefreshAt;
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

    private async Task<SendProcessingResult> ProcessCompletedSendOperationsAsync(
        RoomLeafNode selectedRoom,
        RoomViewState roomState,
        IList<PendingLocalMessage> pendingMessages,
        IList<PendingSendOperation> pendingSendOperations,
        CancellationToken cancellationToken)
    {
        bool handled = false;
        bool shouldRefreshImmediately = false;
        bool shouldRefreshServer = false;

        for (int index = pendingSendOperations.Count - 1; index >= 0; index--)
        {
            PendingSendOperation pendingOperation = pendingSendOperations[index];
            if (!pendingOperation.Completion.IsCompleted)
            {
                continue;
            }

            SendCompletionResult completionResult = await pendingOperation.Completion.WaitAsync(cancellationToken);
            pendingSendOperations.RemoveAt(index);
            handled = true;

            PendingLocalMessage? pendingMessage = pendingMessages
                .SingleOrDefault(message => message.LocalId == pendingOperation.LocalId);

            if (completionResult.Succeeded)
            {
                if (pendingMessage is not null)
                {
                    pendingMessages.Remove(pendingMessage);
                }

                shouldRefreshImmediately = true;

                if (selectedRoom.Path == pendingOperation.Room.Path)
                {
                    roomState = roomState with
                    {
                        Status = "Message sent.",
                        Error = null
                    };
                }

                continue;
            }

            if (pendingMessage is not null)
            {
                RenderedMessage currentMessage = pendingMessage.Message;
                pendingMessage.Message = new RenderedMessage
                {
                    LocalId = currentMessage.LocalId,
                    SentAtUtc = currentMessage.SentAtUtc,
                    Sender = currentMessage.Sender,
                    SenderKeyHash = currentMessage.SenderKeyHash,
                    Body = currentMessage.Body,
                    IsVerified = currentMessage.IsVerified,
                    IsError = currentMessage.IsError,
                    IsPending = false,
                    DeliveryFailed = true
                };
            }

            if (selectedRoom.Path == pendingOperation.Room.Path)
            {
                roomState = roomState with
                {
                    Error = null,
                    Status = $"Send failed: {completionResult.ErrorMessage}"
                };
            }

            shouldRefreshServer = true;
        }

        return new SendProcessingResult(roomState, handled, shouldRefreshImmediately, shouldRefreshServer);
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
            long? sinceId = currentState.LastMessageId > 0 ? currentState.LastMessageId : null;
            IReadOnlyList<EncryptedMessageDto> messages = await _apiClient.GetMessagesAsync(
                server,
                roomHash,
                _config.Core.HistoryCount,
                sinceId,
                cancellationToken);

            IReadOnlyList<RenderedMessage> renderedMessages = messages
                .Select(message => _cryptoService.TryReadMessage(room, message))
                .ToArray();
            IReadOnlyList<RenderedMessage> mergedMessages = sinceId.HasValue
                ? currentState.Messages
                    .Concat(renderedMessages)
                    .TakeLast(_config.Core.HistoryCount)
                    .ToArray()
                : renderedMessages;
            long lastMessageId = messages.Count > 0
                ? messages.Max(message => message.Id)
                : currentState.LastMessageId;

            _logger.LogInformation(
                "Refreshed room {RoomPath} from server {ServerUrl} with {MessageCount} messages.",
                room.Path,
                server.Url,
                renderedMessages.Count);
            return new RoomViewState(room, mergedMessages, null, $"Synced {renderedMessages.Count} new message(s).", lastMessageId);
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

    private SendStartResult StartSendMessage(
        RoomLeafNode room,
        RoomViewState currentState,
        StringBuilder composeBuffer,
        ServerConfig server,
        IList<PendingLocalMessage> pendingMessages,
        IList<PendingSendOperation> pendingSendOperations,
        CancellationToken cancellationToken)
    {
        if (!_config.Identities.TryGetValue(room.IdentityName, out IdentityConfig? identity))
        {
            throw new InvalidOperationException($"Room '{room.Path}' references unknown identity '{room.IdentityName}'.");
        }

        string text = composeBuffer.ToString().Trim();
        if (string.IsNullOrEmpty(text))
        {
            return new SendStartResult(
                currentState with
                {
                    Status = "Message cannot be empty."
                },
                DateTimeOffset.MinValue);
        }

        string localId = Guid.NewGuid().ToString("N");
        RenderedMessage pendingMessage = CreatePendingMessage(identity, text, localId);
        pendingMessages.Add(new PendingLocalMessage(room.Path, localId, pendingMessage));
        composeBuffer.Clear();

        pendingSendOperations.Add(new PendingSendOperation(
            localId,
            room,
            SendMessageCoreAsync(room, server, identity, text, cancellationToken)));

        return new SendStartResult(
            currentState with
            {
                Status = "Sending message...",
                Error = null
            },
            nextRefreshAt: DateTimeOffset.MaxValue);
    }

    private async Task<SendCompletionResult> SendMessageCoreAsync(
        RoomLeafNode room,
        ServerConfig server,
        IdentityConfig identity,
        string text,
        CancellationToken cancellationToken)
    {
        try
        {
            EncryptedMessageDto dto = _cryptoService.CreateEncryptedMessage(room, identity, text);
            await _apiClient.SendMessageAsync(server, dto, cancellationToken);
            _logger.LogInformation("Sent message for room {RoomPath} via server {ServerUrl}.", room.Path, server.Url);
            return SendCompletionResult.Success();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Sending message for room {RoomPath} via server {ServerUrl} failed.", room.Path, server.Url);
            return SendCompletionResult.Failure(ex.Message);
        }
    }

    private void Render(
        LiveDisplayContext context,
        IReadOnlyList<RoomListEntry> roomEntries,
        RoomLeafNode selectedRoom,
        RoomViewState roomState,
        StringBuilder composeBuffer,
        ServerConfig server,
        DateTimeOffset lastRefreshAt,
        IReadOnlyList<PendingLocalMessage> pendingMessages)
    {
        context.UpdateTarget(BuildRootLayout(
            roomEntries,
            selectedRoom,
            roomState,
            composeBuffer,
            server,
            lastRefreshAt,
            pendingMessages));
        context.Refresh();
    }

    private Layout BuildRootLayout(
        IReadOnlyList<RoomListEntry> roomEntries,
        RoomLeafNode selectedRoom,
        RoomViewState roomState,
        StringBuilder composeBuffer,
        ServerConfig server,
        DateTimeOffset lastRefreshAt,
        IReadOnlyList<PendingLocalMessage> pendingMessages)
    {
        IdentityConfig identity = _config.Identities[selectedRoom.IdentityName];

        Layout root = new("Root");
        root.SplitColumns(
            new Layout("Rooms").Size(RoomsPanelWidth),
            new Layout("Main"));

        root["Rooms"].Update(BuildRoomsPanel(roomEntries, selectedRoom));

        Layout mainLayout = root["Main"];
        mainLayout.SplitRows(
            new Layout("Header").Size(HeaderPanelHeight),
            new Layout("Messages"),
            new Layout("Compose").Size(ComposePanelHeight));

        mainLayout["Header"].Update(BuildHeaderPanel(selectedRoom, identity, server, roomState, lastRefreshAt));
        mainLayout["Messages"].Update(BuildMessagesPanel(roomState, pendingMessages));
        mainLayout["Compose"].Update(BuildComposePanel(composeBuffer));

        return root;
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

    private Panel BuildMessagesPanel(RoomViewState roomState, IReadOnlyList<PendingLocalMessage> pendingMessages)
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

        IReadOnlyList<RenderedMessage> messages = GetVisibleMessages(roomState, pendingMessages);
        IReadOnlyList<RenderedMessage> viewportMessages = TakeMessagesThatFitViewport(messages);

        if (viewportMessages.Count == 0)
        {
            return new Panel(new Markup("[grey]No messages yet.[/]"))
            {
                Header = new PanelHeader("Chat"),
                Border = BoxBorder.Rounded,
                Expand = true
            };
        }

        List<IRenderable> chatRows = [];

        foreach (RenderedMessage message in viewportMessages)
        {
            if (message.IsHidden)
            {
                chatRows.Add(BuildHiddenMessageLine(message));
                continue;
            }

            chatRows.Add(BuildMessageMetadataLine(message));
            chatRows.Add(BuildIndentedMessageBody(message));
        }

        return new Panel(new Rows(chatRows.ToArray()))
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

    private static string FormatMessageTimestamp(DateTimeOffset sentAtUtc)
    {
        return sentAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    }

    private static IRenderable BuildMessageBody(RenderedMessage message)
    {
        if (message.DeliveryFailed)
        {
            return new Markup($"[red]{Markup.Escape(message.Body)}[/]");
        }

        if (message.IsPending)
        {
            return new Markup($"[grey]{Markup.Escape(message.Body)}[/]");
        }

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

    private static IRenderable BuildMessageMetadataLine(RenderedMessage message)
    {
        string timestamp = Markup.Escape(FormatMessageTimestamp(message.SentAtUtc));
        string sender = Markup.Escape(message.Sender);
        string statusSuffix = message.DeliveryFailed
            ? " [red](failed)[/]"
            : message.IsPending
                ? " [grey](sending...)[/]"
                : string.Empty;

        if (string.IsNullOrWhiteSpace(message.SenderKeyHash))
        {
            return new Markup($"[grey]{timestamp}[/] [blue]{sender}[/]{statusSuffix}");
        }

        string senderKeyHash = Markup.Escape(message.SenderKeyHash);
        return new Markup($"[grey]{timestamp}[/] [blue]{sender}[/] [grey]{senderKeyHash}[/]{statusSuffix}");
    }

    private static IRenderable BuildIndentedMessageBody(RenderedMessage message)
    {
        Grid grid = new();
        grid.AddColumn(new GridColumn().Width(4));
        grid.AddColumn();
        grid.AddRow(new Text("    "), BuildMessageBody(message));
        return grid;
    }

    private static IRenderable BuildHiddenMessageLine(RenderedMessage message)
    {
        return new Markup($"[grey]{Markup.Escape(message.Body)}[/]");
    }

    private static RenderedMessage CreatePendingMessage(IdentityConfig identity, string text, string localId)
    {
        return new RenderedMessage
        {
            LocalId = localId,
            SentAtUtc = DateTimeOffset.UtcNow,
            Sender = identity.Name,
            SenderKeyHash = SenderKeyDisplayFormatter.Format(identity.PublicKey),
            Body = text,
            IsVerified = true,
            IsError = false,
            IsPending = true,
            DeliveryFailed = false
        };
    }

    private IReadOnlyList<RenderedMessage> GetVisibleMessages(
        RoomViewState roomState,
        IReadOnlyList<PendingLocalMessage> pendingMessages)
    {
        RenderedMessage[] roomPendingMessages = pendingMessages
            .Where(message => message.RoomPath == roomState.Room.Path)
            .Select(message => message.Message)
            .ToArray();

        return roomState.Messages
            .Concat(roomPendingMessages)
            .OrderBy(message => message.SentAtUtc)
            .Select(message => _messageVisibilityFilter.Apply(message))
            .ToArray();
    }

    private IReadOnlyList<RenderedMessage> TakeMessagesThatFitViewport(IReadOnlyList<RenderedMessage> messages)
    {
        int availableRows = GetAvailableMessageRows();
        if (messages.Count == 0 || availableRows <= 0)
        {
            return [];
        }

        int contentWidth = GetMessageContentWidth();
        List<RenderedMessage> visibleMessages = [];
        int usedRows = 0;

        for (int index = messages.Count - 1; index >= 0; index--)
        {
            RenderedMessage message = messages[index];
            int messageRows = EstimateMessageRowCount(message, contentWidth);

            if (visibleMessages.Count > 0 && usedRows + messageRows > availableRows)
            {
                break;
            }

            visibleMessages.Add(message);
            usedRows += messageRows;

            if (usedRows >= availableRows)
            {
                break;
            }
        }

        visibleMessages.Reverse();
        return visibleMessages;
    }

    private int GetAvailableMessageRows()
    {
        int consoleHeight = _console.Profile.Height;
        int reservedRows = HeaderPanelHeight + ComposePanelHeight;
        int availableRows = consoleHeight - reservedRows - 2;
        return Math.Max(1, availableRows);
    }

    private int GetMessageContentWidth()
    {
        int consoleWidth = _console.Profile.Width;
        int mainPanelWidth = consoleWidth - RoomsPanelWidth - 1;
        int contentWidth = mainPanelWidth - PanelBorderPaddingWidth - MessageBodyIndentWidth;
        return Math.Max(12, contentWidth);
    }

    private static int EstimateMessageRowCount(RenderedMessage message, int contentWidth)
    {
        if (message.IsHidden)
        {
            return Math.Max(1, EstimateWrappedLineCount(message.Body, contentWidth));
        }

        int metadataRows = EstimateWrappedLineCount(BuildMetadataText(message), contentWidth);
        int bodyRows = EstimateWrappedLineCount(message.Body, contentWidth);
        return Math.Max(1, metadataRows) + Math.Max(1, bodyRows);
    }

    private static int EstimateWrappedLineCount(string text, int contentWidth)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 1;
        }

        int lineCount = 0;

        foreach (string line in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            int lineLength = Math.Max(1, line.Length);
            lineCount += (int)Math.Ceiling(lineLength / (double)contentWidth);
        }

        return Math.Max(1, lineCount);
    }

    private static string BuildMetadataText(RenderedMessage message)
    {
        string timestamp = FormatMessageTimestamp(message.SentAtUtc);
        string statusSuffix = message.DeliveryFailed
            ? " (failed)"
            : message.IsPending
                ? " (sending...)"
                : string.Empty;

        if (string.IsNullOrWhiteSpace(message.SenderKeyHash))
        {
            return $"{timestamp} {message.Sender}{statusSuffix}";
        }

        return $"{timestamp} {message.Sender} {message.SenderKeyHash}{statusSuffix}";
    }

    private TimeSpan GetServerDiscoveryCacheDuration()
    {
        return TimeSpan.FromSeconds(Math.Max(_config.Core.RefreshIntervalSeconds * 3, s_minServerDiscoveryCacheDuration.TotalSeconds));
    }

    private TerminalSize GetTerminalSize()
    {
        return new TerminalSize(_console.Profile.Width, _console.Profile.Height);
    }
}

internal sealed record TerminalSize
{
    public TerminalSize(int width, int height)
    {
        Width = width;
        Height = height;
    }

    public int Width { get; init; }

    public int Height { get; init; }
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
        string status,
        long lastMessageId)
    {
        Room = room;
        Messages = messages;
        Error = error;
        Status = status;
        LastMessageId = lastMessageId;
    }

    public RoomLeafNode Room { get; init; }

    public IReadOnlyList<RenderedMessage> Messages { get; init; }

    public string? Error { get; init; }

    public string Status { get; init; }

    public long LastMessageId { get; init; }

    public static RoomViewState Empty(RoomLeafNode room) => new(room, [], null, "Ready.", 0);
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

internal sealed record PendingLocalMessage
{
    public PendingLocalMessage(string roomPath, string localId, RenderedMessage message)
    {
        RoomPath = roomPath;
        LocalId = localId;
        Message = message;
    }

    public string RoomPath { get; init; }

    public string LocalId { get; init; }

    public RenderedMessage Message { get; set; }
}

internal sealed record PendingSendOperation
{
    public PendingSendOperation(string localId, RoomLeafNode room, Task<SendCompletionResult> completion)
    {
        LocalId = localId;
        Room = room;
        Completion = completion;
    }

    public string LocalId { get; init; }

    public RoomLeafNode Room { get; init; }

    public Task<SendCompletionResult> Completion { get; init; }
}

internal sealed record SendStartResult
{
    public SendStartResult(RoomViewState roomState, DateTimeOffset nextRefreshAt)
    {
        RoomState = roomState;
        NextRefreshAt = nextRefreshAt;
    }

    public RoomViewState RoomState { get; init; }

    public DateTimeOffset NextRefreshAt { get; init; }
}

internal sealed record SendProcessingResult
{
    public SendProcessingResult(RoomViewState roomState, bool handled, bool shouldRefreshImmediately, bool shouldRefreshServer)
    {
        RoomState = roomState;
        Handled = handled;
        ShouldRefreshImmediately = shouldRefreshImmediately;
        ShouldRefreshServer = shouldRefreshServer;
    }

    public RoomViewState RoomState { get; init; }

    public bool Handled { get; init; }

    public bool ShouldRefreshImmediately { get; init; }

    public bool ShouldRefreshServer { get; init; }
}

internal sealed record SendCompletionResult
{
    private SendCompletionResult(bool succeeded, string? errorMessage)
    {
        Succeeded = succeeded;
        ErrorMessage = errorMessage;
    }

    public bool Succeeded { get; init; }

    public string? ErrorMessage { get; init; }

    public static SendCompletionResult Success()
    {
        return new SendCompletionResult(true, null);
    }

    public static SendCompletionResult Failure(string errorMessage)
    {
        return new SendCompletionResult(false, errorMessage);
    }
}
