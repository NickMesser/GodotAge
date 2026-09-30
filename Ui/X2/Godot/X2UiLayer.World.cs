#nullable enable
using AAEmu.GodotViewer.Ui.X2.Online;
using AAEmu.GodotViewer.Ui.X2.Scripting;
using AAEmu.GodotViewer.Ui.X2.Scripting.World;
using AAEmu.GodotViewer.Data;
using AAEmu.GodotViewer.Lua;
using AAEmu.GodotViewer.Ui;
using System.Globalization;

namespace AAEmu.GodotViewer.Ui.X2;

// In-world half: the live-state bridge and the X2 API families installed on the stage-6 Lua state.
public partial class X2UiLayer
{
    /// <summary>Live game state for the in-game UI (null offline / before world entry).</summary>
    public X2WorldBridge Bridge { get; private set; }
    private X2CombatBinding? _combatBinding;
    private readonly bool _startInWorld;
    private readonly string _gameDatabase;

    /// <summary>The in-game UI for a character that is already in the world (no login screens).</summary>
    public static X2UiLayer ForWorld(ILoginBackend backend, string? gameDatabase = null)
        => new(backend, gameDatabase: gameDatabase, startInWorld: true);

    /// <summary>
    /// Extra installers run on every in-game Lua state after the built-in families (the host can add its own,
    /// e.g. state modules that are not part of the UI).
    /// </summary>
    public event Action<X2LuaHost, X2GameContext> InstallingWorldApis;

    /// <summary>The live game session (combat state, targeting, skills) once the world viewer has created it.</summary>
    public Client.OnlineSession? LiveSession { get; private set; }
    private X2ProtocolBinding? _protocolBinding;
    private Action<int>? _questStageBuiltHandler;
    private X2ProtocolSocialData? _protocolSocialData;
    private X2ProtocolItemsData? _protocolItemsData;
    private X2ProtocolEconomyData? _protocolEconomyData;
    private X2ProtocolWorldData? _protocolWorldData;
    private X2ProtocolQuestData? _protocolQuestData;
    private X2RankRuntime? _rankRuntime;
    private GameData? _protocolGameData;
    private Client.ClientActions? _protocolActions;
    private readonly HashSet<ulong> _announcedChatChannels = [];
    private readonly List<PendingLocalSay> _pendingLocalSays = [];
    private bool _pendingZoneTrade;

    private sealed record PendingLocalSay(uint SenderUnitId, short ChatType, string Message, long SentAtMs);

    /// <summary>Raised when <see cref="AttachSession"/> hands the UI the live session.</summary>
    public event Action<Client.OnlineSession>? SessionAttached;

    /// <summary>Raised when the player accepts a non-empty native chat input line.</summary>
    public event Action<int, string>? ChatSubmitted;

