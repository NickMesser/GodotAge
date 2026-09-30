#nullable enable

using AAEmu.GodotViewer.Net;
using Microsoft.Data.Sqlite;

namespace AAEmu.GodotViewer.Client;

/// <summary>
/// Read-only NPC quest-marker candidates from quest_components/quest_acts and their NPC detail rows.
/// Server-only race, faction, prerequisite, schedule and completion-bit filters are intentionally not guessed.
/// </summary>
public sealed class QuestMarkerCatalog : IDisposable
{
    private sealed record QuestInfo(uint QuestId, OverheadQuestKind Kind, int Level, int MinLevel, int MaxLevel, int Priority);
    private sealed record Candidate(QuestInfo Quest, uint ComponentId, uint NpcTemplateId);

    private readonly Dictionary<uint, List<Candidate>> _starts = [];
    private readonly Dictionary<uint, List<Candidate>> _talks = [];
    private readonly Dictionary<uint, List<Candidate>> _reports = [];
    private bool _disposed;

    public QuestMarkerCatalog(string databasePath)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(databasePath),
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
        };
        using var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        Load(connection, _starts, componentKind: 2, detailType: "QuestActConAcceptNpc", detailTable: "quest_act_con_accept_npcs");
        Load(connection, _talks, componentKind: 4, detailType: "QuestActObjTalk", detailTable: "quest_act_obj_talks");
        Load(connection, _reports, componentKind: 6, detailType: "QuestActConReportNpc", detailTable: "quest_act_con_report_npcs");
    }

    /// <summary>Returns the best local candidate for this NPC, applying level and active-quest state.</summary>
    public OverheadQuestMarker Resolve(uint npcTemplateId, int playerLevel, QuestState state)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(state);
        if (npcTemplateId == 0) return OverheadQuestMarker.None;

        var activeIds = state.ActiveQuests.Values.Select(q => q.TemplateId).ToHashSet();
        foreach (var active in state.ActiveQuests.Values)
        {
            if (!state.Progress.TryGetValue(active.InstanceId, out var progress)) continue;
            if (Contains(_reports, npcTemplateId, active.TemplateId, progress.CurrentComponentId, out var ready))
                return Marker(OverheadQuestState.Completable, ready.Quest.Kind);
            if (Contains(_talks, npcTemplateId, active.TemplateId, progress.CurrentComponentId, out var talk))
                return Marker(OverheadQuestState.InProgress, talk.Quest.Kind);
        }

        foreach (var active in state.ActiveQuests.Values)
            if (ContainsQuestNpc(_starts, npcTemplateId, active.TemplateId, out var start))
                return Marker(OverheadQuestState.InProgress, start.Quest.Kind);

        if (_starts.TryGetValue(npcTemplateId, out var candidates))
        {
            foreach (var candidate in candidates.OrderByDescending(c => c.Quest.Priority).ThenBy(c => c.Quest.QuestId))
            {
                if (activeIds.Contains(candidate.Quest.QuestId) || !IsWithinLevel(candidate.Quest, playerLevel)) continue;
                return Marker(OverheadQuestState.Available, candidate.Quest.Kind);
            }
        }
        return OverheadQuestMarker.None;
    }

    public void Dispose() => _disposed = true;

    private static OverheadQuestMarker Marker(OverheadQuestState state, OverheadQuestKind kind) =>
        new(state, kind, JournalFallback: true);

    private static bool IsWithinLevel(QuestInfo quest, int playerLevel)
    {
        var minimum = quest.MinLevel > 0 ? quest.MinLevel : quest.Level;
        return playerLevel >= minimum && (quest.MaxLevel <= 0 || playerLevel <= quest.MaxLevel);
    }

    private static bool Contains(Dictionary<uint, List<Candidate>> candidates, uint npcId, uint questId,
        uint componentId, out Candidate candidate)
    {
        if (candidates.TryGetValue(npcId, out var list))
        {
            candidate = list.FirstOrDefault(c => c.Quest.QuestId == questId && c.ComponentId == componentId)!;
            return candidate is not null;
        }
        candidate = null!;
        return false;
    }

    private static bool ContainsQuestNpc(Dictionary<uint, List<Candidate>> candidates, uint npcId, uint questId,
        out Candidate candidate)
    {
        if (candidates.TryGetValue(npcId, out var list))
        {
            candidate = list.FirstOrDefault(c => c.Quest.QuestId == questId)!;
            return candidate is not null;
        }
        candidate = null!;
        return false;
    }

    private static void Load(SqliteConnection connection, Dictionary<uint, List<Candidate>> output,
        int componentKind, string detailType, string detailTable)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $@"SELECT q.id, q.detail_id, q.level, q.min_level, q.max_level, q.priority,
       c.id, d.npc_id
FROM quest_contexts q
JOIN quest_components c ON c.quest_context_id=q.id
JOIN quest_acts a ON a.quest_component_id=c.id AND a.act_detail_type=$detailType
JOIN {detailTable} d ON d.id=a.act_detail_id
WHERE c.component_kind_id=$componentKind AND d.npc_id>0
  AND lower(cast(a.enable as text)) IN ('t','true','1')
  AND lower(cast(c.hide_quest_marker as text)) IN ('f','false','0')
  AND lower(cast(COALESCE(d.use_alias,0) as text)) IN ('f','false','0')";
        command.Parameters.AddWithValue("$detailType", detailType);
        command.Parameters.AddWithValue("$componentKind", componentKind);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var quest = new QuestInfo(
                checked((uint)reader.GetInt64(0)),
                (OverheadQuestKind)reader.GetInt32(1),
                reader.GetInt32(2), reader.GetInt32(3), reader.GetInt32(4), reader.GetInt32(5));
            var candidate = new Candidate(quest, checked((uint)reader.GetInt64(6)), checked((uint)reader.GetInt64(7)));
            if (!output.TryGetValue(candidate.NpcTemplateId, out var list)) output[candidate.NpcTemplateId] = list = [];
            list.Add(candidate);
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(QuestMarkerCatalog));
    }
}
