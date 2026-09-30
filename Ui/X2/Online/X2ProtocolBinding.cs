#nullable enable
using AAEmu.GodotViewer.Client;
using AAEmu.GodotViewer.Net;
using AAEmu.GodotViewer.Lua;
using AAEmu.GodotViewer.Ui.X2.Scripting.Api;
using AAEmu.GodotViewer.Ui.X2.Scripting.World;

namespace AAEmu.GodotViewer.Ui.X2.Online;

/// <summary>
/// Translates applied protocol changes into the event names and argument shapes consumed by x2ui.
/// OnlineSession invokes these callbacks on the main thread, after updating its state tables.
/// </summary>
public sealed class X2ProtocolBinding : IDisposable
{
    private readonly OnlineSession _session;
    private readonly IX2Events _events;
    private readonly X2WorldBridge? _world;
    private readonly X2ProtocolSocialData? _social;
    private readonly X2ProtocolItemsData? _items;
    private readonly X2ProtocolEconomyData? _economy;
    private readonly Action? _prepareInitialQuestSnapshot;
    private readonly Dictionary<long, uint> _questTypesByInstance = new();
    private bool _hasPopulatedQuestSnapshot;
    private long _lastBmPoint;
    private sealed record MateStamp(string UnitId, bool Exists, bool Mounted, string State,
        long Experience, string Skills);
    private Dictionary<int, MateStamp> _mates = [];
    private HashSet<uint> _slaves = [];

    public X2ProtocolBinding(OnlineSession session, IX2Events events, X2WorldBridge? world,
        X2ProtocolSocialData? social = null, X2ProtocolItemsData? items = null,
        Action? prepareInitialQuestSnapshot = null, X2ProtocolEconomyData? economy = null)
    {
        _session = session;
        _events = events;
        _world = world;
        _social = social;
        _items = items;
        _economy = economy;
        _prepareInitialQuestSnapshot = prepareInitialQuestSnapshot;
        session.InventoryChanged += OnInventory;
        session.QuestUpdated += OnQuest;
        session.MailChanged += OnMail;
        session.PartyChanged += OnParty;
        session.SocialChanged += OnSocial;
        session.TradeUpdated += OnTrade;
        session.ShopUpdated += OnShop;
        session.AuctionUpdated += OnAuction;
        session.SkillStarted += OnCraftSkillStarted;
        session.SkillEnded += OnCraftSkillEnded;
        session.CraftFailed += OnCraftFailed;
        session.ActabilityChanged += OnCraftActability;
        session.VehicleUiStateChanged += OnVehicleUiStateChanged;
        // These callbacks run after InventoryState.Apply.  Keep the loot window on the
        // dedicated session boundary instead of treating an arbitrary inventory update as loot.
        session.LootOpened += OnLootOpened;
        session.LootTaken += OnLootTaken;
        session.LootableChanged += OnLootableChanged;
        // CHAT_MESSAGE has one owner: X2WorldBridge.Apply(ChatEvent).
        SnapshotVehicles();
    }

    private X2VehicleUiProjection Vehicles() => new(_session.MateSlaveState, _session.Entered.UnitId,
        _session.Entered.CharacterId, _session.PlayerAttachedUnitId, _world?.TargetId ?? 0);

    private void SnapshotVehicles()
    {
        var projection = Vehicles();
        _mates = projection.Mates.ToDictionary(mate => mate.MateType, Stamp);
        _slaves = projection.OwnedSlaveUnitIds.ToHashSet();
    }

    private static MateStamp Stamp(X2MateState mate) => new(mate.UnitId, mate.Exists, mate.Mounted,
        mate.State, mate.Experience,
        string.Join(",", mate.Skills?.Select(skill => skill.GetValueOrDefault("skillType")) ?? []));