    public void AttachSession(Client.OnlineSession session)
    {
        if (_questStageBuiltHandler is not null)
        {
            _session.StageBuilt -= _questStageBuiltHandler;
            _questStageBuiltHandler = null;
        }
        if (LiveSession is { } previousChat)
        {
            previousChat.ChatChannelJoined -= OnChatChannelJoined;
            previousChat.ChatChannelLeft -= OnChatChannelLeft;
            previousChat.RawPacket -= OnRankPacket;
        }
        _protocolBinding?.Dispose();
        _protocolGameData?.Dispose();
        if (LiveSession is { } previous)
        {
            previous.NpcDialogue -= OnNpcDialogue;
            previous.NpcInteractionEnded -= OnNpcInteractionEnded;
            previous.NpcInteractionStarted -= OnNpcInteractionStarted;
            previous.DoodadInteractionRequested -= OnDoodadInteractionRequested;
        }
        LiveSession = session;
        _announcedChatChannels.Clear();
        _pendingLocalSays.Clear();
        _pendingZoneTrade = false;
        _protocolGameData = new GameData(_gameDatabase);
        _protocolActions = new Client.ClientActions(session.Client, _protocolGameData);
        var questUiPrepared = false;
        bool WorldQuestUiReady() => _session.Engine.Stage == X2Engine.StageWorld &&
            _session.Root?.HasEventListeners("QUEST_NOTIFIER_START") == true;
        void PrepareQuestUi()
        {
            // Interactive login attaches the session before its queued world stage is built.
            // A snapshot in that interval must not initialize the old login-stage UI data.
            if (questUiPrepared || !WorldQuestUiReady()) return;
            questUiPrepared = true;
            // Keep the native main-quest decal while making an empty pre-snapshot
            // list seedable again. The notifier reserves decal slot zero for it.
            var notifier = _systemData?.GetUiData("questNotifier", "default") as LuaTable ?? new LuaTable();
            if (notifier.GetValueOrDefault("questList") is not LuaTable savedQuests || savedQuests.Count == 0)
                notifier.Remove("questList");
            if (notifier.GetValueOrDefault("decalStates") is not LuaTable decalStates)
            {
                decalStates = new LuaTable();
                notifier["decalStates"] = decalStates;
            }
            for (var index = 0; index < 11; index++)
                if (!decalStates.ContainsKey((double)index)) decalStates[(double)index] = 0d;
            if (_protocolQuestData is { } questApi &&
                questApi.TryQuery("X2Quest", "GetMainQuestType", [1d], out var mainType) &&
                mainType is double mainQuestType && mainQuestType > 0)
                decalStates[0d] = mainQuestType;
            _systemData?.SetUiData("questNotifier", "default", notifier);
            _systemData?.SetUiData("questContext", "default", null);
        }
        if (!WorldQuestUiReady())
        {
            _questStageBuiltHandler = stage =>
            {
                if (stage != X2Engine.StageWorld || !ReferenceEquals(LiveSession, session)) return;
                _session.StageBuilt -= _questStageBuiltHandler;
                _questStageBuiltHandler = null;
                if (questUiPrepared || session.QuestState.ActiveQuests.Count == 0) return;
                PrepareQuestUi();
                _session.Root?.DispatchEvent("QUEST_NOTIFIER_START");
                _session.Root?.DispatchEvent("LEFT_LOADING");
            };
            _session.StageBuilt += _questStageBuiltHandler;
        }
        _protocolBinding = new X2ProtocolBinding(session, Bridge?.Events ?? new RootEvents(() => _session.Root),
            Bridge, _protocolSocialData, _protocolItemsData, prepareInitialQuestSnapshot: PrepareQuestUi,
            economy: _protocolEconomyData);
        ChatSubmitted -= SendChatLine;
        ChatSubmitted += SendChatLine;
        session.InventoryChanged += _ => { RefreshItemSlots(); _combatBinding?.RefreshInventorySlots(); };
        session.AssignmentUpdated += _ =>
        {
            if (!ReferenceEquals(LiveSession, session)) return;
            _session.Root?.DispatchEvent("ACHIEVEMENT_UPDATE");
            _session.Root?.DispatchEvent("UPDATE_TODAY_ASSIGNMENT");
        };
        session.NpcDialogue += OnNpcDialogue;
        session.NpcInteractionEnded += OnNpcInteractionEnded;
        session.NpcInteractionStarted += OnNpcInteractionStarted;
        session.DoodadInteractionRequested += OnDoodadInteractionRequested;
        // local emote text ("You bow.") goes into the chat transcript like the client's say-channel emote lines
        session.EmoteTextGenerated += text => AddChatMessage(0, null, text);
        _combatBinding?.Attach(session);
        if (Backend is Client.NetLoginBackend net && net.Entered is { } entered)
        {
            var lobby = X2NetDetails.Read(net, entered.CharacterId, _texts);
            _combatBinding?.SeedActiveAbilities(lobby?.Abilities);
            _lobbyLabor = lobby?.LaborPower ?? 0;
            _lobbyLocalLabor = lobby?.LocalLaborPower ?? 0;
            // InventoryState only accumulates the ChangeMoney deltas of item tasks; the balance at world entry is the
            // character record's money (the lobby/state record read by X2NetDetails)
            _entryMoney = lobby?.Money;
            Emit($"[x2] entry money from the character record: {(lobby == null ? "unknown (no lobby record)" : lobby.Money.ToString(CultureInfo.InvariantCulture))}");
            if (lobby != null) session.InventoryState.SeedMoney(lobby.Money);
        }
        // the HUD read exp, labor and points at world entry, before the session and its first packets existed:
        // refresh it once they have arrived, as the client's end of loading does
        GetTree().CreateTimer(3.0).Timeout += () =>
        {
            if (!ReferenceEquals(LiveSession, session)) return;
            // A snapshot normally seeds the notifier immediately. Handle one that arrived
            // before the protocol binding attached as well, in the same order as the snapshot.
            if (!questUiPrepared && session.QuestState.ActiveQuests.Count > 0)
            {
                PrepareQuestUi();
                if (questUiPrepared) _session.Root?.DispatchEvent("QUEST_NOTIFIER_START");
            }
            // while the loading screen still covers the world, LoadingTick sends LEFT_LOADING when it closes
            if (WorldQuestUiReady()) _session.Root?.DispatchEvent("LEFT_LOADING");
            // the session's protocol state may have been (re)initialised after attach: seed the entry balance again (applied once)
            if (_entryMoney is { } entryMoney) session.InventoryState.SeedMoney(entryMoney);
            // the labor bar text is only rebuilt on LABORPOWER_CHANGED; the lobby record's labor is known by now
            _session.Root?.DispatchEvent("LABORPOWER_CHANGED");
        };
        _protocolWorldData?.Attach(session);
        session.ChatChannelJoined += OnChatChannelJoined;
        session.ChatChannelLeft += OnChatChannelLeft;
        session.RawPacket += OnRankPacket;
        // world entry can announce channels before the HUD attaches to the session
        foreach (var channel in session.SocialState.ChatChannels.Values)
            AnnounceChatChannel(channel);
        FlushZoneTrade();
        SessionAttached?.Invoke(session);
    }

