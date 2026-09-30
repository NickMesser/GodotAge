#nullable enable

using System;
using System.Collections.Generic;
using AAEmu.GodotViewer.Data;
using AAEmu.GodotViewer.Net;

namespace AAEmu.GodotViewer.Client;

/// <summary>
/// UI-facing sender for requests with a recovered client packet layout.  This class contains no
/// gameplay decisions: callers supply every value that the original client puts on the wire.
/// </summary>
public sealed partial class ClientActions
{
    private readonly Action<ushort, byte[]> _sendGame;
    private readonly GameData _gameData;

    /// <summary>Constructs an action surface over the game-channel sender and read-only content database.</summary>
    public ClientActions(Action<ushort, byte[]> sendGame, GameData gameData)
    {
        _sendGame = sendGame ?? throw new ArgumentNullException(nameof(sendGame));
        _gameData = gameData ?? throw new ArgumentNullException(nameof(gameData));
    }

    /// <summary>Constructs an action surface for an active viewer connection.</summary>
    public ClientActions(GameClient client, GameData gameData) : this((opcode, body) => client.SendGame(opcode, body), gameData)
    {
        ArgumentNullException.ThrowIfNull(client);
    }

    public void MoveItem(ulong itemId, byte fromSlotType, byte fromSlot, byte toSlotType, byte toSlot,
        ulong displacedItemId = 0) => Send(ItemClientWriters.Move(itemId, fromSlotType, fromSlot, toSlotType, toSlot, displacedItemId));

    public void SplitItem(ulong fromItemId, ulong toItemId, byte fromSlotType, byte fromSlot,
        byte toSlotType, byte toSlot, int count) =>
        Send(ItemClientWriters.Split(fromItemId, toItemId, fromSlotType, fromSlot, toSlotType, toSlot, count));

    public void SwapItems(ulong fromItemId, ulong toItemId, byte fromSlotType, byte fromSlot,
        byte toSlotType, byte toSlot) =>
        Send(ItemClientWriters.Swap(fromItemId, toItemId, fromSlotType, fromSlot, toSlotType, toSlot));

    public void EquipItem(ulong itemId, byte bagSlot, byte equipmentSlot, ulong displacedItemId = 0,
        byte inventorySlotType = (byte)InventorySlotType.Inventory,
        byte equipmentSlotType = (byte)InventorySlotType.Equipment) =>
        Send(ItemClientWriters.Equip(itemId, bagSlot, equipmentSlot, displacedItemId, inventorySlotType, equipmentSlotType));

    public void UnequipItem(ulong itemId, byte equipmentSlot, byte bagSlot, ulong displacedItemId = 0,
        byte equipmentSlotType = (byte)InventorySlotType.Equipment,
        byte inventorySlotType = (byte)InventorySlotType.Inventory) =>
        Send(ItemClientWriters.Unequip(itemId, equipmentSlot, bagSlot, displacedItemId, equipmentSlotType, inventorySlotType));

    public void DestroyItem(ulong itemId, byte slotType, byte slot, uint amount) =>
        Send(ItemClientWriters.Destroy(itemId, slotType, slot, amount));

    /// <summary>Uses the source item's database <c>use_skill_id</c> through an item caster.</summary>
    public void UseItem(ItemSkillCast cast) =>
        Send(CombatPacketWriters.StartItemSkillOnUnit(UseSkillId(cast.ItemTemplateId), cast.CasterUnitId, cast.ItemId,
            cast.ItemTemplateId, cast.Type1, cast.Type2, cast.TargetUnitId));

    public void UseSelectiveItem(byte slotType, byte slot, uint tryCount, IReadOnlyList<uint> optionIndices) =>
        Send(ItemClientWriters.UseSelectiveItem(slotType, slot, tryCount, optionIndices));

    public void LootItem(ushort itemIndex, ushort ownerType, uint ownerObjectId, ushort unknown1, ushort unknown2) =>
        Send(ItemClientWriters.LootOne(itemIndex, ownerType, ownerObjectId, unknown1, unknown2));

