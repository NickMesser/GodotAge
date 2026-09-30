#nullable enable

using AAEmu.GodotViewer.Net;

namespace AAEmu.GodotViewer.Client;

public partial class OnlineSession
{
    public InventoryState InventoryState { get; private set; } = new();
    public QuestState QuestState { get; private set; } = new();
    public AchievementState AchievementState { get; private set; } = new();
    public TodayAssignmentState TodayAssignmentState { get; private set; } = new();
    public SocialState SocialState { get; private set; } = new();
    public MailSessionState MailState { get; private set; } = new();
    public ShopSessionState ShopState { get; private set; } = new();
    public AuctionSessionState AuctionState { get; private set; } = new();
    public CraftSessionState CraftState { get; private set; } = new();
    public MateSlaveState MateSlaveState { get; private set; } = new();

    /// <summary>Raised after the matching main-thread state reducer has applied the event.</summary>
    public event Action<IInventoryProtocolEvent>? InventoryChanged;
    public event Action<IInventoryProtocolEvent>? MoneyChanged;
    public event Action<IInventoryProtocolEvent>? LootUpdated;
    public event Action<LootBagEvent>? LootOpened;
    public event Action<IQuestProtocolEvent>? QuestUpdated;
    public event Action<IAssignmentProtocolEvent>? AssignmentUpdated;
    public event Action<SocialEvent>? SocialChanged;
    public event Action<SocialEvent>? PartyChanged;
    public event Action<SocialEvent>? TradeUpdated;
    public event Action<SocialChatMessageEvent>? ChatMessage;
    public event Action<GameEvent>? MailChanged;
    public event Action<MailListEndEvent>? MailListReceived;
    public event Action<GameEvent>? ShopUpdated;
    public event Action<NpcInteractionSkillsEvent>? ShopOpened;
    public event Action<NpcInteractionSkillsEvent>? NpcInteractionStarted;
    public event Action<CraftFailedEvent>? CraftUpdated;
    public event Action<GameEvent>? AuctionUpdated;
    public event Action<IVehicleProtocolEvent>? MateSlaveChanged;
    /// <summary>
    /// Raised after MateSlaveState has incorporated either a vehicle packet or a unit/combat packet which can
    /// complete or update a mate/slave view. This is the UI adapter boundary; the typed packet events remain intact.
    /// </summary>
    public event Action<GameEvent>? VehicleUiStateChanged;
    public event Action<SlaveCreatedEvent>? SlaveCreated;
    public event Action<SlaveRemovedEvent>? SlaveRemoved;
    public event Action<SlaveDespawnEvent>? SlaveDespawned;
    public event Action<SlaveBoundEvent>? SlaveBound;
    public event Action<SlaveStateEvent>? SlaveStateReceived;
    public event Action<MySlaveEvent>? MySlaveReceived;
    public event Action<SlaveEscapedEvent>? SlaveEscaped;
    public event Action<SlaveEquipmentChangedEvent>? SlaveEquipmentChanged;
    public event Action<SlaveEquipmentExpiredEvent>? SlaveEquipmentExpired;
    public event Action<SlaveEquipmentFlagsChangedEvent>? SlaveEquipmentFlagsChanged;
    public event Action<MateSpawnedEvent>? MateSpawned;
    public event Action<MateStateEvent>? MateStateReceived;
    public event Action<MateEquipmentChangedEvent>? MateEquipmentChanged;
    public event Action<MateEquipmentExpiredEvent>? MateEquipmentExpired;
    public event Action<MateEquipmentFlagsChangedEvent>? MateEquipmentFlagsChanged;