    /// <summary>Bag, bank and equipment slots show the live item (icon, stack count); the scripts establish them 0-based.</summary>
    private void BindItemSlot(SlotWidget slot)
    {
        if (_protocolItemsData is not { } items || !int.TryParse(slot.SlotType, out var type)) return;
        var index = (int)slot.SlotIndex + 1;
        Scripting.Api.X2InventoryItem? item;
        switch (type)
        {
            case 1: item = items.GetEquippedItem("player", index); break;               // ISLOT_EQUIPMENT
            case 2: item = items.GetContainerItem(Scripting.Api.X2ContainerKind.Bag, index); break;   // ISLOT_BAG
            case 3: item = items.GetContainerItem(Scripting.Api.X2ContainerKind.Bank, index); break;  // ISLOT_BANK
            default: return;
        }
        slot.SetIcon(item?.Icon);
        slot.SetCount(item?.Count ?? 0);
        // The Lua bag slot uses 1/1 as an icon sentinel when ItemDurability is nil.
        // Hover text must use the real instance projection instead of that slot copy.
        if (slot is NativeSlotWidget native)
            native.SetTooltip(item is null ? null : Scripting.Api.X2ItemsApi.CompleteItemTable(item));
    }

    private void RefreshItemSlots()
    {
        if (_session.Root is not { } root) return;
        foreach (var slot in root.AllWidgets().OfType<SlotWidget>()) BindItemSlot(slot);
    }

    private void DisposeProtocolBinding()
    {
        if (_questStageBuiltHandler is not null)
        {
            _session.StageBuilt -= _questStageBuiltHandler;
            _questStageBuiltHandler = null;
        }
        if (LiveSession is { } chatSession)
        {
            chatSession.ChatChannelJoined -= OnChatChannelJoined;
            chatSession.ChatChannelLeft -= OnChatChannelLeft;
            chatSession.RawPacket -= OnRankPacket;
        }
        _announcedChatChannels.Clear();
        _pendingZoneTrade = false;
        _protocolWorldData?.Dispose();
        _protocolWorldData = null;
        _protocolBinding?.Dispose();
        _protocolBinding = null;
        _protocolGameData?.Dispose();
        _protocolGameData = null;
        _protocolActions = null;
        if (LiveSession is { } session)
        {
            session.NpcDialogue -= OnNpcDialogue;
            session.NpcInteractionEnded -= OnNpcInteractionEnded;
            session.DoodadInteractionRequested -= OnDoodadInteractionRequested;
        }
        ChatSubmitted -= SendChatLine;
    }

