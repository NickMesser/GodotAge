#nullable enable

using System.Globalization;
using AAEmu.GodotViewer.Client;
using AAEmu.GodotViewer.Data;
using AAEmu.GodotViewer.Lua;
using AAEmu.GodotViewer.Net;
using AAEmu.GodotViewer.Ui.X2.Scripting.Api;

namespace AAEmu.GodotViewer.Ui.X2.Online;

/// <summary>
/// Projects the main-thread protocol aggregates into the social X2 API. The accessors are intentionally lazy:
/// the X2 API family is installed before an online session exists, and its inherited Null behavior remains in
/// effect until both the relevant session and action dispatcher are available.
/// </summary>
public sealed class X2ProtocolSocialData : NullSocialData
{
    private const int MailAttachmentSlots = 10;
    private readonly Func<OnlineSession?> _session;
    private readonly Func<ClientActions?> _actions;
    private readonly Func<X2CursorState>? _cursor;
    private readonly Action? _clearCursor;
    private readonly Action<string, object?[]>? _fireEvent;
    private readonly GameData? _gameData;
    private readonly NullItemsData _staticItems = new();
    private readonly Dictionary<X2SocialMailboxKind, MailViewState> _mailViews = new();
    private byte _lastRequestedMailboxKind;
    private int _nextMailListIndex;
    private readonly Dictionary<long, HashSet<ulong>> _takenMailItems = [];
    private readonly HashSet<long> _takenMailMoney = [];
    private readonly HashSet<long> _pendingSequentialMail = [];

    private sealed class MailViewState
    {
        public bool Reading;
        public bool Writing;
        public long? CurrentId;
        public bool CurrentIsSent;
        public int ReceivedTotal = -1;
        public int PostedTotal = -1;
        public Dictionary<long, MailHeaderRecord> Received { get; } = [];
        public Dictionary<long, MailHeaderRecord> Posted { get; } = [];
        public SortedDictionary<int, long> ReceivedPositions { get; } = [];
        public SortedDictionary<int, long> PostedPositions { get; } = [];
        public MailSlot?[] ComposeSlots { get; } = new MailSlot?[MailAttachmentSlots];
        public X2SocialMailItem?[] ComposeItems { get; } = new X2SocialMailItem?[MailAttachmentSlots];
    }

    public X2ProtocolSocialData(Func<OnlineSession?> session, Func<ClientActions?> actions,
        Func<X2CursorState>? cursor = null, Action? clearCursor = null,
        Action<string, object?[]>? fireEvent = null, GameData? gameData = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _actions = actions ?? throw new ArgumentNullException(nameof(actions));
        _cursor = cursor;
        _clearCursor = clearCursor;
        _fireEvent = fireEvent;
        _gameData = gameData;
        _mailViews[X2SocialMailboxKind.Mail] = new MailViewState();
        _mailViews[X2SocialMailboxKind.GoodsMail] = new MailViewState();
    }

    public override X2TeamState Team
    {
        get
        {
            var state = _session()?.SocialState;
            if (state is null) return base.Team;
            var members = state.TeamMembers.Values.Select((member, index) => new X2TeamMember(
                    member.ObjectId, member.CharacterId, member.Name, 1, index + 1, (index / 5) + 1,
                    member.Role, member.CharacterId == state.TeamOwnerId))
                .ToArray();
            return new X2TeamState
            {
                Members = members,
                IsRaid = state.TeamIsRaid,
                MyJointOrder = 1,
                PartyVisibility = members.Select(member => member.Party).Distinct()
                    .Select(party => new X2PartyVisibility(1, party)).ToArray(),
            };
        }
    }