    public event Action<InventoryInitEvent>? InventoryInitialized;
    public event Action<InventoryContentsEvent>? InventoryContentsReceived;
    public event Action<InventoryExpandedEvent>? InventoryExpanded;
    public event Action<PreliminaryEquipmentEvent>? PreliminaryEquipmentReceived;
    public event Action<ItemDetailEvent>? ItemDetailUpdated;
    public event Action<ItemTaskNotifyEvent>? ItemTaskNotified;
    public event Action<EquipmentChangedEvent>? EquipmentChanged;
    public event Action<EquipmentIdsEvent>? EquipmentIdsReceived;
    public event Action<CofferContentsEvent>? CofferContentsReceived;
    public event Action<CurrencySnapshotEvent>? CurrencySnapshotReceived;
    public event Action<CurrencyDeltaEvent>? CurrencyDeltaReceived;
    public event Action<CurrencyInitializedEvent>? CurrencyInitialized;
    public event Action<BmPointEvent>? BmPointChanged;
    public event Action<ActionPointDeltaEvent>? ActionPointChanged;
    public event Action<PremiumPointEvent>? PremiumPointChanged;
    public event Action<UnitPremiumPointChangedEvent>? UnitPremiumPointChanged;
    public event Action<CurrencySlotUnlockedEvent>? CurrencySlotUnlocked;
    public event Action<LaborPowerEvent>? LaborPowerChanged;
    public event Action<ItemTaskResultEvent>? ItemTaskCompleted;
    public event Action<LootableEvent>? LootableChanged;
    public event Action<LootTakenEvent>? LootTaken;
    public event Action<LootFailedEvent>? LootFailed;
    public event Action<UnitLootingEvent>? UnitLootingChanged;
    public event Action<LootDiceSummaryEvent>? LootDiceSummarized;

    public event Action<QuestsSnapshotEvent>? QuestsSnapshotReceived;
    public event Action<CompletedQuestsEvent>? CompletedQuestsReceived;
    public event Action<QuestContextStartedEvent>? QuestStarted;
    public event Action<QuestContextUpdatedEvent>? QuestContextUpdated;
    public event Action<QuestContextFailedEvent>? QuestFailed;
    public event Action<QuestContextCompletedEvent>? QuestCompleted;
    public event Action<QuestContextResetEvent>? QuestReset;
    public event Action<QuestContextsResetEvent>? QuestContextsReset;
    public event Action<DoodadQuestMarkerEvent>? DoodadQuestMarkerChanged;
    public event Action<NamedQuestListRawEvent>? NamedQuestListReceived;

    public event Action<FriendsPageEvent>? FriendsReceived;
    public event Action<FriendRequestEvent>? FriendRequested;
    public event Action<FriendAcceptedEvent>? FriendAccepted;
    public event Action<FriendDeletedEvent>? FriendDeleted;
    public event Action<FriendStatusEvent>? FriendStatusChanged;
    public event Action<FriendCancelEvent>? FriendCanceled;
    public event Action<BlockedUsersPageEvent>? BlockedUsersReceived;
    public event Action<BlockedUserAddedEvent>? BlockedUserAdded;
    public event Action<BlockedUserDeletedEvent>? BlockedUserDeleted;
    public event Action<TeamInviteEvent>? TeamInvited;
    public event Action<TeamInviteRejectedEvent>? TeamInviteRejected;
    public event Action<TeamJoinedEvent>? TeamJoined;
    public event Action<TeamLeftEvent>? TeamLeft;
    public event Action<TeamDismissedEvent>? TeamDismissed;
    public event Action<TeamMemberJoinedEvent>? TeamMemberJoined;
    public event Action<TeamMemberLeftEvent>? TeamMemberLeft;
    public event Action<TeamMemberDisconnectedEvent>? TeamMemberDisconnected;
    public event Action<TeamOwnerChangedEvent>? TeamOwnerChanged;
    public event Action<TeamMemberRoleChangedEvent>? TeamMemberRoleChanged;
    public event Action<TeamBecameRaidEvent>? TeamBecameRaid;
    public event Action<TeamMemberRefreshedEvent>? TeamMemberRefreshed;
    public event Action<TeamRemoteMembersEvent>? TeamRemoteMembersReceived;
    public event Action<ExpeditionsPageEvent>? ExpeditionsReceived;
    public event Action<ExpeditionInvitationEvent>? ExpeditionInvited;
    public event Action<ExpeditionMembersPageEvent>? ExpeditionMembersReceived;
    public event Action<ExpeditionMemberListEndEvent>? ExpeditionMemberListEnded;
    public event Action<ExpeditionMemberStatusEvent>? ExpeditionMemberStatusChanged;
    public event Action<ExpeditionOwnerChangedEvent>? ExpeditionOwnerChanged;
    public event Action<ExpeditionRoleChangedEvent>? ExpeditionRoleChanged;
    public event Action<ExpeditionDismissedEvent>? ExpeditionDismissed;
    public event Action<UnitExpeditionChangedEvent>? UnitExpeditionChanged;
    public event Action<ChatChannelJoinedEvent>? ChatChannelJoined;
    public event Action<ChatChannelLeftEvent>? ChatChannelLeft;
    public event Action<ChatFailedEvent>? ChatFailed;
    public event Action<ChatLocalizedMessageEvent>? ChatLocalizedMessage;
    public event Action<DirectChatStartedEvent>? DirectChatStarted;
    public event Action<DirectChatMessageEvent>? DirectChatMessage;
    public event Action<TradeRequestEvent>? TradeRequested;
    public event Action<TradeStartedEvent>? TradeStarted;
    public event Action<TradeCanceledEvent>? TradeCanceled;
    public event Action<TradeItemOfferedEvent>? TradeItemOffered;
    public event Action<TradeItemRemovedEvent>? TradeItemRemoved;
    public event Action<TradeMoneyOfferedEvent>? TradeMoneyOffered;
    public event Action<TradeLockChangedEvent>? TradeLockChanged;
    public event Action<TradeConfirmChangedEvent>? TradeConfirmChanged;
    public event Action<TradeCompletedEvent>? TradeCompleted;
    public event Action<SocialRawBodyEvent>? SocialRawBodyReceived;