    /// <summary>
    /// A line accepted in the chat input goes to the server: /w name text is a whisper, /s /y /p /r /g /trade pick the
    /// channel, anything else uses the channel selected in the chat window.
    /// </summary>
    private void SendChatLine(int chatType, string line)
    {
        var actions = _protocolActions;
        if (actions is null || line.Length == 0) return;
        Emit($"[x2] chat line ({chatType}): {line}");
        if (line.StartsWith('/'))
        {
            var firstSpace = line.IndexOf(' ');
            var command = (firstSpace < 0 ? line : line[..firstSpace]).ToLowerInvariant();
            var body = firstSpace < 0 ? "" : line[(firstSpace + 1)..].Trim();
            if (command is "/w" or "/whisper")
            {
                var separator = body.IndexOf(' ');
                if (separator <= 0 || separator == body.Length - 1) return;
                actions.SendWhisper(body[..separator], body[(separator + 1)..]);
                return;
            }
            short? channel = command switch
            {
                "/s" or "/say" => Net.SocialChatTypes.Say,
                "/y" or "/shout" => Net.SocialChatTypes.Shout,
                "/p" or "/party" => Net.SocialChatTypes.Party,
                "/r" or "/raid" => Net.SocialChatTypes.Raid,
                "/g" or "/guild" => Net.SocialChatTypes.Guild,
                "/f" or "/faction" => Net.SocialChatTypes.Ally,
                "/trade" => Net.SocialChatTypes.Trade,
                _ => null,
            };
            if (channel.HasValue)
            {
                if (body.Length > 0)
                {
                    if (channel.Value == Net.SocialChatTypes.Say) EchoLocalSay(body);
                    actions.SendChat(channel.Value, body);
                }
                return;
            }
            // the client's emote aliases (/bow, /dance); anything else goes to the server as typed (its slash commands)
            if (LiveSession?.TrySendEmoteCommand(line, Bridge?.TargetId ?? 0) != true) actions.SendChat(Net.SocialChatTypes.Say, line);
            return;
        }
        var selectedChannel = chatType > 0 ? (short)chatType : Net.SocialChatTypes.Say;
        if (selectedChannel == Net.SocialChatTypes.Say) EchoLocalSay(line);
        actions.SendChat(selectedChannel, line);
    }

    /// <summary>Tells the UI the current target changed (from the targeting controller); 0 clears it.</summary>
    public void SetTarget(uint unitId) => Bridge?.SetTarget(unitId);

    /// <summary>Uses the learned skill assigned to a one-based main action-bar slot.</summary>
    public bool UseActionSlot(int slot) => _combatBinding?.UseActionSlot(slot) ?? false;

    /// <summary>The settings the in-game options window edits; the host may set its own store before world entry.</summary>
    public static Settings.OptionStore? Options { get; set; }
    /// <summary>Key bindings shown on action bar slots and in the key binding window; the host may set its own.</summary>
    public static Settings.KeyBindings? Bindings { get; set; }

    private static Settings.OptionStore LoadOptions()
    {
        var store = new Settings.OptionStore();
        store.Load();
        return store;
    }

    /// <summary>Routes an incoming server line through each native window's selected-tab filter and colour table.</summary>
    private void AppendChat(Net.ChatEvent chat)
    {
        if (ConsumeLocalSayEcho(chat)) return;
        AddChatMessage(chat.ChatType, chat.SenderName, chat.Message);
    }

    private void EchoLocalSay(string message)
    {
        if (LiveSession is not { } session || _session.Root is null) return;
        var now = Environment.TickCount64;
        RemoveExpiredLocalSays(now);
        _pendingLocalSays.Add(new PendingLocalSay(session.Entered.UnitId, Net.SocialChatTypes.Say, message, now));
        AddChatMessage(Net.SocialChatTypes.Say, session.Entered.Name, message);
        session.Overhead?.ShowChat(session.Entered.UnitId, session.Entered.Name, message, Client.OverheadChatKind.Say);
    }

    private bool ConsumeLocalSayEcho(Net.ChatEvent chat)
    {
        var now = Environment.TickCount64;
        RemoveExpiredLocalSays(now);
        for (var i = 0; i < _pendingLocalSays.Count; i++)
        {
            var pending = _pendingLocalSays[i];
            if (pending.SenderUnitId != chat.SenderUnitId || pending.ChatType != chat.ChatType ||
                !string.Equals(pending.Message, chat.Message, StringComparison.Ordinal)) continue;
            _pendingLocalSays.RemoveAt(i);
            return true;
        }
        return false;
    }

