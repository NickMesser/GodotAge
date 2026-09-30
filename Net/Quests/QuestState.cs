#nullable enable

namespace AAEmu.GodotViewer.Net;

/// <summary>
/// Main-thread quest journal and NPC marker view. Apply decoded server events on the main thread in
/// receive order; call <see cref="ClearForCharacterLoad"/> before applying a fresh login snapshot.
/// </summary>
public sealed class QuestState
{
    private readonly Dictionary<long, QuestWireRecord> _active = new();
    private readonly Dictionary<uint, CompletedQuestBlock> _completed = new();
    private readonly Dictionary<uint, DoodadQuestMarkerEvent> _markers = new();
    private readonly Dictionary<long, QuestProgressState> _progress = new();

    public IReadOnlyDictionary<long, QuestWireRecord> ActiveQuests => _active;
    public IReadOnlyDictionary<uint, CompletedQuestBlock> CompletedBlocks => _completed;
    public IReadOnlyDictionary<uint, DoodadQuestMarkerEvent> DoodadMarkers => _markers;
    public IReadOnlyDictionary<long, QuestProgressState> Progress => _progress;
    public QuestContextFailedEvent? LastFailure { get; private set; }
    public QuestContextCompletedEvent? LastCompletion { get; private set; }
    public NamedQuestListRawEvent? LastNamedQuestList { get; private set; }

    public void ClearForCharacterLoad()
    {
        _active.Clear();
        _completed.Clear();
        _markers.Clear();
        _progress.Clear();
        LastFailure = null;
        LastCompletion = null;
        LastNamedQuestList = null;
    }

    public void Apply(IQuestProtocolEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        switch (e)
        {
            case QuestsSnapshotEvent snapshot:
                foreach (var quest in snapshot.Quests)
                    Upsert(quest);
                break;
            case CompletedQuestsEvent completed:
                foreach (var block in completed.Blocks)
                    _completed[block.Index] = block;
                break;
            case QuestContextStartedEvent started:
                Upsert(started.Quest);
                _progress[started.Quest.InstanceId] = new QuestProgressState(
                    started.ComponentId, started.Quest.Objectives, Array.Empty<uint>());
                break;
            case QuestContextUpdatedEvent updated:
                Upsert(updated.Quest);
                _progress[updated.Quest.InstanceId] = new QuestProgressState(
                    updated.ComponentId, updated.Quest.Objectives, updated.Parameters);
                break;
            case QuestContextFailedEvent failed:
                LastFailure = failed;
                break;
            case QuestContextCompletedEvent completed:
                LastCompletion = completed;
                RemoveQuest(completed.QuestId);
                break;
            case QuestContextResetEvent reset:
                if (reset.QuestId >= 0) RemoveQuestType((uint)reset.QuestId);
                ClearCompletedQuest(reset.QuestId);
                break;
            case QuestContextsResetEvent bulk:
                foreach (var questId in bulk.QuestIds)
                {
                    RemoveQuestType(questId);
                    ClearCompletedQuest(questId);
                }
                break;
            case DoodadQuestMarkerEvent marker:
                _markers[marker.DoodadObjectId] = marker;
                break;
            case NamedQuestListRawEvent named:
                LastNamedQuestList = named;
                break;
        }
    }

    private void Upsert(QuestWireRecord quest)
    {
        _active[quest.InstanceId] = quest;
        if (!_progress.ContainsKey(quest.InstanceId))
            _progress[quest.InstanceId] = new QuestProgressState(0, quest.Objectives, Array.Empty<uint>());
    }

    private void RemoveQuest(long id)
    {
        _active.Remove(id);
        _progress.Remove(id);
    }

    private void RemoveQuestType(uint questId)
    {
        foreach (var instanceId in _active.Where(entry => entry.Value.TemplateId == questId)
                     .Select(entry => entry.Key).ToArray())
            RemoveQuest(instanceId);
    }

    private void ClearCompletedQuest(long questId)
    {
        if (questId < 0 || questId > uint.MaxValue) return;
        var blockId = (uint)questId / 64;
        var bit = (int)((uint)questId % 64);
        if (!_completed.TryGetValue(blockId, out var block) || bit / 8 >= block.Bits.Length) return;
        var bits = (byte[])block.Bits.Clone();
        bits[bit / 8] &= (byte)~(1 << (bit % 8));
        _completed[blockId] = block with { Bits = bits };
    }
}

public sealed record QuestProgressState(
    uint CurrentComponentId,
    IReadOnlyList<uint> Objectives,
    IReadOnlyList<uint> Parameters);