    public event Action<MailFailedEvent>? MailFailed;
    public event Action<MailCountsEvent>? MailCountsChanged;
    public event Action<MailSentEvent>? MailSent;
    public event Action<MailReceivedEvent>? MailReceived;
    public event Action<MailListEntryEvent>? MailListEntryReceived;
    public event Action<MailBodyEvent>? MailBodyReceived;
    public event Action<MailReceiverOpenedEvent>? MailReceiverOpened;
    public event Action<MailAttachmentTakenEvent>? MailAttachmentTaken;
    public event Action<MailChargePaidEvent>? MailChargePaid;
    public event Action<MailDeletedEvent>? MailDeleted;
    public event Action<MailReturnedEvent>? MailReturned;
    public event Action<MailStatusUpdatedEvent>? MailStatusUpdated;
    public event Action<MailRemovedEvent>? MailRemoved;

    public event Action<SpecialtyGoodsPageEvent>? SpecialtyGoodsReceived;
    public event Action<SpecialtyRecordsEvent>? SpecialtyRecordsReceived;
    public event Action<SpecialtyEventMessageEvent>? SpecialtyEventMessage;
    public event Action<SpecialtyCurrentEvent>? SpecialtyCurrentReceived;
    public event Action<SoldItemListEvent>? SoldItemListReceived;
    public event Action<StoreTradeFailedEvent>? StoreTradeFailed;
    public event Action<MerchantPurchaseLimitsEvent>? MerchantPurchaseLimitsReceived;
    public event Action<MerchantPurchaseLimitFailedEvent>? MerchantPurchaseLimitFailed;
    public event Action<MerchantPurchaseLimitsResetEvent>? MerchantPurchaseLimitsReset;
    public event Action<NpcDialogueEvent>? NpcDialogue;
    public event Action<NpcInteractionEndedEvent>? NpcInteractionEnded;
    public event Action<NpcInteractionStatusEvent>? NpcInteractionStatusChanged;
    public event Action<WorldInteractionSkillsEvent>? WorldInteractionSkillsReceived;
    public event Action<WorldInteractionCanceledEvent>? WorldInteractionCanceled;
    public event Action<DoodadInteractionCallbackEvent>? DoodadInteractionCallback;
    public event Action<DoodadFirstInteractionEvent>? DoodadFirstInteraction;