    private void RemoveExpiredLocalSays(long now)
        => _pendingLocalSays.RemoveAll(pending => now - pending.SentAtMs > 5000);

    /// <summary>Host entry point for an incoming chat channel, sender and message.</summary>
    public void AddChatMessage(int chatType, string? sender, string? text)
    {
        if (_session.Root is not { } root) return;
        foreach (var window in root.AllWidgets().OfType<ChatWindowWidget>())
            window.AddChatMessage(chatType, sender, text);
        root.DispatchEvent("CHAT_MESSAGE", (double)chatType, 0d, sender ?? "", text ?? "",
            new AAEmu.GodotViewer.Lua.LuaTable());
    }

    private void OnChatChannelJoined(Net.ChatChannelJoinedEvent joined) => AnnounceChatChannel(joined.Channel);

    private void OnChatChannelLeft(Net.ChatChannelLeftEvent left)
        => _announcedChatChannels.Remove(left.Channel.Handle);

    private void AnnounceChatChannel(Net.SocialChannel channel)
    {
        if (_session.Root is not { } root || !_announcedChatChannels.Add(channel.Handle)) return;

        // SCJoinedChatChannel contains a channel descriptor and an optional user-channel name.
        // The zone channel joins Shout, Party Search and Trade together on the client.
        if (channel.Type == 1)
        {
            var zone = LiveSession is { } session
                ? _protocolGameData?.GetZone((int)session.Entered.ZoneId)
                : null;
            var zoneSuffix = zone is { DisplayText: { Length: > 0 } } && zone.GroupId == channel.SubType
                ? zone.DisplayText : "";
            AddChannelInfo(root, 1, "Shout", zoneSuffix);
            AddChannelInfo(root, 3, "Party Search", zoneSuffix);
            // The client announces Trade after the following Nation channel on world entry.
            _pendingZoneTrade = true;
            return;
        }
        if (_pendingZoneTrade && channel.Type is not 6) FlushZoneTrade();
        if (channel.Type == 18) return; // The server's CSM channel has no entry line.

        // The retail client formats these as CMF_CHANNEL_INFO alarms.
        var name = channel.Type switch
        {
            2 => "Trade",
            3 => "Party Search",
            4 => "Party",
            5 => "Raid",
            6 => "Faction",
            7 => "Guild",
            9 => "Family",
            10 => "Commander",
            11 => "Trial",
            14 => "Faction",
            _ => string.IsNullOrWhiteSpace(channel.Name) ? $"Channel {channel.Type}" : channel.Name,
        };
        var suffix = channel.Type is 6 or 11 or 14 && channel.FactionId != 0
            ? _protocolGameData?.GetText("system_factions", "name", channel.FactionId) ?? ""
            : "";
        AddChannelInfo(root, channel.Type, name, suffix);
        if (channel.Type == 6) FlushZoneTrade();
    }

    private void FlushZoneTrade()
    {
        if (!_pendingZoneTrade || _session.Root is not { } root) return;
        _pendingZoneTrade = false;
        AddChannelInfo(root, 2, "Trade", "");
    }

    private void AddChannelInfo(UiRoot root, int type, string name, string suffix)
    {
        var template = _texts.Get(88, "enter_channel") ?? "Entering Chat: $1.$2";
        var label = name + "." + (suffix.Length == 0 ? "" : $" {suffix}");
        var message = template.Replace("$1", type.ToString(CultureInfo.InvariantCulture))
            .Replace("$2", label);
        foreach (var window in root.AllWidgets().OfType<ChatWindowWidget>())
            window.AddFilteredMessage(13, null, message); // CMF_CHANNEL_INFO
    }

    private void OnRankPacket(Net.RawPacketEvent packet)
    {
        try
        {
            _rankRuntime?.Apply(packet.Opcode, packet.Body);
        }
        catch (Exception error)
        {
            Emit($"[x2] rank packet 0x{packet.Opcode:X3}: {error.Message}");
        }
    }