    private void OnVehicleUiStateChanged(GameEvent value)
    {
        var projection = Vehicles();
        var nextMates = projection.Mates.ToDictionary(mate => mate.MateType, Stamp);
        foreach (var mate in nextMates)
        {
            _mates.TryGetValue(mate.Key, out var old);
            var current = mate.Value;
            if (current.Exists && (old is not { Exists: true } || old.UnitId != current.UnitId))
                _events.Fire(X2UnitEvents.SpawnPet, (double)mate.Key);
            if (old is { Exists: true } && (!current.Exists || old.UnitId != current.UnitId))
                _events.Fire(X2UnitEvents.DismissPet, (double)mate.Key);
            if (current.Exists && current.Mounted != (old?.Mounted ?? false))
                _events.Fire(current.Mounted ? "MOUNT_PET" : "UNMOUNT_PET", (double)mate.Key, true);
            if (current.Exists && old != null && current.State != old.State)
                _events.Fire("MATE_STATE_UPDATE", (double)mate.Key, (double)StateIndex(current.State));
            if (current.Exists && old != null && current.Skills != old.Skills)
                _events.Fire("MATE_SKILL_LEARNED", (double)mate.Key, "");
        }
        foreach (var old in _mates.Where(pair => pair.Value.Exists && !nextMates.ContainsKey(pair.Key)))
            _events.Fire(X2UnitEvents.DismissPet, (double)old.Key);
        _mates = nextMates;

        var nextSlaves = projection.OwnedSlaveUnitIds.ToHashSet();
        foreach (var slave in nextSlaves.Except(_slaves))
            _events.Fire(X2UnitEvents.SpawnSlave, slave.ToString(System.Globalization.CultureInfo.InvariantCulture));
        _slaves = nextSlaves;

        if (value is MateEquipmentChangedEvent or MateEquipmentExpiredEvent or MateEquipmentFlagsChangedEvent)
            _events.Fire("UNIT_NPC_EQUIPMENT_CHANGED");
        if (value is SlaveEquipmentChangedEvent or SlaveEquipmentExpiredEvent or SlaveEquipmentFlagsChangedEvent)
            _events.Fire("UPDATE_SLAVE_EQUIPMENT_SLOT", true);
        if (value is MySlaveEvent mine)
            _events.Fire(X2UnitEvents.UnitNameChanged,
                mine.Body.ObjectId.ToString(System.Globalization.CultureInfo.InvariantCulture));

        if (value is UnitPointsEvent points)
            FireVehiclePoints(points.UnitId, checked((long)points.Hp), checked((long)points.Mp));
        else if (value is UnitStateEvent state)
            FireVehiclePoints(state.Snapshot.Unit.UnitId, checked((long)state.Snapshot.Unit.Hp),
                checked((long)state.Snapshot.Unit.Mp));
    }

    private void FireVehiclePoints(uint unitId, long hp, long mp)
    {
        var projection = Vehicles();
        var mateType = projection.MateTypeByUnitId(unitId);
        var token = mateType > 0 ? $"playerpet{mateType}" : projection.OwnedSlaveUnitIds.Contains(unitId)
            ? unitId.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
        if (token == null) return;
        var view = _session.MateSlaveState.Units.GetValueOrDefault(unitId);
        var maxHp = view?.MaxHp ?? (ulong)Math.Max(0, hp);
        var maxMp = view?.Combat?.MaxMp is { } maximum ? Math.Max(0, maximum) : Math.Max(0, mp);
        _events.Fire("UNIT_HEALTH_CHANGED", token, (double)Math.Max(0, hp), (double)maxHp);
        _events.Fire("UNIT_MANA_CHANGED", token, (double)Math.Max(0, mp), (double)maxMp);
    }

    private static int StateIndex(string state) => state switch
    {
        "aggressive" => 1, "protective" => 2, "passive" => 3, "idle" => 4, _ => 0,
    };