    public override X2FriendState FriendState
    {
        get
        {
            var state = _session()?.SocialState;
            if (state is null) return base.FriendState;
            var friends = state.Friends.Values.OrderByDescending(friend => friend.IsOnline).ThenBy(friend => friend.Name)
                .Select(friend => new X2FriendEntry(
                    friend.Name, friend.Level, [friend.Ability1, friend.Ability2, friend.Ability3],
                    [friend.ZoneId.ToString(CultureInfo.InvariantCulture), friend.WorldX.ToString(CultureInfo.InvariantCulture),
                        friend.WorldY.ToString(CultureInfo.InvariantCulture), friend.WorldZ.ToString(CultureInfo.InvariantCulture)],
                    friend.Race.ToString(CultureInfo.InvariantCulture), friend.IsOnline, friend.InParty,
                    friend.HeirLevel, 0, friend.IsOnline ? null : Elapsed(friend.LastWorldLeaveTime)))
                .ToArray();
            var blocked = state.BlockedUsers.Values.OrderBy(user => user.Name)
                .Select(user => new X2BlockedUser(user.Name, user.Name, user.WorldId.ToString(CultureInfo.InvariantCulture)))
                .ToArray();
            // Pending requests are not retained by SocialState. They remain empty until the aggregate exposes them.
            return new X2FriendState(friends, [], [], blocked, IsLoaded: true);
        }
    }

    public override X2ExpeditionState ExpeditionState
    {
        get
        {
            var session = _session();
            var state = session?.SocialState;
            if (session is null || state is null) return base.ExpeditionState;
            state.Expeditions.TryGetValue(state.CurrentExpeditionId, out var descriptor);
            var info = descriptor is null ? null : new X2ExpeditionInfo
            {
                Id = descriptor.Id,
                Name = descriptor.Name,
                OwnerName = descriptor.OwnerName,
                IsOwner = descriptor.OwnerId != 0 && descriptor.OwnerId == session.Entered.CharacterId,
            };
            var members = state.ExpeditionMembers.Values.OrderBy(member => member.Name)
                .Select(member => new X2ExpeditionMember(
                    member.Name, member.Level, [member.Ability1, member.Ability2, member.Ability3], member.Role,
                    member.IsOnline ? new X2SocialElapsedTime() : Elapsed(member.LastWorldLeaveTime), member.Memo,
                    member.IsOnline, member.InParty, member.ContributionPoint, member.HeirLevel,
                    member.WeeklyContributionPoint))
                .ToArray();
            return new X2ExpeditionState(info, members, IsLoaded: true);
        }
    }

    /// <summary>Builds the exact positional rows consumed by FRIENDLIST_INFO.</summary>
    public LuaTable BuildFriendListRows(bool includeOffline = true)
    {
        var result = new LuaTable();
        var index = 1d;
        foreach (var friend in FriendState.Friends.Where(friend => includeOffline || friend.Online))
        {
            var abilities = LuaArray(friend.Abilities.Select(value => (object?)(double)value));
            object position = friend.Online
                ? LuaArray(friend.Position.Cast<object?>())
                : ElapsedTable(friend.LastSeen ?? new X2SocialElapsedTime());
            result[index++] = LuaArray([
                friend.Name, (double)friend.Level, abilities, position, friend.Race, friend.Online,
                friend.InParty, (double)friend.HeirLevel, (double)friend.FactionId, false,
            ]);
        }
        return result;
    }

    public override IReadOnlyList<X2ChatChannel> ChatChannels
    {
        get
        {
            var state = _session()?.SocialState;
            if (state is null || state.ChatChannels.Count == 0) return base.ChatChannels;
            var channels = base.ChatChannels.ToDictionary(channel => channel.ChatType);
            foreach (var channel in state.ChatChannels.Values)
                channels[channel.Type] = new X2ChatChannel(channel.Type,
                    string.IsNullOrWhiteSpace(channel.Name) ? $"Channel {channel.Type}" : channel.Name);
            return channels.Values.OrderBy(channel => channel.ChatType).ToArray();
        }
    }

    public override void CreateUserChatChannel(string channel, string password)
        => LiveActions()?.JoinChatChannel(channel, password, create: true);

    public override void JoinUserChatChannel(string channel, string password)
        => LiveActions()?.JoinChatChannel(channel, password);

    public override void LeaveUserChatChannel(string channel)
    {
        var match = _session()?.SocialState.ChatChannels.Values.FirstOrDefault(value =>
            string.Equals(value.Name, channel, StringComparison.OrdinalIgnoreCase));
        if (match is not null) LiveActions()?.LeaveChatChannel(match.Handle);
    }