    private void StartWorldBridge(CharacterInfo? offlineCharacter = null)
    {
        _protocolWorldData?.Dispose();
        _protocolWorldData = null;
        _combatBinding?.Dispose();
        _combatBinding = null;
        _rankRuntime = null;
        Bridge?.Dispose();
        IX2UnitData units = NullUnitData.Instance;
        Scripting.Api.IX2UnitData apiUnits = Scripting.Api.NullUnitData.Instance;
        if (Backend is Client.NetLoginBackend net && net.Client != null && net.Entered != null)
        {
            var events = new RootEvents(() => _session.Root);
            Bridge = new X2WorldBridge(net.Client, net.Entered, events, Emit);
            _rankRuntime = new X2RankRuntime((opcode, body) => net.Client.SendGame(opcode, body),
                (name, args) => _session.Root?.DispatchEvent(name, args));
            units = Bridge;
            apiUnits = _combatBinding = new X2CombatBinding(Bridge, events, _gameDatabase);
            if (LiveSession != null) _combatBinding.Attach(LiveSession);
            Bridge.Applied += e =>
            {
                if (e is Net.ChatEvent chat) AppendChat(chat);
                else if (e is Net.ServerErrorEvent error) ShowServerError(error);
            };
            NativeSlotWidget.HotkeyResolver = (type, index) => Bindings == null ? null : Online.X2KeyBindingText.ForSlot(Bindings, type, index);
            X2NativeWidgetTypes.MapProvider = new Online.X2BridgeMapProvider(Bridge, net.Entered.ZoneId, net.Entered.Yaw,
                () => LiveSession?.SocialState.TeamMembers.Values.Select(m => m.ObjectId).Where(id => id != 0).ToHashSet()
                    ?? new HashSet<uint>(),
                () => LiveSession?.QuestState, QuestOfferContext);
        }
        else if (offlineCharacter is { } character)
        {
            // MockLoginBackend enters the same stage as the live path. Seed its player before loading world scripts,
            // which query UnitLevel/UnitHeirLevel during their initial ChangedLevel handlers.
            units = new OfflineWorldUnits(character);
        }
        var context = new X2GameContext(new RootEvents(() => _session.Root), units, _texts, Emit);
        // static game data (item/quest names, icons) comes from the client database until live state modules plug in
        var family = new X2FamilyData
        {
            Unit = apiUnits,
            Rank = _rankRuntime,
            // installed before the OnlineSession exists: each family reads it through a lazy accessor
            Items = _protocolItemsData = new X2ProtocolItemsData(_gameDatabase, () => LiveSession,
                () => _protocolActions, id => Bridge?.Get(id)?.TemplateId),
            Quest = _protocolQuestData = new X2ProtocolQuestData(_gameDatabase, () => LiveSession,
                () => _protocolActions, fireEvent: (name, args) => _session.Root?.DispatchEvent(name, args),
                world: () => Bridge),
            Social = _protocolSocialData = new X2ProtocolSocialData(() => LiveSession, () => _protocolActions,
                () => _protocolItemsData?.PickedCursor ?? new Scripting.Api.X2CursorState(),
                () => _protocolItemsData?.ClearPickedCursor(),
                (name, args) => _session.Root?.DispatchEvent(name, args), _protocolGameData),
            Economy = _protocolEconomyData = new X2ProtocolEconomyData(() => LiveSession, () => _protocolActions,
                () => _craftDoodadObjectId != 0 ? _craftDoodadObjectId : Bridge?.TargetId ?? 0,
                () => _craftDoodadObjectId != 0 ? _craftDoodadTemplateId : Bridge?.Get(Bridge.TargetId)?.TemplateId ?? 0,
                id => _protocolItemsData?.GetItemInfo(id),
                () => _protocolItemsData?.PickedCursor,
                (name, args) => _session.Root?.DispatchEvent(name, args)),
            World = Bridge is null ? null : _protocolWorldData = new X2ProtocolWorldData(
                _gameDatabase, checked((int)((Client.NetLoginBackend)Backend).Entered!.ZoneId), context.Events,
                () => _protocolActions),
            GameDatabase = _gameDatabase,
            System = _systemData = new Online.X2OptionSystemData(Options ??= LoadOptions(), Bindings ??= new Settings.KeyBindings())
            {
                PickedCursorProvider = () => _protocolItemsData?.PickedCursor ?? new Scripting.Api.X2CursorState(),
                ClearPickedCursorAction = () => _protocolItemsData?.ClearPickedCursor(),
            },
        };
        _protocolQuestData.DirectingModeChanged += SetQuestDirectingMode;
        _protocolEconomyData?.SetCraftLabor(() => Labor() + LocalLabor());
        _protocolEconomyData?.SetCraftTargetTemplate(() =>
            _craftDoodadObjectId != 0 ? _craftDoodadTemplateId : Bridge?.Get(Bridge.TargetId)?.TemplateId ?? 0);
        _session.InstallWorldApis = host =>
        {
            if (_session.Root is { } root && _combatBinding != null)
            {
                root.SlotEstablished = slot => { _combatBinding.BindSlot(slot); BindItemSlot(slot); };
                root.SlotInteraction = (slot, interaction) =>
                {
                    if (!int.TryParse(slot.SlotType, out var slotType)) return false;
                    return _protocolItemsData?.HandleNativeSlotInteraction(slotType, (int)slot.SlotIndex, interaction) == true;
                };
                root.SlotActivated = slot =>
                {
                    if (slot.SlotType is "254" or "action") _combatBinding.UseActionSlot((int)slot.SlotIndex);
                    else if (slot.SlotType == "246") _combatBinding.UseModeActionSlot((int)slot.SlotIndex);
                };
                _combatBinding.UiText = (category, key) => _texts?.Get(category, key);
                root.SlotDropped = (source, destination) => _combatBinding.DropSlot(source, destination);
            }
            X2WorldApis.Install(host, context, family);
            host.Define("X2Interaction", "CancelNPCInteraction", _ =>
            {
                LeaveNpcInteraction();
                return null;
            });
            InstallProtocolCurrencyApis(host);
            // center_message_manager.lua shows the zone banner on LEFT_LOADING for X2Unit:GetCurrentZoneGroup(), looked up
            // with X2Map:GetZoneStateInfoByZoneId: both use the session's current zone (0 made the banner never appear)
            host.Define("X2Unit", "GetCurrentZoneGroup", _ => (double)(_protocolWorldData?.World is { } w
                ? (w.ZoneGroupId != 0 ? w.ZoneGroupId : w.ZoneId) : 0));
            host.Root.ChatSubmit = (chatType, text) => ChatSubmitted?.Invoke(chatType, text);
            InstallingWorldApis?.Invoke(host, context);
        };
    }