    private void OnSocial(SocialEvent value)
    {
        switch (value)
        {
            case FriendsPageEvent:
                if (_social != null)
                    _events.Fire("FRIENDLIST_INFO", (double)_session.SocialState.Friends.Count,
                        _social.BuildFriendListRows());
                break;
            case FriendAcceptedEvent:
            case FriendDeletedEvent:
            case FriendStatusEvent:
                _events.Fire("FRIENDLIST_UPDATE", value is FriendDeletedEvent ? "delete" : "insert", null);
                if (_social != null)
                    _events.Fire("FRIENDLIST_INFO", (double)_session.SocialState.Friends.Count,
                        _social.BuildFriendListRows());
                break;
            case FriendRequestEvent request:
                _events.Fire("WAIT_FRIENDLIST_UPDATE", request.IsRequest ? "request" : "receive");
                if (!request.IsRequest) _events.Fire("WAIT_FRIEND_ADD_ALARM");
                break;
            case FriendCancelEvent:
                _events.Fire("WAIT_FRIENDLIST_UPDATE", "cancel");
                break;
            case BlockedUsersPageEvent:
            case BlockedUserAddedEvent:
            case BlockedUserDeletedEvent:
                _events.Fire("BLOCKED_USER_UPDATE");
                break;
        }
    }

    private void OnInventory(IInventoryProtocolEvent value)
    {
        _economy?.ValidateAuctionAttachment();
        switch (value)
        {
            case InventoryContentsEvent contents:
                // Initial chunks replace a whole container. Refresh its real slots, including old slots now empty.
                if (contents.StartChunk == 0)
                {
                    var capacity = contents.SlotType == (byte)InventorySlotType.Bank
                        ? _session.InventoryState.BankSlots : _session.InventoryState.InventorySlots;
                    if (contents.SlotType is (byte)InventorySlotType.Inventory or (byte)InventorySlotType.Bank)
                        for (var slot = 0; slot < Math.Min(capacity, 255u); slot++) FireSlot(contents.SlotType, (int)slot);
                }
                else foreach (var slot in contents.Slots) FireSlot(slot.SlotType, slot.Slot);
                break;
            case InventoryExpandedEvent expanded:
                _events.Fire(expanded.SlotType == (byte)InventorySlotType.Bank ? "BANK_EXPANDED" : "BAG_EXPANDED");
                break;
            case PreliminaryEquipmentEvent:
                _events.Fire("UNIT_EQUIPMENT_CHANGED", -1d);
                break;
            case EquipmentChangedEvent equipped when equipped.UnitId == _world?.PlayerId:
                foreach (var slot in equipped.Slots) _events.Fire("UNIT_EQUIPMENT_CHANGED", (double)slot.Slot + 1);
                _events.Fire("UNIT_EQUIPMENT_CHANGED", -1d);
                break;
            case CofferContentsEvent coffer:
                foreach (var slot in coffer.Slots)
                    _events.Fire("COFFER_UPDATE", 1d, (double)slot.Slot + 1);
                break;
            case ItemTaskResultEvent task:
                foreach (var change in task.Changes)
                {
                    switch (change)
                    {
                        case ItemAddedChange x: FireSlot(x.SlotType, x.Slot); break;
                        case ItemRemovedChange x: FireSlot(x.SlotType, x.Slot); break;
                        case ItemMovedChange x:
                            FireSlot(x.FromSlotType, x.FromSlot);
                            FireSlot(x.ToSlotType, x.ToSlot);
                            break;
                        case ItemStackChangedChange x when x.SlotType.HasValue && x.Slot.HasValue:
                            FireSlot(x.SlotType.Value, x.Slot.Value); break;
                        case ItemGradeChangedChange x: FireSlot(x.SlotType, x.Slot); break;
                        case ItemDurabilityChangedChange x: FireSlot(x.SlotType, x.Slot); break;
                        case ItemCurrencyChangedChange x: FireMoney(x.WireAction); break;
                        case ItemAutoUseAaPointChangedChange:
                            _events.Fire("CHANGED_AUTO_USE_AAPOINT", _session.InventoryState.AutoUseAaPoint != 0);
                            break;
                    }
                }
                break;
            case CurrencySnapshotEvent:
                _events.Fire("PLAYER_HONOR_POINT");
                _events.Fire("PLAYER_LIVING_POINT");
                break;
            case CurrencyDeltaEvent point:
                _events.Fire(point.Kind == GamePointSlots.Honor ? "PLAYER_HONOR_POINT" : "PLAYER_LIVING_POINT");
                break;
            case BmPointEvent:
                _events.Fire("PLAYER_BM_POINT", _lastBmPoint.ToString(System.Globalization.CultureInfo.InvariantCulture));
                _lastBmPoint = _session.InventoryState.LoyaltyPoints;
                _events.Fire("PLAYER_AA_POINT");
                break;
            case LaborPowerEvent: _events.Fire("LABORPOWER_CHANGED"); break;
        }
    }