    public override void ExecuteTeamCommand(string command, IReadOnlyList<object?> arguments)
    {
        var actions = LiveActions();
        var state = _session()?.SocialState;
        if (actions is null || state is null) return;
        switch (command)
        {
            case "InviteToTeam" when Text(arguments, 0) is { Length: > 0 } name:
                actions.InviteParty(name, state.TeamId);
                break;
            case "LeaveTeam":
            case "DismissTeam":
                if (state.TeamId != 0) actions.LeaveParty(state.TeamId);
                break;
            case "KickTeamMemberByName" when FindTeamMember(state, Text(arguments, 0)) is { } kickedByName:
                actions.KickPartyMember(state.TeamId, kickedByName.CharacterId);
                break;
            case "KickTeamMember" when FindTeamMember(state, arguments.Count > 0 ? arguments[0] : null) is { } kicked:
                actions.KickPartyMember(state.TeamId, kicked.CharacterId);
                break;
            case "MakeTeamOwner" when FindTeamMember(state, arguments.Count > 0 ? arguments[0] : null) is { } owner:
                actions.MakePartyLeader(state.TeamId, owner.CharacterId);
                break;
        }
    }

    public override X2RelationValue? ExecuteRelationCommand(X2RelationCommand command)
    {
        var actions = LiveActions();
        if (actions is null) return base.ExecuteRelationCommand(command);
        if (command.Table != "X2Friend") return null;
        switch (command.Method)
        {
            case "FriendRequest" when Text(command.Arguments, 0) is { Length: > 0 } name:
                actions.AddFriend(name);
                return X2RelationValue.From(true);
            case "DeleteFriend" when Text(command.Arguments, 0) is { Length: > 0 } name:
                actions.RemoveFriend(name);
                return X2RelationValue.From(true);
            case "AcceptFriend" when Unsigned(command.Arguments, 0) is { } characterId:
                actions.AcceptFriend(characterId);
                return X2RelationValue.From(true);
            case "RequestFriendList":
                // SocialState is populated by the server's initial SCFriends page; no list-request writer exists.
                return X2RelationValue.From(true);
            default:
                return null;
        }
    }

    public override X2SocialMailboxState GetMailbox(X2SocialMailboxKind kind)
    {
        var session = _session();
        if (session is null) return base.GetMailbox(kind);
        var state = session.MailState;
        var view = _mailViews[kind];
        var received = ProjectPositions(state, view.Received, view.ReceivedPositions, false);
        var posted = ProjectPositions(state, view.Posted, view.PostedPositions, true);
        X2SocialMailBody? current = null;
        if (view.CurrentId is { } id && state.Bodies.TryGetValue(id, out var body))
        {
            var headers = view.CurrentIsSent ? state.Sent : state.Received;
            headers.TryGetValue(id, out var header);
            current = Body(EffectiveBody(body), header, view.CurrentIsSent);
        }
        var counts = state.Counts;
        var unread = kind == X2SocialMailboxKind.GoodsMail
            ? counts?.CommercialReceived ?? 0
            : counts?.Received ?? 0;
        return new X2SocialMailboxState
        {
            CanSendMail = LiveActions() is not null,
            IsReading = view.Reading,
            IsWriting = view.Writing,
            UnreadCount = unread,
            AttachmentMoneyLimit = long.MaxValue,
            NormalMailCost = 50,
            NormalAttachmentCost = 30,
            ExpressMailCost = 100,
            ExpressAttachmentCost = 80,
            Received = received,
            Posted = posted,
            ReceivedTotal = view.ReceivedTotal,
            PostedTotal = view.PostedTotal,
            Current = current,
            ComposeItems = view.ComposeItems.ToArray(),
        };
    }