    private sealed class OfflineWorldUnits(CharacterInfo character) : IX2UnitData
    {
        private const uint OfflinePlayerId = 1;
        private readonly X2UnitInfo _player = new()
        {
            Id = OfflinePlayerId,
            Name = character.Name,
            Type = "character",
            Level = character.Level,
            HeirLevel = 0,
            Race = character.Race.ToLowerInvariant(),
            Gender = character.Gender.ToLowerInvariant(),
            CharacterId = character.Id,
            ModelId = character.ModelId is >= 0 and <= uint.MaxValue ? (uint)character.ModelId : 0,
            FactionId = character.FactionId,
            FactionName = character.FactionName,
        };

        public uint? Resolve(string token) => token == "player" || token == OfflinePlayerId.ToString(CultureInfo.InvariantCulture)
            ? OfflinePlayerId : null;
        public X2UnitInfo? Get(uint unitId) => unitId == OfflinePlayerId ? _player : null;
        public IEnumerable<X2UnitInfo> All => [_player];
        public uint PlayerId => OfflinePlayerId;
        public uint TargetId => 0;
        public void RequestTarget(uint unitId) { }
    }
    /// <summary>Labor from the live inventory state, or the lobby record until the first labor packet arrives.</summary>
    private long Labor() => LiveSession?.InventoryState.Labor is > 0 and var live ? live : _lobbyLabor;

    private long _lobbyLabor, _lobbyLocalLabor;
    private long? _entryMoney;
    /// <summary>Local (character) labor: the live value once a labor packet arrived, else the lobby record's localLp.</summary>
    private long LocalLabor() => LiveSession?.InventoryState.LocalLabor is > 0 and var live ? live : _lobbyLocalLabor;
    private Online.X2OptionSystemData? _systemData;