    public void OpenLoot(uint ownerObjectId, uint secondaryObjectId, bool lootAll = false) =>
        Send(ItemClientWriters.OpenLoot(ownerObjectId, secondaryObjectId, lootAll));

    public void LootAll(uint ownerObjectId, uint secondaryObjectId = 0) => Send(ItemClientWriters.LootAll(ownerObjectId, secondaryObjectId));

    public void CloseLoot(ushort itemIndex, ushort ownerType, uint ownerObjectId, byte unknown) =>
        Send(ItemClientWriters.CloseLoot(itemIndex, ownerType, ownerObjectId, unknown));

    public void Buy(uint npcObjectId, uint doodadObjectId, uint shopType, IReadOnlyList<NpcBuyEntry> entries,
        IReadOnlyList<int>? buybackSlots = null, bool useAaPoint = false, byte openType = 0) =>
        Send(ItemClientWriters.Buy(npcObjectId, doodadObjectId, shopType, entries, buybackSlots, useAaPoint, openType));

    public void Sell(uint npcObjectId, uint secondaryObjectId, IReadOnlyList<NpcSellEntry> entries) =>
        Send(ItemClientWriters.Sell(npcObjectId, secondaryObjectId, entries));

    public void ListSoldItems(uint npcObjectId) => Send(ItemClientWriters.ListSoldItems(npcObjectId));

    public void RepairItem(byte slotType, byte slot, bool autoUseAaPoint = false, bool inBag = false) =>
        Send(ItemClientWriters.RepairOne(slotType, slot, autoUseAaPoint, inBag));

    public void RepairAll(bool autoUseAaPoint = false, bool inBag = false) => Send(ItemClientWriters.RepairAll(autoUseAaPoint, inBag));

    public void AcceptQuest(uint questContextId, uint npcObjectId = 0, uint doodadObjectId = 0, uint sphereId = 0) =>
        Send(QuestProtocol.CSStartQuestContext, QuestProtocol.WriteStart(questContextId, npcObjectId, doodadObjectId, sphereId));

    public void CompleteQuest(uint questContextId, uint npcObjectId = 0, uint doodadObjectId = 0, int selectedReward = 0) =>
        Send(QuestProtocol.CSCompleteQuestContext, QuestProtocol.WriteComplete(questContextId, npcObjectId, doodadObjectId, selectedReward));

    public void DropQuest(uint questId) => Send(QuestProtocol.CSDropQuestContext, QuestProtocol.WriteDrop(questId));

    public void TalkQuest(uint npcObjectId, uint questContextId, uint componentId, uint actionId) =>
        Send(QuestProtocol.CSQuestTalkMade, QuestProtocol.WriteTalk(npcObjectId, questContextId, componentId, actionId));

    public void EndNpcInteraction(uint npcUnitId) => Send(CombatPacketWriters.EndNpcInteraction(npcUnitId));

    public void SendMail(MailSendRequest request) => Send(MailProtocol.CSSendMail, MailProtocol.WriteSend(
        request.Kind, request.Receiver, request.ReceiverReference, request.Title, request.Text, request.AttachmentCount,
        request.Money0, request.Money1, request.Money2, request.Money3, request.Extra, request.GroupMail,
        request.AttachmentSlots, request.MailboxDoodadObjectId, request.GroupMoney, request.GroupRecipients));

    public void ListMail(byte mailboxKind, uint startIndex, uint sentCount, bool recover = false, bool test = false) =>
        Send(MailProtocol.CSListMail, MailProtocol.WriteList(mailboxKind, startIndex, sentCount, recover, test));
    public void ContinueMailList(byte mailboxKind) =>
        Send(MailProtocol.CSListMailContinue, MailProtocol.WriteListContinue(mailboxKind));