    public override void RequestMailList(X2SocialMailboxKind kind, int mailListKind, int startIndex, int count)
    {
        if (startIndex <= 1)
        {
            var target = mailListKind == 3 ? _mailViews[X2SocialMailboxKind.GoodsMail] : _mailViews[kind];
            if (mailListKind == 2) { target.Posted.Clear(); target.PostedPositions.Clear(); target.PostedTotal = -1; }
            else { target.Received.Clear(); target.ReceivedPositions.Clear(); target.ReceivedTotal = -1; }
        }
        LiveActions()?.ListMail((byte)Math.Clamp(mailListKind, 0, byte.MaxValue),
            (uint)Math.Max(0, startIndex), (uint)Math.Max(0, count));
        _lastRequestedMailboxKind = (byte)Math.Clamp(mailListKind, 0, byte.MaxValue);
        _nextMailListIndex = Math.Max(1, startIndex);
    }

    public override void CompleteMailList(X2SocialMailboxKind kind)
    {
        if (_lastRequestedMailboxKind != 0) LiveActions()?.ContinueMailList(_lastRequestedMailboxKind);
    }

    public override void ReadMail(X2SocialMailboxKind kind, bool sent, string mailId)
    {
        if (!long.TryParse(mailId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)) return;
        var view = _mailViews[kind];
        view.CurrentId = id;
        view.CurrentIsSent = sent;
        LiveActions()?.ReadMail(sent, id);
    }