    private void OnCraftSkillStarted(SkillStartedEvent skill)
    {
        if (_economy?.CraftSkillStarted(skill, out var remaining) == true)
            _events.Fire(X2EconomyEvents.CraftStarted, (double)remaining);
    }

    private void OnCraftSkillEnded(SkillEndedEvent skill)
    {
        if (_economy?.CraftSkillEnded(skill, out var remaining) != true) return;
        if (remaining > 0) _events.Fire(X2EconomyEvents.CraftEnded, (double)remaining);
        else _events.Fire(X2EconomyEvents.CraftEnded);
        // The craft book's BAG_UPDATE handler is disabled in this client script;
        // PLAYER_MONEY refreshes material counts and the newly executable batch count.
        _events.Fire("PLAYER_MONEY");
    }

    private void OnCraftFailed(CraftFailedEvent failure)
    {
        if (_economy?.CraftFailed(failure) == true)
            _events.Fire(X2EconomyEvents.CraftEnded);
    }

    private void OnCraftActability(ActabilityEvent update)
    {
        _economy?.ApplyCraftActability(update);
        _events.Fire(X2EconomyEvents.ActabilityModifierUpdate);
    }

    private void OnLootOpened(LootBagEvent _) => _events.Fire("LOOT_BAG_CHANGED", false);

    private void OnLootTaken(LootTakenEvent _) =>
        _events.Fire(_session.InventoryState.LootItems.Count == 0 ? "LOOT_BAG_CLOSE" : "LOOT_BAG_CHANGED", false);

    private void OnLootableChanged(LootableEvent value)
    {
        if (!value.HasLoot) _events.Fire("LOOT_BAG_CLOSE");
    }

    private void FireSlot(byte slotType, int wireSlot)
    {
        // InventoryState uses wire slots; X2Bag/X2Bank expose 1-based real slots.
        if (slotType == (byte)InventorySlotType.Inventory) _events.Fire("BAG_UPDATE", 1d, (double)wireSlot + 1);
        else if (slotType == (byte)InventorySlotType.Bank) _events.Fire("BANK_UPDATE", 1d, (double)wireSlot + 1);
        else if (slotType == (byte)InventorySlotType.Equipment) _events.Fire("UNIT_EQUIPMENT_CHANGED", (double)wireSlot + 1);
    }

    private void FireMoney(ItemWireAction action)
    {
        switch (action)
        {
            case ItemWireAction.ChangeMoney: _events.Fire("PLAYER_MONEY"); break;
            case ItemWireAction.ChangeBankMoney: _events.Fire("PLAYER_BANK_MONEY"); break;
            case ItemWireAction.ChangeAaPoint: _events.Fire("PLAYER_AA_POINT"); break;
            case ItemWireAction.ChangeBankAaPoint: _events.Fire("PLAYER_BANK_AA_POINT"); break;
            case ItemWireAction.ChangeGamePoint:
                _events.Fire("PLAYER_HONOR_POINT");
                _events.Fire("PLAYER_LIVING_POINT");
                break;
        }
    }

