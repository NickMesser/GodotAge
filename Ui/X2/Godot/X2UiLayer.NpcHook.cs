#nullable enable
using Microsoft.Data.Sqlite;
using AAEmu.GodotViewer.Ui.X2.Online;

namespace AAEmu.GodotViewer.Ui.X2;

// The session's NPC interaction reply (after F / right-click on an NPC) opens the client's NPC windows: the quests the NPC
// offers or takes back (quest_act_con_accept_npcs / quest_act_con_report_npcs for its template) and its store.
public partial class X2UiLayer
{
    private const uint RepairNpcSkillId = 12086;
    // NPC service skills offered in SCNpcInteractionSkillList (skills table: 12083 Use Auctioneer, 13238 Use Warehouse);
    // AAEmu NpcInteractionRules offers UseAuctioneer for npcs.auctioneer templates
    private const uint AuctionNpcSkillId = 12083;
    private const uint WarehouseNpcSkillId = 13238;

    private void OnNpcInteractionStarted(Net.NpcInteractionSkillsEvent e)
    {
        var template = Bridge?.Get(e.NpcUnitId)?.TemplateId ?? 0;
        Emit($"[x2] npc interaction {e.NpcUnitId} template {template} object {e.ObjectId} skills [{string.Join(' ', e.SkillIds)}] interactable {e.Interactable}");
        var ready = LiveSession?.QuestState.ActiveQuests.Values.Where(q => q.Status == 3)
            .Select(q => q.TemplateId).ToHashSet() ?? [];
        var (accept, report) = NpcQuests(template);
        var player = Bridge is null ? null : Bridge.Get(Bridge.PlayerId);
        var offerContext = QuestOfferContext();
        uint[] eligible = LiveSession is null || player is null ? [] : accept.Where(q =>
            X2MapIconCatalog.IsQuestOfferAvailable(q, player.Level, player.Race, LiveSession.QuestState, offerContext)).ToArray();
        var name = Bridge?.Get(e.NpcUnitId)?.Name ?? "";
        BeginNpcInteraction(new NpcInteractionView(
            e.NpcUnitId, template, e.ObjectId != 0 ? e.ObjectId : e.NpcUnitId, // quest requests carry the NPC's object (unit) id; the skill list's ObjectId is 0 for NPCs
            StartQuests: eligible,
            CompleteQuests: report.Where(ready.Contains).ToArray(),
            OpenStore: NpcHasStore(template),
            CanRepair: e.SkillIds.Contains(RepairNpcSkillId),
            OpenAuction: e.SkillIds.Contains(AuctionNpcSkillId),
            OpenWarehouse: e.SkillIds.Contains(WarehouseNpcSkillId),
            NpcName: name));
    }

    /// <summary>The player facts the accept requirements (unit_reqs) of a quest need: faction, buffs, owned/equipped items.</summary>
    private QuestOfferContext? QuestOfferContext()
    {
        if (LiveSession is not { } session || Bridge is not { } bridge || bridge.Get(bridge.PlayerId) is not { } player) return null;
        var buffs = session.CombatState.TryGetUnit(bridge.PlayerId, out var combat) && combat is not null
            ? combat.Buffs.Values.Select(b => b.BuffId).ToHashSet() : new HashSet<uint>();
        var items = session.InventoryState.Items;
        return new QuestOfferContext(player.FactionId, buffs,
            template => items.Any(i => i.Key.SlotType is (byte)Net.InventorySlotType.Inventory or (byte)Net.InventorySlotType.Equipment
                && i.Value.TemplateId == template),
            template => items.Any(i => i.Key.SlotType == (byte)Net.InventorySlotType.Equipment && i.Value.TemplateId == template));
    }

    private static readonly Dictionary<uint, (uint[] Accept, uint[] Report)> NpcQuestCache = [];

    private (uint[] Accept, uint[] Report) NpcQuests(uint template)
    {
        if (template == 0) return ([], []);
        if (NpcQuestCache.TryGetValue(template, out var cached)) return cached;
        uint[] Query(string table, string detailType, int componentKind)
        {
            try
            {
                using var db = new SqliteConnection($"Data Source={_gameDatabase};Mode=ReadOnly");
                db.Open();
                using var cmd = db.CreateCommand();
                cmd.CommandText = $"select distinct c.quest_context_id from {table} n join quest_acts a on a.act_detail_id = n.id and a.act_detail_type = $type " +
                                  "join quest_components c on c.id = a.quest_component_id where n.npc_id = $npc " +
                                  "and a.enable = 't' and n.use_alias = 'f' and c.component_kind_id = $kind order by c.quest_context_id";
                cmd.Parameters.AddWithValue("$type", detailType);
                cmd.Parameters.AddWithValue("$npc", (long)template);
                cmd.Parameters.AddWithValue("$kind", componentKind);
                using var r = cmd.ExecuteReader();
                var list = new List<uint>();
                while (r.Read()) list.Add((uint)r.GetInt64(0));
                return list.ToArray();
            }
            catch (Exception ex)
            {
                Emit($"[x2] npc quest lookup failed: {ex.Message}");
                return [];
            }
        }
        var result = (Query("quest_act_con_accept_npcs", "QuestActConAcceptNpc", 2), Query("quest_act_con_report_npcs", "QuestActConReportNpc", 6));
        NpcQuestCache[template] = result;
        return result;
    }

    private bool NpcHasStore(uint template)
    {
        if (template == 0) return false;
        try
        {
            using var db = new SqliteConnection($"Data Source={_gameDatabase};Mode=ReadOnly");
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "select count(*) from npcs where id = $npc and merchant = 't'";
            cmd.Parameters.AddWithValue("$npc", (long)template);
            return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
        }
        catch (Exception) { return false; }
    }
}
