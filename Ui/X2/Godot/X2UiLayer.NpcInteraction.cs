#nullable enable
using AAEmu.GodotViewer.Net;
using AAEmu.GodotViewer.Lua;
using Microsoft.Data.Sqlite;

namespace AAEmu.GodotViewer.Ui.X2;

/// <summary>NPC options resolved by the host after the server starts an interaction.</summary>
/// <param name="NpcUnitId">Live NPC unit id from SCNpcInteractionSkillList.</param>
/// <param name="NpcTemplateId">Template id for merchant goods and localized NPC content.</param>
/// <param name="NpcObjectId">Object id used by quest and shop requests (SCNpcInteractionSkillList.ObjectId).</param>
/// <param name="StartQuests">Quest context ids offered by this NPC.</param>
/// <param name="CompleteQuests">Ready quest context ids reportable to this NPC.</param>
/// <param name="OpenStore">Open the merchant content after ShopState has applied the server response.</param>
/// <param name="CanRepair">Open the stock repair package when the server offers repair skill 12086.</param>
/// <param name="StoreOpenType">SHOP_OPEN_* value consumed by store.lua.</param>
/// <param name="TalkBubbleId">Optional npc_chat_bubbles.id supplied by the interaction resolver.</param>
/// <param name="NpcName">Visible NPC name for the stock chat bubble.</param>
public sealed record NpcInteractionView(
    uint NpcUnitId, uint NpcTemplateId, uint NpcObjectId,
    IReadOnlyList<uint>? StartQuests = null, IReadOnlyList<uint>? CompleteQuests = null,
    bool OpenStore = false, bool CanRepair = false, int StoreOpenType = 0, uint TalkBubbleId = 0, string NpcName = "",
    bool OpenAuction = false, bool OpenWarehouse = false);

public partial class X2UiLayer
{
    private uint _activeNpcUnitId;
    private const int StoreContentId = 60; // UIC_STORE in X2ApiData.g.cs
    private const int AuctionContentId = 58; // UIC_AUCTION
    private const int BankContentId = 59; // UIC_BANK
    private const int RepairContentId = 70; // UIC_ITEM_REPAIR in X2ApiData.g.cs

    /// <summary>
    /// Called by the world host after OnlineSession has applied the NPC interaction response. The host
    /// supplies quest option ids from its interaction resolver; this method never sends CSInteractNPC.
    /// </summary>
    public void BeginNpcInteraction(NpcInteractionView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        if (_session.Root is not { } root) return;
        _activeNpcUnitId = view.NpcUnitId;
        _protocolItemsData?.SetInteractionNpc(view.NpcUnitId, view.NpcTemplateId);
        _protocolItemsData?.SetRepairAuthority(view.CanRepair);
        _protocolQuestData?.SetNpcInteraction(view.NpcObjectId, view.StartQuests, view.CompleteQuests);
        if (view.TalkBubbleId != 0 && ReadNpcTalk(view.TalkBubbleId) is { Length: > 0 } talk)
            ShowNpcTalk(talk, view.NpcName, view.NpcUnitId);
        var openedService = false;
        if (view.OpenStore && LiveSession?.ShopState.OpenInteraction is { } interaction &&
            interaction.NpcUnitId == view.NpcUnitId)
        {
            _session.WorldCore.ShowContent(StoreContentId, true, [new LuaTable
            {
                ["openType"] = (double)view.StoreOpenType,
                ["direct"] = true,
            }]);
            openedService = true;
        }
        if (view.OpenAuction)
        {
            _session.WorldCore.ShowContent(AuctionContentId, true, []);
            Emit($"[x2] npc service: auction (UIC_AUCTION {AuctionContentId}) shown, auctionWindow visible=" +
                 $"{root.AllWidgets().Any(w => w.Id == "auctionWindow" && w.IsEffectivelyVisible())}");
            openedService = true;
        }
        if (view.OpenWarehouse)
        {
            _session.WorldCore.ShowContent(BankContentId, true, []);
            openedService = true;
        }
        if (view.CanRepair)
        {
            _session.WorldCore.ShowContent(RepairContentId, true, []);
            openedService = true;
        }
        if (!openedService && view.CompleteQuests is { Count: > 0 })
            root.DispatchEvent("NPC_INTERACTION_START", "quest", "complete", (double)view.NpcObjectId);
        else if (!openedService && view.StartQuests is { Count: > 0 })
            root.DispatchEvent("NPC_INTERACTION_START", "quest", "start", (double)view.NpcObjectId);
    }

    /// <summary>Closes the active NPC session using CSLeaveInteraction.</summary>
    public void LeaveNpcInteraction()
    {
        if (_activeNpcUnitId == 0) return;
        _protocolActions?.EndNpcInteraction(_activeNpcUnitId);
        _activeNpcUnitId = 0;
        _protocolItemsData?.SetRepairAuthority(false);
        _session.WorldCore.ShowContent(RepairContentId, false, []);
        _protocolQuestData?.SetNpcInteraction(0, null, null);
        _session.Root?.DispatchEvent("NPC_INTERACTION_END");
    }

    private void OnNpcDialogue(NpcDialogueEvent dialogue)
    {
        if (dialogue.Message.Length == 0) return;
        ShowNpcTalk(dialogue.Message, dialogue.NpcName, dialogue.NpcUnitId);
    }

    private void ShowNpcTalk(string text, string name, uint npcUnitId) =>
        _session.Root?.DispatchEvent("CHAT_MSG_QUEST", text, name, (double)npcUnitId,
            false, 0d, 5000d, 500d, 0d, 0d, false);

    private string? ReadNpcTalk(uint bubbleId)
    {
        if (!File.Exists(_gameDatabase)) return null;
        using var db = new SqliteConnection($"Data Source={_gameDatabase};Mode=ReadOnly");
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT COALESCE(NULLIF(l.en_us, ''), b.bubble)
            FROM npc_chat_bubbles b
            LEFT JOIN localized_texts l ON l.tbl_name='npc_chat_bubbles'
                AND l.tbl_column_name='bubble' AND l.idx=b.id
            WHERE b.id=$id LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$id", bubbleId);
        return cmd.ExecuteScalar() as string;
    }

    private void OnNpcInteractionEnded(NpcInteractionEndedEvent _)
    {
        _activeNpcUnitId = 0;
        _protocolItemsData?.SetRepairAuthority(false);
        _session.WorldCore.ShowContent(RepairContentId, false, []);
    }
}