    private void OnQuest(IQuestProtocolEvent value)
    {
        switch (value)
        {
            case QuestsSnapshotEvent:
                _questTypesByInstance.Clear();
                foreach (var quest in _session.QuestState.ActiveQuests.Values)
                    _questTypesByInstance[quest.InstanceId] = quest.TemplateId;
                // World entry may have saved an empty notifier before the first quest snapshot.
                // Prepare its UI data once quests exist, then let the notifier seed before the
                // quest list reads its checked state during LEFT_LOADING.
                if (!_hasPopulatedQuestSnapshot && _session.QuestState.ActiveQuests.Count > 0)
                {
                    _hasPopulatedQuestSnapshot = true;
                    _prepareInitialQuestSnapshot?.Invoke();
                }
                _events.Fire("QUEST_NOTIFIER_START");
                if (_hasPopulatedQuestSnapshot)
                    _events.Fire("LEFT_LOADING");
                break;
            case QuestContextStartedEvent started:
                _questTypesByInstance[started.Quest.InstanceId] = started.Quest.TemplateId;
                _events.Fire("QUEST_CONTEXT_UPDATED", (double)started.Quest.TemplateId, "started"); break;
            case QuestContextUpdatedEvent updated:
                _questTypesByInstance[updated.Quest.InstanceId] = updated.Quest.TemplateId;
                _events.Fire("QUEST_CONTEXT_UPDATED", (double)updated.Quest.TemplateId, "updated"); break;
            case QuestContextCompletedEvent completed:
                _events.Fire("QUEST_CONTEXT_UPDATED", (double)RemoveQuestType(completed.QuestId), "completed"); break;
            case QuestContextResetEvent reset:
                _events.Fire("QUEST_CONTEXT_UPDATED", (double)RemoveQuestType(reset.QuestId), "dropped"); break;
            case QuestContextsResetEvent reset:
                foreach (var id in reset.QuestIds) _events.Fire("QUEST_CONTEXT_UPDATED", (double)RemoveQuestType(id), "dropped");
                break;
            case QuestContextFailedEvent failed:
                _events.Fire("QUEST_ERROR_INFO", (double)failed.Reason, (double)failed.QuestId); break;
        }
    }

    private uint RemoveQuestType(long instanceId)
    {
        if (!_questTypesByInstance.Remove(instanceId, out var questType))
            return unchecked((uint)instanceId);
        return questType;
    }

    private void OnMail(GameEvent value)
    {
        _social?.ApplyMail(value);
        switch (value)
        {
            case MailListEntryEvent:
                // The packet stream for a page ends with SCMailListEnd; refresh once all
                // positions have arrived so Lua does not request the next page mid-batch.
                break;
            case MailListEndEvent end:
                if (end.MailboxKind == 1) _events.Fire("MAIL_INBOX_UPDATE", false, 3d);
                else if (end.MailboxKind == 2) _events.Fire("MAIL_SENTBOX_UPDATE", false, 3d);
                else if (end.MailboxKind == 3) _events.Fire("GOODS_MAIL_INBOX_UPDATE", false);
                break;
            case MailReceivedEvent: _events.Fire("MAIL_INBOX_UPDATE", false, null); break;
            case MailSentEvent: _events.Fire("MAIL_SENT_SUCCESS"); break;
            case MailBodyEvent body when !body.IsPrepare:
                _events.Fire(body.IsSent ? "MAIL_SENTBOX_UPDATE" : "MAIL_INBOX_UPDATE", true, null); break;
            case MailAttachmentTakenEvent taken:
                if (taken.Sequential && _social?.ConsumeSequentialMailRequest(taken.MailId) == true)
                    _events.Fire("MAIL_INBOX_ATTACHMENT_TAKEN_ALL", taken.MailId.ToString());
                if (taken.Money) _events.Fire("MAIL_INBOX_MONEY_TAKEN");
                if (_session.MailState.Bodies.TryGetValue(taken.MailId, out var takenBody))
                {
                    for (var index = 0; index < takenBody.Attachments.Count; index++)
                        if (takenBody.Attachments[index] is { } item && taken.ItemIds.Contains(item.ItemId))
                            _events.Fire("MAIL_INBOX_ITEM_TAKEN", (double)index + 1);
                }
                break;
            case MailReturnedEvent: _events.Fire("MAIL_RETURNED"); break;
            case MailChargePaidEvent: _events.Fire("MAIL_INBOX_TAX_PAID"); break;
            case MailDeletedEvent deleted:
                _events.Fire(deleted.IsSent ? "MAIL_SENTBOX_UPDATE" : "MAIL_INBOX_UPDATE", false, null); break;
        }
    }