    public event Action<CraftFailedEvent>? CraftFailed;
    public event Action<AuctionPostedEvent>? AuctionPosted;
    public event Action<AuctionSearchResultsEvent>? AuctionSearchResultsReceived;
    public event Action<AuctionLowestPriceEvent>? AuctionLowestPriceReceived;
    public event Action<AuctionBidEvent>? AuctionBid;
    public event Action<AuctionCanceledEvent>? AuctionCanceled;
    public event Action<AuctionMessageEvent>? AuctionMessage;
    public event Action<AuctionSoldRecordsRawEvent>? AuctionSoldRecordsReceived;
    public event Action<AuctionLimitedPricesEvent>? AuctionLimitedPricesReceived;

    private void InitializeProtocolState()
    {
        InventoryState.ClearForCharacterLoad();
        SeedEnteredEquipment();
        QuestState.ClearForCharacterLoad();
        AchievementState.Clear();
        TodayAssignmentState.Clear();
        SocialState.ClearForCharacterLoad();
        MailState.Clear();
        ShopState.Clear();
        MateSlaveState.Clear();
    }

    private void SeedEnteredEquipment()
    {
        if (Entered?.Self is { } self)
            InventoryState.SeedUnitEquipment(Entered.UnitId, self.Equipment);
    }

    /// <summary>Runs after CombatState.Apply on the main-thread dequeue path.</summary>
    private void ApplyMateSlaveEvent(GameEvent value)
    {
        MateSlaveState.Apply(value, CombatState);
        if (value is IVehicleProtocolEvent or UnitAppearedEvent or UnitStateEvent or UnitsRemovedEvent or UnitMovedEvent or
            UnitPointsEvent or UnitLevelEvent or ExperienceChangedEvent or UnitNameChangedEvent)
            VehicleUiStateChanged?.Invoke(value);
        if (value is not IVehicleProtocolEvent vehicle)
            return;

        MateSlaveChanged?.Invoke(vehicle);
        switch (value)
        {
            case SlaveCreatedEvent x: SlaveCreated?.Invoke(x); break;
            case SlaveRemovedEvent x: SlaveRemoved?.Invoke(x); break;
            case SlaveDespawnEvent x: SlaveDespawned?.Invoke(x); break;
            case SlaveBoundEvent x: SlaveBound?.Invoke(x); break;
            case SlaveStateEvent x: SlaveStateReceived?.Invoke(x); break;
            case MySlaveEvent x: MySlaveReceived?.Invoke(x); break;
            case SlaveEscapedEvent x: SlaveEscaped?.Invoke(x); break;
            case SlaveEquipmentChangedEvent x: SlaveEquipmentChanged?.Invoke(x); break;
            case SlaveEquipmentExpiredEvent x: SlaveEquipmentExpired?.Invoke(x); break;
            case SlaveEquipmentFlagsChangedEvent x: SlaveEquipmentFlagsChanged?.Invoke(x); break;
            case MateSpawnedEvent x: MateSpawned?.Invoke(x); break;
            case MateStateEvent x: MateStateReceived?.Invoke(x); break;
            case MateEquipmentChangedEvent x: MateEquipmentChanged?.Invoke(x); break;
            case MateEquipmentExpiredEvent x: MateEquipmentExpired?.Invoke(x); break;
            case MateEquipmentFlagsChangedEvent x: MateEquipmentFlagsChanged?.Invoke(x); break;
        }
    }