    public void ReadMail(bool isSent, long mailId) => Send(MailProtocol.CSReadMail, MailProtocol.WriteRead(isSent, mailId));
    public void TakeMailItem(long mailId, byte slotType, byte attachmentIndex) => Send(MailProtocol.CSTakeAttachmentItem, MailProtocol.WriteTakeAttachmentItem(mailId, slotType, attachmentIndex));
    public void TakeMailMoney(long mailId) => Send(MailProtocol.CSTakeAttachmentMoney, MailProtocol.WriteTakeAttachmentMoney(mailId));
    public void TakeMailAttachmentsSequentially(long mailId) => Send(MailProtocol.CSTakeAttachmentSequentially, MailProtocol.WriteTakeAttachmentSequentially(mailId));
    public void PayMailCharge(long mailId, bool autoUseAaPoint) => Send(MailProtocol.CSPayChargeMoney, MailProtocol.WritePayChargeMoney(mailId, autoUseAaPoint));
    public void DeleteMail(long mailId, bool isSent) => Send(MailProtocol.CSDeleteMail, MailProtocol.WriteDelete(mailId, isSent));
    public void ReturnMail(long mailId) => Send(MailProtocol.CSReturnMail, MailProtocol.WriteReturn(mailId));
    public void TakeAllMailItems(long mailId) => Send(MailProtocol.CSTakeAllAttachmentItem, MailProtocol.WriteTakeAllAttachmentItems(mailId));
    public void ReportSpamMail(ulong type, string sender) => Send(MailProtocol.CSReportSpamMail, MailProtocol.WriteReportSpamMail(type, sender));
    public void AcceptHeroDropoutComeback(ulong type = 0) => Send(MailProtocol.CSHeroDropoutComebackAccept, MailProtocol.WriteHeroDropoutComebackAccept(type));

    public void SendChat(short channelType, string message, short subType = 0, uint factionId = 0,
        string target = "", sbyte targetWorldId = 0, sbyte clientLocale = 0, sbyte languageType = 0, uint ability = 0) =>
        Send(SocialPacketWriter.SendChat(channelType, message, subType, factionId, target, targetWorldId, clientLocale, languageType, ability));

    public void SendWhisper(string targetName, string message, sbyte targetWorldId = 0, sbyte clientLocale = 0,
        sbyte languageType = 0, uint ability = 0) =>
        Send(SocialPacketWriter.SendWhisper(targetName, message, targetWorldId, clientLocale, languageType, ability));

    public void ExpressEmotion(uint characterUnitId, uint targetUnitId, uint emotionId) =>
        Send(CombatPacketWriters.ExpressEmotion(characterUnitId, targetUnitId, emotionId));

    public void JoinChatChannel(string name, string password = "", bool create = false) => Send(SocialPacketWriter.JoinUserChat(name, password, create));
    public void LeaveChatChannel(ulong handle) => Send(SocialPacketWriter.LeaveChat(handle));
    public void InviteParty(string targetName, int teamId = 0, sbyte teamRole = 1, sbyte worldId = 0) => Send(SocialPacketWriter.InviteParty(targetName, teamId, teamRole, worldId));
    public void ReplyPartyInvite(int teamId, bool party, ulong inviterId, string inviterName, bool accept, sbyte teamRole = 1, bool isArea = false, long logEventId = 0) => Send(SocialPacketWriter.ReplyPartyInvite(teamId, party, inviterId, inviterName, accept, teamRole, isArea, logEventId));
    public void LeaveParty(int teamId) => Send(SocialPacketWriter.LeaveParty(teamId));
    public void KickPartyMember(int teamId, ulong memberId) => Send(SocialPacketWriter.KickPartyMember(teamId, memberId));
    public void MakePartyLeader(int teamId, ulong memberId) => Send(SocialPacketWriter.MakePartyLeader(teamId, memberId));
    public void AddFriend(string targetName) => Send(SocialPacketWriter.AddFriend(targetName));
    public void AcceptFriend(ulong requesterCharacterId) => Send(SocialPacketWriter.AcceptFriend(requesterCharacterId));
    public void RemoveFriend(string friendName) => Send(SocialPacketWriter.RemoveFriend(friendName));