    private void OnParty(SocialEvent value)
    {
        var details = new LuaTable
        {
            ["isParty"] = !_session.SocialState.TeamIsRaid,
            ["jointOrder"] = 1d,
            ["memberIndex"] = 1d,
            ["name"] = "",
            ["teamRoleType"] = 0d,
        };
        switch (value)
        {
            case TeamJoinedEvent joined:
                details["isParty"] = joined.IsParty;
                details["jointOrder"] = (double)joined.JointOrder;
                _events.Fire("TEAM_MEMBERS_CHANGED", "joined_by_self", details); break;
            case TeamLeftEvent left:
                _events.Fire("TEAM_MEMBERS_CHANGED", left.Kicked ? "kicked_by_self" : "leaved_by_self", details); break;
            case TeamDismissedEvent:
                _events.Fire("TEAM_MEMBERS_CHANGED", "dismissed", details); break;
            case TeamMemberJoinedEvent joined:
                details["name"] = joined.Member.Name;
                _events.Fire("TEAM_MEMBERS_CHANGED", "joined", details); break;
            case TeamMemberLeftEvent left:
                _events.Fire("TEAM_MEMBERS_CHANGED", left.Kicked ? "kicked" : "leaved", details); break;
            case TeamMemberDisconnectedEvent disconnected:
                _events.Fire("TEAM_MEMBER_DISCONNECTED", !_session.SocialState.TeamIsRaid, 1d,
                    disconnected.MemberId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    (double)MemberIndex(disconnected.MemberId));
                _events.Fire("TEAM_MEMBERS_CHANGED", "refresh", details); break;
            case TeamMemberRefreshedEvent:
                _events.Fire("TEAM_MEMBERS_CHANGED", "refresh", details); break;
            case TeamMemberRoleChangedEvent role:
                _events.Fire("TEAM_ROLE_CHANGED", 1d, (double)MemberIndex(role.MemberId), (double)role.Role); break;
            case TeamBecameRaidEvent:
                _events.Fire("CONVERT_TO_RAID_TEAM");
                _events.Fire("TEAM_MEMBERS_CHANGED", "refresh", details); break;
            case TeamOwnerChangedEvent:
                _events.Fire("TEAM_MEMBERS_CHANGED", "owner_changed", details); break;
        }
    }

    private int MemberIndex(ulong characterId)
    {
        var index = 1;
        foreach (var id in _session.SocialState.TeamMembers.Keys.OrderBy(id => id))
        {
            if (id == characterId) return index;
            index++;
        }
        return 1;
    }

    private void OnTrade(SocialEvent value)
    {
        switch (value)
        {
            case TradeRequestEvent request when request.CanStart:
                _events.Fire("TRADE_CAN_START", request.ObjectId.ToString()); break;
            case TradeStartedEvent started:
                _events.Fire("TRADE_STARTED", _world?.Get(started.OtherObjectId)?.Name ?? ""); break;
            case TradeCanceledEvent: _events.Fire("TRADE_CANCELED"); break;
            case TradeItemOfferedEvent item:
                if (item.Mine) _events.Fire("TRADE_ITEM_PUTUP", (double)item.Slot + 1, (double)item.Amount);
                else _events.Fire("TRADE_OTHER_ITEM_PUTUP", (double)item.Slot + 1, 0d,
                    (double)item.Amount, new LuaTable());
                break;
            case TradeItemRemovedEvent item:
                _events.Fire(item.Mine ? "TRADE_ITEM_TOOKDOWN" : "TRADE_OTHER_ITEM_TOOKDOWN", (double)item.Slot + 1);
                break;
            case TradeMoneyOfferedEvent money:
                _events.Fire(money.Mine ? "TRADE_MONEY_PUTUP" : "TRADE_OTHER_MONEY_PUTUP",
                    money.Amount.ToString(System.Globalization.CultureInfo.InvariantCulture));
                break;
            case TradeLockChangedEvent locked:
                _events.Fire(locked.MyLock ? "TRADE_LOCKED" : "TRADE_UNLOCKED");
                if (locked.OtherLock) _events.Fire("TRADE_OTHER_LOCKED");
                break;
            case TradeConfirmChangedEvent confirmed:
                if (confirmed.MyConfirmed) _events.Fire("TRADE_OK");
                if (confirmed.OtherConfirmed) _events.Fire("TRADE_OTHER_OK");
                break;
            case TradeCompletedEvent: _events.Fire("TRADE_MADE"); break;
        }
    }

