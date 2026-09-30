#nullable enable
using Microsoft.Data.Sqlite;
using AAEmu.GodotViewer.Net;
using AAEmu.GodotViewer.Ui.X2.Scripting.World;

namespace AAEmu.GodotViewer.Ui.X2.Online;

/// <summary>Read-only client map icon metadata and the user's live Show Icons choices.</summary>
// the live markers (units in sight, quests, party) need the network bridge, so they live in the Godot/online part
public static partial class X2MapIconCatalog
{
    public static IReadOnlyList<X2MapMarker> Markers(X2WorldBridge bridge, IReadOnlySet<uint>? partyUnits = null,
        QuestState? questState = null, QuestOfferContext? offerContext = null)
    {
        var data = Data.Value;
        var result = new List<X2MapMarker>();
        var active = questState?.ActiveQuests.Values.GroupBy(q => q.TemplateId)
            .ToDictionary(group => group.Key, group => group.Max(q => q.Status))
            ?? new Dictionary<uint, byte>();
        var player = bridge.Get(bridge.PlayerId);
        var playerLevel = player?.Level ?? 0;
        var playerRace = RaceId(player?.Race);
        foreach (var unit in bridge.All)
        {
            if (unit.Type == "npc" && unit.TemplateId > 0)
            {
                var template = unit.TemplateId;
                var icon = data.Services.GetValueOrDefault(template);
                var category = "Npc";
                if (questState is not null && data.QuestGivers.TryGetValue(template, out var giverQuests))
                {
                    var offered = giverQuests.Where(id => IsOfferAvailable(id, playerLevel, playerRace, questState, offerContext))
                        .OrderByDescending(id => IsMain(data.Quests[id]))
                        .ThenByDescending(id => data.Quests[id].Priority).ThenBy(id => id).FirstOrDefault();
                    if (offered != 0 && data.Quests.TryGetValue(offered, out var offer))
                    {
                        icon = Pick(icon, IdFor(OfferIcon(offer)));
                        category = "GivenQuestStatic";
                    }
                }
                if (questState is not null && data.QuestReporters.TryGetValue(template, out var reportQuests))
                {
                    var ready = reportQuests.Where(id => active.GetValueOrDefault(id) == 3)
                        .OrderByDescending(id => data.Quests.TryGetValue(id, out var quest) && IsMain(quest))
                        .ThenByDescending(id => data.Quests.TryGetValue(id, out var quest) ? quest.Priority : 0)
                        .ThenBy(id => id).FirstOrDefault();
                    if (ready != 0)
                    {
                        data.Quests.TryGetValue(ready, out var offer);
                        icon = Pick(icon, IdFor(offer is null ? "quest_zone_completes" : CompleteIcon(offer)));
                        category = "CompletedQuest";
                    }
                }
                if (icon != 0) result.Add(Marker(unit.Id.ToString(), unit.X, unit.Y, icon, category));
            }
            else if (unit.Type == "character" && unit.Id != bridge.PlayerId && partyUnits?.Contains(unit.Id) == true)
            {
                var partyIcon = IdFor("party");
                if (partyIcon != 0) result.Add(Marker(unit.Id.ToString(), unit.X, unit.Y, partyIcon, "Npc"));
            }
        }
        return result;
    }

    private static bool IsMain(QuestOffer offer) => offer.DetailId is 2 or 3;
    private static string OfferIcon(QuestOffer offer) => offer.DetailId switch
    {
        8 or 11 => "quest_livelihood_gives",
        _ when IsMain(offer) => "quest_main_gives",
        _ when offer.Repeatable => "quest_repeat",
        _ => "quest_zone_gives",
    };
    private static string CompleteIcon(QuestOffer offer) => offer.DetailId switch
    {
        8 or 11 => "quest_livelihood_complete",
        _ when IsMain(offer) => "quest_main_completes",
        _ => "quest_zone_completes",
    };

    /// <summary>Checks a database quest offer against the live character, journal, and completion bitset.</summary>
    public static bool IsQuestOfferAvailable(uint questId, int playerLevel, string? playerRace, QuestState state,
        QuestOfferContext? context = null) =>
        IsOfferAvailable(questId, playerLevel, RaceId(playerRace), state, context);

    private static int RaceId(string? race) => race?.ToLowerInvariant() switch
    {
        "nuian" => 1, "fairy" => 2, "dwarf" => 3, "elf" => 4,
        "hariharan" => 5, "ferre" => 6, "returned" => 7, "warborn" => 8,
        _ => 0,
    };

    private static bool IsOfferAvailable(uint questId, int playerLevel, int playerRace, QuestState state,
        QuestOfferContext? context = null)
    {
        if (!Data.Value.Quests.TryGetValue(questId, out var offer) ||
            state.ActiveQuests.Values.Any(q => q.TemplateId == questId)) return false;
        if (playerLevel <= 0 || playerRace <= 0 || playerLevel < offer.MinLevel ||
            offer.MaxLevel > 0 && playerLevel > offer.MaxLevel) return false;
        if (offer.RaceMask != 255 && (offer.RaceMask & (1 << (playerRace - 1))) == 0) return false;
        // The server clears daily/weekly completion bits at the calendar boundary. Until then,
        // a completed calendar quest should not be offered again even if its template is repeatable.
        if ((!offer.Repeatable || IsCalendarQuest(offer.DetailId)) && IsCompleted(state, questId)) return false;
        if (!AcceptRequirementsMet(questId, playerLevel, playerRace, state, context)) return false;
        return offer.Prerequisites.Count == 0 || offer.Prerequisites.Any(required => IsCompleted(state, required));
    }

    private static bool IsCalendarQuest(int detailId) => detailId is 7 or 10 or 11 or 12 or 13 or 15;

    private static bool IsCompleted(QuestState state, uint questId)
    {
        if (state.ActiveQuests.Values.Any(q => q.TemplateId == questId && q.Status is 5 or 6)) return true;
        var blockId = questId / 64;
        var bit = (int)(questId % 64);
        return state.CompletedBlocks.TryGetValue(blockId, out var block) &&
            bit / 8 < block.Bits.Length && (block.Bits[bit / 8] & (1 << (bit % 8))) != 0;
    }

}