    public void RequestTrade(uint targetObjectId) => Send(SocialPacketWriter.RequestTrade(targetObjectId));
    public void AcceptTrade(uint requesterObjectId) => Send(SocialPacketWriter.AcceptTrade(requesterObjectId));
    public void RejectTrade(uint requesterObjectId, uint reason) => Send(SocialPacketWriter.RejectTrade(requesterObjectId, reason));
    public void CancelTrade(uint reason = 0) => Send(SocialPacketWriter.CancelTrade(reason));
    public void OfferTradeItem(byte slotType, byte slot, uint amount) => Send(SocialPacketWriter.OfferTradeItem(slotType, slot, amount));
    public void RemoveTradeItem(byte slotType, byte slot) => Send(SocialPacketWriter.RemoveTradeItem(slotType, slot));
    public void OfferTradeMoney(ulong amount) => Send(SocialPacketWriter.OfferTradeMoney(amount));
    public void LockTrade(bool locked = true) => Send(SocialPacketWriter.LockTrade(locked));
    public void ConfirmTrade() => Send(SocialPacketWriter.ConfirmTrade());

    public void Craft(int recipeId, uint stationObjectId, uint count) =>
        Send(CraftingProtocol.CSExecuteCraft, CraftingProtocol.WriteExecute(recipeId, stationObjectId, count));

    public void SearchAuction(AuctionSearchRequest request) => Send(AuctionProtocol.CSAuctionSearch, AuctionProtocol.WriteSearch(request));
    public void PostAuction(ulong itemId, long startPrice, long buyoutPrice, byte duration, int minimumStack = 1, int maximumStack = 1) =>
        Send(AuctionProtocol.CSAuctionPost, AuctionProtocol.WritePost(itemId, startPrice, buyoutPrice, duration, minimumStack, maximumStack));
    public void BidAuction(SerializedAuctionLot lot, AuctionBidRecord bid) => Send(AuctionProtocol.CSBidAuction, AuctionProtocol.WriteBid(lot, bid));
    public void CancelAuction(SerializedAuctionLot lot) => Send(AuctionProtocol.CSCancelAuction, AuctionProtocol.WriteCancel(lot));
    public void SearchMyAuctionBids(int page) =>
        Send(AuctionProtocol.CSAuctionMyBidList, AuctionProtocol.WriteMyBidList(page));
    public void RequestAuctionLowestPrice(uint templateId, byte grade) =>
        Send(AuctionProtocol.CSAuctionLowestPrice, AuctionProtocol.WriteLowestPrice(templateId, grade));
    public void SearchAuctionSoldRecords(uint templateId, byte grade, bool marketPriceUi) =>
        Send(AuctionProtocol.CSSearchAuctionSoldRecord,
            AuctionProtocol.WriteSoldRecordSearch(templateId, grade, marketPriceUi));

    /// <summary>Requests the Marketplace menu and its first pages (CSICSMenuListPacket has no body).</summary>
    public void RequestIcsMenuList() => Send(0x173, []);

    /// <summary>Places a slave using the exact CSSpawnSlave fixed-point position and item slot fields.</summary>
    public void SpawnSlave(uint slaveTemplateId, long positionX, long positionY, float positionZ, float yaw,
        ulong itemId, byte? slotType, byte? slot, bool hideSpawnEffect) =>
        Send(VehiclePacketWriters.SpawnSlave(slaveTemplateId, positionX, positionY, positionZ, yaw,
            itemId, slotType, slot, hideSpawnEffect));

    public void DespawnSlave(uint slaveObjectId) => Send(VehiclePacketWriters.DespawnSlave(slaveObjectId));
    public void BindSlave(short targetTimelineId, int skillType) => Send(VehiclePacketWriters.BindSlave(targetTimelineId, skillType));
    public void DismissSlave(short timelineId) => Send(VehiclePacketWriters.DiscardSlave(timelineId));
    public void BoardTransfer(short timelineId, byte attachPoint) => Send(VehiclePacketWriters.BoardingTransfer(timelineId, attachPoint));
    public void DismissMate(short timelineId) => Send(VehiclePacketWriters.RemoveMate(timelineId));
    public void MountMate(short timelineId, byte attachPoint, byte reason) => Send(VehiclePacketWriters.MountMate(timelineId, attachPoint, reason));
    public void UnmountMate(short timelineId, byte attachPoint, byte reason) => Send(VehiclePacketWriters.UnMountMate(timelineId, attachPoint, reason));