    /// <summary>Runs only from Handle, which is called by the main-thread _Process dequeue loop.</summary>
    private void ApplyProtocolEvent(GameEvent value)
    {
        if (value is IAssignmentProtocolEvent assignment)
        {
            AchievementState.Apply(assignment);
            TodayAssignmentState.Apply(assignment);
            AssignmentUpdated?.Invoke(assignment);
            return;
        }

        if (value is IInventoryProtocolEvent inventory)
        {
            InventoryState.Apply(inventory);
            // SCCharacterInvenInit resets all inventory state but does not carry equipment. Restore the
            // item views that arrived earlier in the local player's SCUnitState before notifying the UI.
            if (inventory is InventoryInitEvent)
                SeedEnteredEquipment();
            InventoryChanged?.Invoke(inventory);
            if (IsMoneyEvent(value)) MoneyChanged?.Invoke(inventory);
            if (IsLootEvent(value)) LootUpdated?.Invoke(inventory);
            if (value is LootBagEvent opened) LootOpened?.Invoke(opened);
            PublishConcreteProtocolEvent(value);
            return;
        }

        if (value is IQuestProtocolEvent quest)
        {
            QuestState.Apply(quest);
            QuestUpdated?.Invoke(quest);
            PublishConcreteProtocolEvent(value);
            return;
        }

        if (value is SocialProtocolGameEvent { Value: var social })
        {
            SocialState.Apply(social);
            SocialChanged?.Invoke(social);
            if (IsPartyEvent(social)) PartyChanged?.Invoke(social);
            if (IsTradeEvent(social)) TradeUpdated?.Invoke(social);
            if (social is SocialChatMessageEvent message) ChatMessage?.Invoke(message);
            PublishConcreteSocialEvent(social);
            return;
        }

        if (IsMailEvent(value))
        {
            MailState.Apply(value);
            MailChanged?.Invoke(value);
            if (value is MailListEndEvent list) MailListReceived?.Invoke(list);
            PublishConcreteProtocolEvent(value);
            return;
        }

        if (IsShopEvent(value))
        {
            ShopState.Apply(value);
            ShopUpdated?.Invoke(value);
            // A house's skill list is the housing reducer's (the window or the build step), not an NPC dialog.
            if (value is NpcInteractionSkillsEvent opened && HouseTimelineOf(opened.NpcUnitId) is null)
            {
                ShopOpened?.Invoke(opened);
                NpcInteractionStarted?.Invoke(opened);
            }
            PublishConcreteProtocolEvent(value);
            return;
        }

        if (value is CraftFailedEvent craft)
        {
            CraftState.Apply(craft);
            CraftUpdated?.Invoke(craft);
            PublishConcreteProtocolEvent(value);
            return;
        }

        if (IsAuctionEvent(value))
        {
            AuctionState.Apply(value);
            AuctionUpdated?.Invoke(value);
            PublishConcreteProtocolEvent(value);
        }
    }