    public override void DeleteMail(X2SocialMailboxKind kind, bool sent, string mailId)
    {
        if (long.TryParse(mailId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
            LiveActions()?.DeleteMail(id, sent);
    }

    public override void ReturnMail(X2SocialMailboxKind kind, string mailId)
    {
        if (long.TryParse(mailId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
            LiveActions()?.ReturnMail(id);
    }

    public override bool SendMail(X2SocialMailboxKind kind, X2SocialOutgoingMail mail)
    {
        var actions = LiveActions();
        if (actions is null || string.IsNullOrWhiteSpace(mail.Receiver)) return false;
        var view = _mailViews[kind];
        var slots = view.ComposeSlots.Where(slot => slot is not null).Select(slot => slot!).ToArray();
        _ = ulong.TryParse(mail.Money, NumberStyles.Integer, CultureInfo.InvariantCulture, out var money);
        var groupRecipients = mail.Receivers
            .Select(value => ulong.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : 0)
            .Where(id => id != 0).ToArray();
        actions.SendMail(new MailSendRequest(
            (byte)Math.Clamp(mail.Type, 0, byte.MaxValue), mail.Receiver, 0, mail.Title, mail.Text,
            (byte)slots.Length, money, 0, 0, 0, 0, mail.GroupMail, slots, mail.DoodadId, 0, groupRecipients));
        Array.Clear(view.ComposeSlots);
        Array.Clear(view.ComposeItems);
        return true;
    }

    public override bool PlaceMailItem(X2SocialMailboxKind kind, int composeIndex)
    {
        var slot = _cursor?.Invoke().BagSlot ?? 0;
        if (slot <= 0 || !PlaceMailItemFromBag(kind, composeIndex, 1, slot)) return false;
        _clearCursor?.Invoke();
        return true;
    }

    public override void SetMailReading(X2SocialMailboxKind kind, bool reading)
    {
        if (_session() is not null) _mailViews[kind].Reading = reading;
    }

    public override void SetMailWriting(X2SocialMailboxKind kind, bool writing)
    {
        if (_session() is not null) _mailViews[kind].Writing = writing;
    }

    public override void ClearCurrentMail(X2SocialMailboxKind kind)
    {
        if (_session() is null) return;
        _mailViews[kind].CurrentId = null;
        _mailViews[kind].Reading = false;
    }

    public override bool PlaceMailItemFromBag(X2SocialMailboxKind kind, int composeIndex, int bagId, int slotIndex)
    {
        if (_session() is not { } session || composeIndex is < 1 or > MailAttachmentSlots) return false;
        // x2ui's AddMailItem supplies bagId=1 and a one-based real bag slot.
        var slotType = bagId == 1 ? (byte)InventorySlotType.Inventory : (byte)Math.Clamp(bagId, 0, byte.MaxValue);
        var protocolSlot = slotIndex - 1;
        if (protocolSlot < 0 || !session.InventoryState.TryGetItem(slotType, protocolSlot, out var snapshot) || snapshot is null) return false;
        var view = _mailViews[kind];
        if (view.ComposeSlots.Where((_, index) => index != composeIndex - 1)
            .Any(existing => existing is { } occupied && occupied.SlotType == slotType && occupied.SlotIndex == protocolSlot))
            return false;
        view.ComposeSlots[composeIndex - 1] = new MailSlot(slotType, (byte)Math.Clamp(protocolSlot, 0, byte.MaxValue));
        view.ComposeItems[composeIndex - 1] = MailItem(snapshot);
        _fireEvent?.Invoke("MAIL_WRITE_ITEM_UPDATE", [(double)composeIndex]);
        return true;
    }

    public override bool ClearMailItem(X2SocialMailboxKind kind, int composeIndex)
    {
        if (_session() is null || composeIndex is < 1 or > MailAttachmentSlots) return false;
        var view = _mailViews[kind];
        view.ComposeSlots[composeIndex - 1] = null;
        view.ComposeItems[composeIndex - 1] = null;
        _fireEvent?.Invoke("MAIL_WRITE_ITEM_UPDATE", [(double)composeIndex]);
        return true;
    }

    public override void TakeCurrentMailItem(X2SocialMailboxKind kind, int itemIndex)
    {
        if (_mailViews[kind].CurrentId is { } id && itemIndex is >= 1 and <= MailAttachmentSlots)
            LiveActions()?.TakeMailItem(id, (byte)InventorySlotType.Mail, (byte)(itemIndex - 1));
    }

    public override void TakeCurrentMailMoney(X2SocialMailboxKind kind)
    {
        if (_mailViews[kind].CurrentId is { } id)
        {
            // A later money-only click supersedes any unanswered take-all request.
            _pendingSequentialMail.Remove(id);
            LiveActions()?.TakeMailMoney(id);
        }
    }

    public override void TakeAllCurrentMailItems(X2SocialMailboxKind kind)
    {
        if (_mailViews[kind].CurrentId is { } id && LiveActions() is { } actions)
        {
            _pendingSequentialMail.Add(id);
            actions.TakeAllMailItems(id);
        }
    }

    public override void TakeMailAttachmentsSequentially(X2SocialMailboxKind kind, string mailId)
    {
        if (long.TryParse(mailId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)
            && LiveActions() is { } actions)
        {
            _pendingSequentialMail.Add(id);
            actions.TakeMailAttachmentsSequentially(id);
        }
    }

    // AAEmu also marks money-only responses as sequential. Only a request from the
    // mailbox's take-all flow may drive its selected-mail completion callback.
    public bool ConsumeSequentialMailRequest(long mailId) => _pendingSequentialMail.Remove(mailId);

    public override void PayCurrentMailCharge(X2SocialMailboxKind kind)
    {
        if (_mailViews[kind].CurrentId is { } id)
            LiveActions()?.PayMailCharge(id, _session()?.InventoryState.AutoUseAaPoint != 0);
    }

    public override void ReportSpamMail(X2SocialMailboxKind kind, string mailId)
    {
        if (!long.TryParse(mailId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)) return;
        var view = _mailViews[kind];
        var header = view.Received.GetValueOrDefault(id) ?? view.Posted.GetValueOrDefault(id);
        if (header is not null) LiveActions()?.ReportSpamMail(header.Type, header.SenderName);
    }

    public override void AcceptComebackRequest(X2SocialMailboxKind kind) => LiveActions()?.AcceptHeroDropoutComeback();

    public override string? GetQuestMailSender(long questMailId) => _gameData?.GetQuestMailSender(questMailId);

    /// <summary>Retains mailbox kind, totals, and mutations that the shared aggregate intentionally normalizes.</summary>
    public void ApplyMail(GameEvent value)
    {
        switch (value)
        {
            case MailListEntryEvent entry:
            {
                var view = _mailViews[entry.MailboxKind == 3 ? X2SocialMailboxKind.GoodsMail : X2SocialMailboxKind.Mail];
                var source = entry.IsSent ? view.Posted : view.Received;
                source[entry.Header.MailId] = entry.Header;
                (entry.IsSent ? view.PostedPositions : view.ReceivedPositions)[_nextMailListIndex++] = entry.Header.MailId;
                if (entry.IsSent) view.PostedTotal = checked((int)entry.Total);
                else view.ReceivedTotal = checked((int)entry.Total);
                break;
            }
            case MailListEndEvent end:
                if (end.MailboxKind == 1) _mailViews[X2SocialMailboxKind.Mail].ReceivedTotal = end.Counts.TotalReceived;
                else if (end.MailboxKind == 2) _mailViews[X2SocialMailboxKind.Mail].PostedTotal = end.Counts.TotalSent;
                else if (end.MailboxKind == 3) _mailViews[X2SocialMailboxKind.GoodsMail].ReceivedTotal = end.Counts.TotalCommercialReceived;
                break;
            case MailCountsEvent counts:
                _mailViews[X2SocialMailboxKind.Mail].ReceivedTotal = counts.Counts.TotalReceived;
                _mailViews[X2SocialMailboxKind.Mail].PostedTotal = counts.Counts.TotalSent;
                _mailViews[X2SocialMailboxKind.GoodsMail].ReceivedTotal = counts.Counts.TotalCommercialReceived;
                break;
            case MailSentEvent sent:
                AddHeader(_mailViews[X2SocialMailboxKind.Mail], sent.Header, true, sent.Counts.TotalSent);
                break;
            case MailReceivedEvent received:
                if (received.Body is not null) ClearTaken(received.Body.MailId);
                AddHeader(_mailViews[X2SocialMailboxKind.Mail], received.Header, false, received.Counts.TotalReceived);
                break;
            case MailBodyEvent body:
                ClearTaken(body.Body.MailId);
                break;
            case MailAttachmentTakenEvent taken:
                if (taken.ItemIds.Count > 0)
                {
                    if (!_takenMailItems.TryGetValue(taken.MailId, out var items))
                        _takenMailItems[taken.MailId] = items = [];
                    foreach (var itemId in taken.ItemIds) items.Add(itemId);
                }
                if (taken.Money) _takenMailMoney.Add(taken.MailId);
                break;
            case MailReturnedEvent returned:
                AddHeader(_mailViews[X2SocialMailboxKind.Mail], returned.Header, false, returned.Counts.TotalReceived);
                break;
            case MailDeletedEvent deleted:
                RemoveHeader(deleted.IsSent, deleted.MailId);
                ClearTaken(deleted.MailId);
                _pendingSequentialMail.Remove(deleted.MailId);
                break;
            case MailRemovedEvent removed:
                RemoveHeader(removed.IsSent, removed.MailId);
                ClearTaken(removed.MailId);
                _pendingSequentialMail.Remove(removed.MailId);
                break;
            case MailStatusUpdatedEvent status:
                UpdateHeader(status.IsSent, status.MailId, header => header with { Status = status.Status });
                break;
            case MailReceiverOpenedEvent opened:
                UpdateHeader(false, opened.MailId, header => header with { OpenDate = opened.OpenDate });
                break;
        }
    }

    private void ClearTaken(long mailId)
    {
        _takenMailItems.Remove(mailId);
        _takenMailMoney.Remove(mailId);
    }

    private MailBodyRecord EffectiveBody(MailBodyRecord body)
    {
        _takenMailItems.TryGetValue(body.MailId, out var takenItems);
        var moneyTaken = _takenMailMoney.Contains(body.MailId);
        if (takenItems is null && !moneyTaken) return body;
        return body with
        {
            Attachments = takenItems is null ? body.Attachments : body.Attachments
                .Select(item => item is not null && takenItems.Contains(item.ItemId) ? null : item).ToArray(),
            Copper = moneyTaken ? 0 : body.Copper,
        };
    }

    private static void AddHeader(MailViewState view, MailHeaderRecord header, bool sent, int total)
    {
        (sent ? view.Posted : view.Received)[header.MailId] = header;
        var positions = sent ? view.PostedPositions : view.ReceivedPositions;
        foreach (var index in positions.Keys.OrderByDescending(index => index).ToArray())
        {
            positions[index + 1] = positions[index];
            positions.Remove(index);
        }
        positions[1] = header.MailId;
        if (sent) view.PostedTotal = total;
        else view.ReceivedTotal = total;
    }

    private X2SocialMailHeader[] ProjectPositions(MailSessionState state,
        Dictionary<long, MailHeaderRecord> headers, SortedDictionary<int, long> positions, bool sent)
    {
        var result = new X2SocialMailHeader[positions.Count == 0 ? 0 : positions.Keys.Max()];
        foreach (var (index, mailId) in positions)
            if (headers.TryGetValue(mailId, out var header)) result[index - 1] = Header(state, header, sent);
        return result;
    }

    private void RemoveHeader(bool sent, long id)
    {
        foreach (var view in _mailViews.Values)
        {
            var source = sent ? view.Posted : view.Received;
            if (!source.Remove(id)) continue;
            var positions = sent ? view.PostedPositions : view.ReceivedPositions;
            var removed = positions.Where(pair => pair.Value == id).Select(pair => pair.Key).ToArray();
            foreach (var index in removed) positions.Remove(index);
            if (removed.Length > 0)
                foreach (var index in positions.Keys.Where(index => index > removed.Min()).OrderBy(index => index).ToArray())
                {
                    positions[index - 1] = positions[index];
                    positions.Remove(index);
                }
            if (sent && view.PostedTotal > 0) view.PostedTotal--;
            if (!sent && view.ReceivedTotal > 0) view.ReceivedTotal--;
        }
    }

    private void UpdateHeader(bool sent, long id, Func<MailHeaderRecord, MailHeaderRecord> update)
    {
        foreach (var view in _mailViews.Values)
        {
            var source = sent ? view.Posted : view.Received;
            if (source.TryGetValue(id, out var header)) source[id] = update(header);
        }
    }

    private X2SocialMailHeader Header(MailSessionState state, MailHeaderRecord header, bool sent)
    {
        state.Bodies.TryGetValue(header.MailId, out var body);
        if (body is not null) body = EffectiveBody(body);
        return new X2SocialMailHeader
        {
            MailId = header.MailId.ToString(CultureInfo.InvariantCulture),
            MailType = header.Type,
            Title = header.Title,
            Sender = header.SenderName,
            Receiver = header.ReceiverName,
            IsRead = header.Status != 0,
            HasAttachments = (body is null ? header.AttachmentCount > 0 : body.Attachments.Any(item => item is not null))
                || body is { Copper: not 0 } || body is { MoneyAmount2: not 0 },
            AttachedItemCount = body is null ? header.AttachmentCount : body.Attachments.Count(item => item is not null),
            FailedToLoadItems = header.FailedToLoadBody,
            Extra = header.Extra,
            IsReturned = header.Returned,
            IsUserMail = header.Type is >= 1 and <= 4,
            IsSystemMail = header.Type >= 5,
            IsBillingMail = header.Type == 6,
            IsBalanceReceiptMail = header.Type == 7,
            IsTaxRateChangedMail = header.Type == 8,
            IsTaxInKindReceiptMail = header.Type == 34,
            IsHeroDropoutComebackRequestMail = header.Type == 48,
            CachedBody = body is null ? null : Body(body, header, sent),
        };
    }

    private X2SocialMailBody Body(MailBodyRecord body, MailHeaderRecord? header, bool sent) => new()
    {
        MailId = body.MailId.ToString(CultureInfo.InvariantCulture),
        IsSent = sent,
        MailType = body.Type,
        Sender = header?.SenderName ?? "",
        Receiver = body.ReceiverName,
        Title = body.Title,
        Text = body.Text,
        SendDate = ProtocolDate(body.SendDate),
        ReceiveDate = ProtocolDate(body.ReceiveDate),
        Money = body.Copper,
        AaPoints = body.MoneyAmount2,
        HonorPoints = body.Reserved,
        ChargeMoney = body.BillingAmount,
        Extra = header?.Extra ?? 0,
        IsReturned = header?.Returned ?? false,
        IsUserMail = body.Type is >= 1 and <= 4,
        IsSystemMail = body.Type >= 5,
        IsBillingMail = body.Type == 6,
        IsBalanceReceiptMail = body.Type == 7,
        IsTaxRateChangedMail = body.Type == 8,
        IsTaxInKindReceiptMail = body.Type == 34,
        IsHeroDropoutComebackRequestMail = body.Type == 48,
        Items = body.Attachments.Select(item => item is null ? null : MailItem(item)).ToArray(),
    };

    private X2SocialMailItem MailItem(ItemSnapshot snapshot)
    {
        var definition = _staticItems.GetItemInfo(snapshot.TemplateId);
        return new X2SocialMailItem
        {
            TemplateId = snapshot.TemplateId,
            ItemId = snapshot.ItemId,
            Name = definition?.Name ?? $"Item {snapshot.TemplateId}",
            IconPath = definition?.Icon ?? "",
            Grade = snapshot.Grade,
            Stack = Math.Max(1, snapshot.Count),
            IsStackable = snapshot.Count > 1,
            Fields = definition?.Fields ?? new Dictionary<string, object?>(),
        };
    }

    private ClientActions? LiveActions() => _session() is null ? null : _actions();

    private static TeamMemberSummary? FindTeamMember(SocialState state, object? value)
    {
        if (value is string text)
        {
            var byName = state.TeamMembers.Values.FirstOrDefault(member =>
                string.Equals(member.Name, text, StringComparison.OrdinalIgnoreCase));
            if (byName is not null) return byName;
            if (ulong.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
                return state.TeamMembers.GetValueOrDefault(id);
        }
        var numeric = value switch { double d when d >= 0 => (ulong)d, int i when i >= 0 => (ulong)i, ulong u => u, _ => 0UL };
        if (numeric == 0) return null;
        return state.TeamMembers.GetValueOrDefault(numeric)
            ?? state.TeamMembers.Values.FirstOrDefault(member => member.ObjectId == numeric);
    }

    private static string? Text(IReadOnlyList<object?> arguments, int index)
        => index < arguments.Count ? arguments[index]?.ToString() : null;

    private static ulong? Unsigned(IReadOnlyList<object?> arguments, int index)
    {
        if (index >= arguments.Count) return null;
        return arguments[index] switch
        {
            ulong value => value,
            long value when value >= 0 => (ulong)value,
            double value when value >= 0 => (ulong)value,
            string value when ulong.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null,
        };
    }

    private static X2SocialElapsedTime Elapsed(long timestamp)
    {
        if (timestamp <= 0) return new X2SocialElapsedTime();
        try
        {
            var seen = timestamp > 10_000_000_000
                ? DateTimeOffset.FromUnixTimeMilliseconds(timestamp)
                : DateTimeOffset.FromUnixTimeSeconds(timestamp);
            var elapsed = DateTimeOffset.UtcNow - seen;
            if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
            return new X2SocialElapsedTime(elapsed.Days / 365, elapsed.Days % 365 / 30,
                elapsed.Days % 30, elapsed.Hours, elapsed.Minutes);
        }
        catch (ArgumentOutOfRangeException)
        {
            return new X2SocialElapsedTime();
        }
    }

    private static DateTime ProtocolDate(long timestamp)
    {
        if (timestamp <= 0) return DateTime.UnixEpoch;
        try
        {
            return (timestamp > 10_000_000_000
                ? DateTimeOffset.FromUnixTimeMilliseconds(timestamp)
                : DateTimeOffset.FromUnixTimeSeconds(timestamp)).LocalDateTime;
        }
        catch (ArgumentOutOfRangeException)
        {
            return DateTime.UnixEpoch;
        }
    }

    private static LuaTable LuaArray(IEnumerable<object?> values)
    {
        var table = new LuaTable();
        var index = 1d;
        foreach (var value in values) table[index++] = value;
        return table;
    }

    private static LuaTable ElapsedTable(X2SocialElapsedTime value) => new()
    {
        ["year"] = (double)value.Year,
        ["month"] = (double)value.Month,
        ["day"] = (double)value.Day,
        ["hour"] = (double)value.Hour,
        ["minute"] = (double)value.Minute,
    };
}
