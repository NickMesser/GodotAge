#nullable enable
using Microsoft.Data.Sqlite;
using AAEmu.GodotViewer.Net;
using AAEmu.GodotViewer.Ui.X2.Scripting.World;

namespace AAEmu.GodotViewer.Ui.X2.Online;

/// <summary>What the accept-requirement check needs to know about the player besides the quest journal.</summary>
public sealed record QuestOfferContext(uint FactionId, IReadOnlySet<uint> Buffs, Func<uint, bool> OwnsItem,
    Func<uint, bool> EquipsItem, bool IsResident = false);

// The start-quest list of an NPC (x2game-dev.dll FUN_397c71b0, behind X2Quest:GetNpcQuestContextCountStart) keeps a quest
// only if, besides the level/race window and the journal state, the conditions of its accept component hold: the
// unit_reqs rows owned by that quest_components row (owner_type 'QuestComponent', component_kind_id 2), all of them, or
// any of them when the component has or_unit_reqs. Example (auctioneer 10857, Xyz level 17): Strange Traders 4439 needs
// complete_quest_context 4424, The Daru's Obsession 8006 needs buff 8321, Visit a Community Center 8433 needs
// is_resident; Earning a Mount/Glider only exclude completed quests and faction 104, so only those two are offered.
public static partial class X2MapIconCatalog
{
    private sealed record AcceptReq(int Kind, long Value1, long Value2);
    private sealed record AcceptReqs(bool Any, List<AcceptReq> Reqs);

    private static readonly Lazy<(Dictionary<uint, AcceptReqs> Reqs, Dictionary<uint, uint> MotherFaction)> ReqData = new(LoadReqs);

    private static (Dictionary<uint, AcceptReqs>, Dictionary<uint, uint>) LoadReqs()
    {
        var reqs = new Dictionary<uint, AcceptReqs>();
        var mothers = new Dictionary<uint, uint>();
        try
        {
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = X2DbLists.DefaultDatabase, Mode = SqliteOpenMode.ReadOnly }.ToString());
            db.Open();
            using (var cmd = db.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT c.quest_context_id, c.or_unit_reqs, r.kind_id, r.value1, r.value2
                    FROM unit_reqs r JOIN quest_components c ON c.id=r.owner_id
                    WHERE r.owner_type='QuestComponent' AND r.enable='t' AND c.component_kind_id=2
                    """;
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var quest = (uint)r.GetInt64(0);
                    if (!reqs.TryGetValue(quest, out var entry))
                        reqs[quest] = entry = new AcceptReqs(!r.IsDBNull(1) && r.GetString(1) == "t", []);
                    entry.Reqs.Add(new AcceptReq(r.GetInt32(2), r.IsDBNull(3) ? 0 : r.GetInt64(3), r.IsDBNull(4) ? 0 : r.GetInt64(4)));
                }
            }
            using (var cmd = db.CreateCommand())
            {
                cmd.CommandText = "SELECT id, mother_id FROM system_factions";
                using var r = cmd.ExecuteReader();
                while (r.Read()) mothers[(uint)r.GetInt64(0)] = r.IsDBNull(1) ? 0 : (uint)r.GetInt64(1);
            }
        }
        catch { /* no database: no accept requirements */ }
        return (reqs, mothers);
    }

    private static bool AcceptRequirementsMet(uint questId, int playerLevel, int playerRace, QuestState state, QuestOfferContext? context)
    {
        if (!ReqData.Value.Reqs.TryGetValue(questId, out var entry) || entry.Reqs.Count == 0) return true;
        return entry.Any ? entry.Reqs.Any(Met) : entry.Reqs.All(Met);

        bool Met(AcceptReq req)
        {
            var id = (uint)req.Value1;
            var faction = context?.FactionId ?? 0;
            var mother = faction == 0 ? 0 : ReqData.Value.MotherFaction.GetValueOrDefault(faction) is var m && m != 0 ? m : faction;
            return req.Kind switch
            {
                1 => playerLevel >= req.Value1 && (req.Value2 == 0 || playerLevel <= req.Value2),       // level
                69 => playerLevel <= req.Value1,                                                          // max_level
                3 => req.Value1 == 0 || playerRace == req.Value1,                                         // race
                9 => context?.EquipsItem(id) ?? false,                                                    // equip_item
                10 => context?.OwnsItem(id) ?? false,                                                     // own_item
                74 => !(context?.OwnsItem(id) ?? false),                                                  // own_item_not
                15 => context?.Buffs.Contains(id) ?? false,                                               // buff
                22 => !(context?.Buffs.Contains(id) ?? false),                                            // nobuff
                31 => IsCompleted(state, id),                                                             // complete_quest_context
                36 => !IsCompleted(state, id),                                                            // except_complete_quest_context
                32 => IsInProgress(state, id),                                                            // progress_quest_context
                72 => !IsInProgress(state, id),                                                           // except_progress_quest_context
                33 => IsReady(state, id),                                                                 // ready_quest_context
                73 => !IsReady(state, id),                                                                // except_ready_quest_context
                37 => IsReady(state, id) || IsCompleted(state, id),                                       // precomplete_quest_context
                42 or 56 => faction == 0 || mother == id,                                                 // mother_faction(_only)
                59 => faction == 0 || mother != id,                                                       // mother_faction_only_not
                40 or 55 => faction == 0 || faction == id,                                                // faction_match(_only)
                58 => faction == 0 || faction != id,                                                      // faction_match_only_not
                91 => context?.IsResident ?? false,                                                       // is_resident
                _ => true, // conditions the client cannot evaluate here (actability, dominion, hero, ...) do not hide a quest
            };
        }
    }

    private static bool IsInProgress(QuestState state, uint questId) =>
        state.ActiveQuests.Values.Any(q => q.TemplateId == questId && q.Status is not (3 or 5 or 6));

    private static bool IsReady(QuestState state, uint questId) =>
        state.ActiveQuests.Values.Any(q => q.TemplateId == questId && q.Status == 3);
}