    private void PublishConcreteProtocolEvent(GameEvent value)
    {
        switch (value)
        {
            case InventoryInitEvent x: InventoryInitialized?.Invoke(x); break;
            case InventoryContentsEvent x: InventoryContentsReceived?.Invoke(x); break;
            case InventoryExpandedEvent x: InventoryExpanded?.Invoke(x); break;
            case PreliminaryEquipmentEvent x: PreliminaryEquipmentReceived?.Invoke(x); break;
            case ItemDetailEvent x: ItemDetailUpdated?.Invoke(x); break;
            case ItemTaskNotifyEvent x: ItemTaskNotified?.Invoke(x); break;
            case EquipmentChangedEvent x: EquipmentChanged?.Invoke(x); break;
            case EquipmentIdsEvent x: EquipmentIdsReceived?.Invoke(x); break;
            case CofferContentsEvent x: CofferContentsReceived?.Invoke(x); break;
            case CurrencySnapshotEvent x: CurrencySnapshotReceived?.Invoke(x); break;
            case CurrencyDeltaEvent x: CurrencyDeltaReceived?.Invoke(x); break;
            case CurrencyInitializedEvent x: CurrencyInitialized?.Invoke(x); break;
            case BmPointEvent x: BmPointChanged?.Invoke(x); break;
            case ActionPointDeltaEvent x: ActionPointChanged?.Invoke(x); break;
            case PremiumPointEvent x: PremiumPointChanged?.Invoke(x); break;
            case UnitPremiumPointChangedEvent x: UnitPremiumPointChanged?.Invoke(x); break;
            case CurrencySlotUnlockedEvent x: CurrencySlotUnlocked?.Invoke(x); break;
            case LaborPowerEvent x: LaborPowerChanged?.Invoke(x); break;
            case ItemTaskResultEvent x: ItemTaskCompleted?.Invoke(x); break;
            case LootableEvent x: LootableChanged?.Invoke(x); break;
            case LootTakenEvent x: LootTaken?.Invoke(x); break;
            case LootFailedEvent x: LootFailed?.Invoke(x); break;
            case UnitLootingEvent x: UnitLootingChanged?.Invoke(x); break;
            case LootDiceSummaryEvent x: LootDiceSummarized?.Invoke(x); break;

            case QuestsSnapshotEvent x: QuestsSnapshotReceived?.Invoke(x); break;
            case CompletedQuestsEvent x: CompletedQuestsReceived?.Invoke(x); break;
            case QuestContextStartedEvent x: QuestStarted?.Invoke(x); break;
            case QuestContextUpdatedEvent x: QuestContextUpdated?.Invoke(x); break;
            case QuestContextFailedEvent x: QuestFailed?.Invoke(x); break;
            case QuestContextCompletedEvent x: QuestCompleted?.Invoke(x); break;
            case QuestContextResetEvent x: QuestReset?.Invoke(x); break;
            case QuestContextsResetEvent x: QuestContextsReset?.Invoke(x); break;
            case DoodadQuestMarkerEvent x: DoodadQuestMarkerChanged?.Invoke(x); break;
            case NamedQuestListRawEvent x: NamedQuestListReceived?.Invoke(x); break;

            case MailFailedEvent x: MailFailed?.Invoke(x); break;
            case MailCountsEvent x: MailCountsChanged?.Invoke(x); break;
            case MailSentEvent x: MailSent?.Invoke(x); break;
            case MailReceivedEvent x: MailReceived?.Invoke(x); break;
            case MailListEntryEvent x: MailListEntryReceived?.Invoke(x); break;
            case MailBodyEvent x: MailBodyReceived?.Invoke(x); break;
            case MailReceiverOpenedEvent x: MailReceiverOpened?.Invoke(x); break;
            case MailAttachmentTakenEvent x: MailAttachmentTaken?.Invoke(x); break;
            case MailChargePaidEvent x: MailChargePaid?.Invoke(x); break;
            case MailDeletedEvent x: MailDeleted?.Invoke(x); break;
            case MailReturnedEvent x: MailReturned?.Invoke(x); break;
            case MailStatusUpdatedEvent x: MailStatusUpdated?.Invoke(x); break;
            case MailRemovedEvent x: MailRemoved?.Invoke(x); break;

            case SpecialtyGoodsPageEvent x: SpecialtyGoodsReceived?.Invoke(x); break;
            case SpecialtyRecordsEvent x: SpecialtyRecordsReceived?.Invoke(x); break;
            case SpecialtyEventMessageEvent x: SpecialtyEventMessage?.Invoke(x); break;
            case SpecialtyCurrentEvent x: SpecialtyCurrentReceived?.Invoke(x); break;
            case SoldItemListEvent x: SoldItemListReceived?.Invoke(x); break;
            case StoreTradeFailedEvent x: StoreTradeFailed?.Invoke(x); break;
            case MerchantPurchaseLimitsEvent x: MerchantPurchaseLimitsReceived?.Invoke(x); break;
            case MerchantPurchaseLimitFailedEvent x: MerchantPurchaseLimitFailed?.Invoke(x); break;
            case MerchantPurchaseLimitsResetEvent x: MerchantPurchaseLimitsReset?.Invoke(x); break;
            case NpcDialogueEvent x: NpcDialogue?.Invoke(x); break;
            case NpcInteractionEndedEvent x: NpcInteractionEnded?.Invoke(x); break;
            case NpcInteractionStatusEvent x: NpcInteractionStatusChanged?.Invoke(x); break;
            case WorldInteractionSkillsEvent x: WorldInteractionSkillsReceived?.Invoke(x); break;
            case WorldInteractionCanceledEvent x: WorldInteractionCanceled?.Invoke(x); break;
            case DoodadInteractionCallbackEvent x: DoodadInteractionCallback?.Invoke(x); break;
            case DoodadFirstInteractionEvent x: DoodadFirstInteraction?.Invoke(x); break;

            case CraftFailedEvent x: CraftFailed?.Invoke(x); break;
            case AuctionPostedEvent x: AuctionPosted?.Invoke(x); break;
            case AuctionSearchResultsEvent x: AuctionSearchResultsReceived?.Invoke(x); break;
            case AuctionLowestPriceEvent x: AuctionLowestPriceReceived?.Invoke(x); break;
            case AuctionBidEvent x: AuctionBid?.Invoke(x); break;
            case AuctionCanceledEvent x: AuctionCanceled?.Invoke(x); break;
            case AuctionMessageEvent x: AuctionMessage?.Invoke(x); break;
            case AuctionSoldRecordsRawEvent x: AuctionSoldRecordsReceived?.Invoke(x); break;
            case AuctionLimitedPricesEvent x: AuctionLimitedPricesReceived?.Invoke(x); break;
        }
    }