    private void OnShop(GameEvent value)
    {
        switch (value)
        {
            case NpcInteractionEndedEvent: _events.Fire("NPC_INTERACTION_END"); break;
            case SoldItemListEvent: _events.Fire("STORE_SOLD_LIST", _items?.BuildSoldHistoryRows() ?? new LuaTable()); break;
            case StoreTradeFailedEvent: _events.Fire("STORE_TRADE_FAILED"); break;
            case MerchantPurchaseLimitsEvent: _events.Fire("REFRESH_STORE_MERCHANT_GOOD_LIMIT_PURCHASE"); break;
        }
    }

    private void OnAuction(GameEvent value)
    {
        string Name(uint templateId) => _items?.GetItemInfo(templateId)?.Name ?? "";
        switch (value)
        {
            case AuctionSearchResultsEvent:
                _events.Fire(X2EconomyEvents.AuctionItemSearched, false);
                break;
            case AuctionPostedEvent posted:
                _economy?.OnAuctionPosted();
                _events.Fire(X2EconomyEvents.AuctionItemAttachmentStateChanged, false);
                _events.Fire(X2EconomyEvents.AuctionItemPutUp, Name(posted.Lot.Item?.TemplateId ?? 0));
                break;
            case AuctionCanceledEvent canceled:
                _events.Fire(X2EconomyEvents.AuctionCanceled, Name(canceled.Lot.Item?.TemplateId ?? 0));
                _economy?.RefreshAuctionView();
                break;
            case AuctionBidEvent bid:
                _events.Fire(X2EconomyEvents.AuctionBidded, Name((uint)Math.Max(0, bid.ItemTemplateId)),
                    bid.Bid.Money.ToString(System.Globalization.CultureInfo.InvariantCulture));
                _economy?.RefreshAuctionView();
                break;
            case AuctionLowestPriceEvent price:
                _events.Fire(X2EconomyEvents.AuctionLowestPrice, (double)price.ItemTemplateId,
                    (double)price.Grade, price.Money.ToString(System.Globalization.CultureInfo.InvariantCulture));
                break;
            case AuctionSoldRecordsRawEvent sold:
            {
                var rows = AuctionProtocol.ReadSoldDays(sold.RawDailyRows);
                var values = new LuaTable();
                for (var i = 0; i < rows.Count; i++)
                {
                    var row = rows[i];
                    var first = Math.Max(0, i - 6);
                    var week = rows.Skip(first).Take(i - first + 1).Select(day => day.Average).DefaultIfEmpty(0).Average();
                    values[(double)(i + 1)] = new LuaTable {
                        ["dailyAvg"] = (double)row.Average,
                        ["weeklyAvg"] = week,
                        ["volume"] = (double)row.Volume,
                        ["minPrice"] = (double)row.Minimum,
                        ["maxPrice"] = (double)row.Maximum,
                    };
                }
                _events.Fire("DIAGONAL_ASR", Name(sold.ItemTemplateId), (double)sold.Grade,
                    sold.MarketPriceUi, values);
                break;
            }
        }
    }

    public void Dispose()
    {
        _session.InventoryChanged -= OnInventory;
        _session.QuestUpdated -= OnQuest;
        _session.MailChanged -= OnMail;
        _session.PartyChanged -= OnParty;
        _session.SocialChanged -= OnSocial;
        _session.TradeUpdated -= OnTrade;
        _session.ShopUpdated -= OnShop;
        _session.AuctionUpdated -= OnAuction;
        _session.SkillStarted -= OnCraftSkillStarted;
        _session.SkillEnded -= OnCraftSkillEnded;
        _session.CraftFailed -= OnCraftFailed;
        _session.ActabilityChanged -= OnCraftActability;
        _session.VehicleUiStateChanged -= OnVehicleUiStateChanged;
        _session.LootOpened -= OnLootOpened;
        _session.LootTaken -= OnLootTaken;
        _session.LootableChanged -= OnLootableChanged;
    }
}