    public void ChangeSlaveTarget(uint targetObjectId, uint slaveObjectId) => Send(VehiclePacketWriters.ChangeSlaveTarget(targetObjectId, slaveObjectId));
    public void ChangeMateTarget(short timelineId, uint targetObjectId) => Send(VehiclePacketWriters.ChangeMateTarget(timelineId, targetObjectId));
    public void ChangeSlaveName(short timelineId, string name) => Send(VehiclePacketWriters.ChangeSlaveName(timelineId, name));
    public void ChangeMateName(short timelineId, string name) => Send(VehiclePacketWriters.ChangeMateName(timelineId, name));
    public void ChangeMateUserState(short timelineId, sbyte userState) => Send(VehiclePacketWriters.ChangeMateUserState(timelineId, userState));
    public void RepairSlaveItems(uint npcObjectId) => Send(VehiclePacketWriters.RepairSlaveItems(npcObjectId));
    public void RepairPetItems(uint npcObjectId) => Send(VehiclePacketWriters.RepairPetItems(npcObjectId));

    public void ChangeSlaveEquipment(ulong characterId, short timelineId, uint databaseSlaveId, bool bts,
        IReadOnlyList<VehicleEquipmentChangeEntry> entries) =>
        Send(VehiclePacketWriters.ChangeSlaveEquipment(characterId, timelineId, databaseSlaveId, bts, entries));

    public void ChangeMateEquipment(ulong owningCharacterId, short timelineId, uint passengerCharacterId, bool bts,
        IReadOnlyList<VehicleEquipmentChangeEntry> entries) =>
        Send(VehiclePacketWriters.ChangeMateEquipment(owningCharacterId, timelineId, passengerCharacterId, bts, entries));

    /// <summary>Sends caller-supplied signed ship controls without assigning keys, ranges, or cadence.</summary>
    public void SendShipRequest(uint objectId, uint time, byte flags, uint? scType, byte? phase,
        sbyte throttle, sbyte steering, sbyte extraFlags) =>
        Send(VehiclePacketWriters.MoveShipRequest(objectId, time, flags, scType, phase,
            throttle, steering, extraFlags));

    private void Send(ItemClientPacket packet) => Send(packet.Opcode, packet.Body);
    private void Send(CombatOutboundPacket packet) => Send(packet.Opcode, packet.Body);
    private void Send(OutboundSocialPacket packet) => Send(packet.Opcode, packet.Body);
    private void Send(ushort opcode, byte[] body) => _sendGame(opcode, body);

    private uint UseSkillId(uint itemTemplateId)
    {
        var item = _gameData.GetItem(itemTemplateId)
            ?? throw new ArgumentOutOfRangeException(nameof(itemTemplateId), itemTemplateId, "item template was not found in GameData");
        if (item.UseSkillId == 0)
            throw new InvalidOperationException($"item template {itemTemplateId} has no use_skill_id");
        return item.UseSkillId;
    }
}

/// <summary>Explicit values for the recovered CSStartSkill item-caster branch.</summary>
public sealed record ItemSkillCast(uint CasterUnitId, ulong ItemId, uint ItemTemplateId, byte Type1,
    ulong Type2, uint TargetUnitId);

/// <summary>Arguments for MailProtocol.WriteSend without concealing any native wire field.</summary>
public sealed record MailSendRequest(byte Kind, string Receiver, ulong ReceiverReference, string Title, string Text,
    byte AttachmentCount, ulong Money0, ulong Money1, ulong Money2, uint Money3, long Extra, bool GroupMail,
    IReadOnlyList<MailSlot> AttachmentSlots, uint MailboxDoodadObjectId, ulong GroupMoney = 0,
    IReadOnlyList<ulong>? GroupRecipients = null);