    private void PublishConcreteSocialEvent(SocialEvent value)
    {
        switch (value)
        {
            case FriendsPageEvent x: FriendsReceived?.Invoke(x); break;
            case FriendRequestEvent x: FriendRequested?.Invoke(x); break;
            case FriendAcceptedEvent x: FriendAccepted?.Invoke(x); break;
            case FriendDeletedEvent x: FriendDeleted?.Invoke(x); break;
            case FriendStatusEvent x: FriendStatusChanged?.Invoke(x); break;
            case FriendCancelEvent x: FriendCanceled?.Invoke(x); break;
            case BlockedUsersPageEvent x: BlockedUsersReceived?.Invoke(x); break;
            case BlockedUserAddedEvent x: BlockedUserAdded?.Invoke(x); break;
            case BlockedUserDeletedEvent x: BlockedUserDeleted?.Invoke(x); break;
            case TeamInviteEvent x: TeamInvited?.Invoke(x); break;
            case TeamInviteRejectedEvent x: TeamInviteRejected?.Invoke(x); break;
            case TeamJoinedEvent x: TeamJoined?.Invoke(x); break;
            case TeamLeftEvent x: TeamLeft?.Invoke(x); break;
            case TeamDismissedEvent x: TeamDismissed?.Invoke(x); break;
            case TeamMemberJoinedEvent x: TeamMemberJoined?.Invoke(x); break;
            case TeamMemberLeftEvent x: TeamMemberLeft?.Invoke(x); break;
            case TeamMemberDisconnectedEvent x: TeamMemberDisconnected?.Invoke(x); break;
            case TeamOwnerChangedEvent x: TeamOwnerChanged?.Invoke(x); break;
            case TeamMemberRoleChangedEvent x: TeamMemberRoleChanged?.Invoke(x); break;
            case TeamBecameRaidEvent x: TeamBecameRaid?.Invoke(x); break;
            case TeamMemberRefreshedEvent x: TeamMemberRefreshed?.Invoke(x); break;
            case TeamRemoteMembersEvent x: TeamRemoteMembersReceived?.Invoke(x); break;
            case ExpeditionsPageEvent x: ExpeditionsReceived?.Invoke(x); break;
            case ExpeditionInvitationEvent x: ExpeditionInvited?.Invoke(x); break;
            case ExpeditionMembersPageEvent x: ExpeditionMembersReceived?.Invoke(x); break;
            case ExpeditionMemberListEndEvent x: ExpeditionMemberListEnded?.Invoke(x); break;
            case ExpeditionMemberStatusEvent x: ExpeditionMemberStatusChanged?.Invoke(x); break;
            case ExpeditionOwnerChangedEvent x: ExpeditionOwnerChanged?.Invoke(x); break;
            case ExpeditionRoleChangedEvent x: ExpeditionRoleChanged?.Invoke(x); break;
            case ExpeditionDismissedEvent x: ExpeditionDismissed?.Invoke(x); break;
            case UnitExpeditionChangedEvent x: UnitExpeditionChanged?.Invoke(x); break;
            case ChatChannelJoinedEvent x: ChatChannelJoined?.Invoke(x); break;
            case ChatChannelLeftEvent x: ChatChannelLeft?.Invoke(x); break;
            case ChatFailedEvent x: ChatFailed?.Invoke(x); break;
            case ChatLocalizedMessageEvent x: ChatLocalizedMessage?.Invoke(x); break;
            case DirectChatStartedEvent x: DirectChatStarted?.Invoke(x); break;
            case DirectChatMessageEvent x: DirectChatMessage?.Invoke(x); break;
            case TradeRequestEvent x: TradeRequested?.Invoke(x); break;
            case TradeStartedEvent x: TradeStarted?.Invoke(x); break;
            case TradeCanceledEvent x: TradeCanceled?.Invoke(x); break;
            case TradeItemOfferedEvent x: TradeItemOffered?.Invoke(x); break;
            case TradeItemRemovedEvent x: TradeItemRemoved?.Invoke(x); break;
            case TradeMoneyOfferedEvent x: TradeMoneyOffered?.Invoke(x); break;
            case TradeLockChangedEvent x: TradeLockChanged?.Invoke(x); break;
            case TradeConfirmChangedEvent x: TradeConfirmChanged?.Invoke(x); break;
            case TradeCompletedEvent x: TradeCompleted?.Invoke(x); break;
            case SocialRawBodyEvent x: SocialRawBodyReceived?.Invoke(x); break;
        }
    }