    private void InstallProtocolCurrencyApis(X2LuaHost host)
    {
        string Amount(Func<Net.InventoryState, long> read) =>
            (LiveSession is { } session ? read(session.InventoryState) : 0L).ToString(CultureInfo.InvariantCulture);
        host.Define("X2Util", "GetMyMoneyString", _ => Amount(s => s.Money));
        host.Define("X2Util", "GetMyBankMoneyString", _ => Amount(s => s.BankMoney));
        host.Define("X2Util", "GetMyAAPointString", _ => Amount(s => s.AaPoints));
        // X2Util:HasEnoughCurrency(currency, amountStr): dialog cost modules (module.lua Satisfy → ApplyOkButtonEnablement) enable
        // OK only when the player can pay; CURRENCY_GOLD 0, HONOR 1, LIVING 2, AA_POINT 3, GOLD_WITH_AA_POINT 4
        host.Define("X2Util", "HasEnoughCurrency", a =>
        {
            var need = decimal.TryParse(a.Str(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0m;
            if (need <= 0) return true;
            if (LiveSession is not { } live) return false;
            var inv = live.InventoryState;
            decimal have = a.Int(0) switch
            {
                0 => inv.Money,
                3 => inv.AaPoints,
                4 => inv.Money + inv.AaPoints,
                _ => decimal.MaxValue, // honor / vocation / other points: not tracked here; the server validates
            };
            return have >= need;
        });
        host.Define("X2Util", "GetMyBankAAPointString", _ => Amount(s => s.BankAaPoints));
        host.Define("X2Player", "GetGlobalLaborPower", _ => (double)Labor());
        host.Define("X2Player", "GetLocalLaborPower", _ => (double)LocalLabor());
        host.Define("X2Player", "GetTotalLaborPower", _ =>
            (double)(Labor() + LocalLabor()));
        host.Define("X2Player", "GetGamePoints", _ =>
        {
            var points = LiveSession?.InventoryState.GamePoints;
            var honor = points?.GetValueOrDefault(Net.GamePointSlots.Honor) ?? 0;
            var vocation = points?.GetValueOrDefault(Net.GamePointSlots.Vocation) ?? 0;
            return new LuaTable
            {
                ["honorPoint"] = (double)honor, ["honorPointStr"] = honor.ToString(CultureInfo.InvariantCulture),
                ["livingPoint"] = (double)vocation, ["livingPointStr"] = vocation.ToString(CultureInfo.InvariantCulture),
                ["leadershipPoint"] = 0d, ["periodLeadershipPointStr"] = "0",
                ["vocationPoint"] = (double)vocation,
            };
        });
    }

    // ------------------------------------------------------------------ server errors

    private Dictionary<int, string>? _errorNames;

    /// <summary>
    /// SCErrorMsg: the client shows the error's ERROR_MSG ui_text (category 1, key = enum_error_messages name) in the system message
    /// window (center_message_manager.lua SYSMSG → AddMessageToSysMsgWindow).
    /// </summary>
    private void ShowServerError(Net.ServerErrorEvent error)
    {
        _errorNames ??= LoadErrorNames();
        var name = _errorNames.GetValueOrDefault(error.Error) ?? $"ERROR_{error.Error}";
        var text = _texts?.Get(1, name);
        Emit($"[x2] server error {error.Error} {name} (type {error.Type}, notify {error.IsNotify}): {text ?? "(no text)"}");
        if (!string.IsNullOrEmpty(text)) _session.Root?.DispatchEvent("SYSMSG", text);
    }

    private Dictionary<int, string> LoadErrorNames()
    {
        var names = new Dictionary<int, string>();
        try
        {
            using var db = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_gameDatabase};Mode=ReadOnly");
            db.Open();
            // the client database has no enum_error_messages: the names stay empty and the error shows as ERROR_<n> without text
            Data.ClientEnums.Ensure(db);
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT id, name FROM enum_error_messages";
            using var r = cmd.ExecuteReader();
            while (r.Read()) names[r.GetInt32(0)] = r.GetString(1);
        }
        catch (Exception e) { Emit($"[x2] error names: {e.Message}"); }
        return names;
    }
}