    private static bool IsMoneyEvent(GameEvent value) => value is
        CurrencySnapshotEvent or CurrencyDeltaEvent or CurrencyInitializedEvent or BmPointEvent or
        ActionPointDeltaEvent or PremiumPointEvent or UnitPremiumPointChangedEvent or
        CurrencySlotUnlockedEvent or LaborPowerEvent ||
        value is ItemTaskResultEvent task && task.Changes.Any(change => change is ItemCurrencyChangedChange);

    private static bool IsLootEvent(GameEvent value) => value is
        LootableEvent or LootBagEvent or LootTakenEvent or LootFailedEvent or UnitLootingEvent or LootDiceSummaryEvent;

    private static bool IsPartyEvent(SocialEvent value) => value is
        TeamInviteEvent or TeamInviteRejectedEvent or TeamJoinedEvent or TeamLeftEvent or TeamDismissedEvent or
        TeamMemberJoinedEvent or TeamMemberLeftEvent or TeamMemberDisconnectedEvent or TeamOwnerChangedEvent or
        TeamMemberRoleChangedEvent or TeamBecameRaidEvent or TeamMemberRefreshedEvent or TeamRemoteMembersEvent;

    private static bool IsTradeEvent(SocialEvent value) => value is
        TradeRequestEvent or TradeStartedEvent or TradeCanceledEvent or TradeItemOfferedEvent or
        TradeItemRemovedEvent or TradeMoneyOfferedEvent or TradeLockChangedEvent or
        TradeConfirmChangedEvent or TradeCompletedEvent;

    private static bool IsMailEvent(GameEvent value) => value is
        MailFailedEvent or MailCountsEvent or MailSentEvent or MailReceivedEvent or MailListEntryEvent or
        MailListEndEvent or MailBodyEvent or MailReceiverOpenedEvent or MailAttachmentTakenEvent or
        MailChargePaidEvent or MailDeletedEvent or MailReturnedEvent or MailStatusUpdatedEvent or MailRemovedEvent;

    private static bool IsShopEvent(GameEvent value) => value is
        SpecialtyGoodsPageEvent or SpecialtyRecordsEvent or SpecialtyEventMessageEvent or SpecialtyCurrentEvent or
        SoldItemListEvent or StoreTradeFailedEvent or MerchantPurchaseLimitsEvent or
        MerchantPurchaseLimitFailedEvent or MerchantPurchaseLimitsResetEvent or NpcDialogueEvent or
        NpcInteractionSkillsEvent or NpcInteractionEndedEvent or NpcInteractionStatusEvent or
        WorldInteractionSkillsEvent or WorldInteractionCanceledEvent or DoodadInteractionCallbackEvent or
        DoodadFirstInteractionEvent;

    private static bool IsAuctionEvent(GameEvent value) => value is
        AuctionPostedEvent or AuctionSearchResultsEvent or AuctionLowestPriceEvent or AuctionBidEvent or
        AuctionCanceledEvent or AuctionMessageEvent or AuctionSoldRecordsRawEvent or AuctionLimitedPricesEvent;
}
