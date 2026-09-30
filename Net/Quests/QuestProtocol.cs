#nullable enable

namespace AAEmu.GodotViewer.Net;

/// <summary>Marker used by QuestState without coupling it to individual packet records.</summary>
public interface IQuestProtocolEvent
{
}

public sealed record QuestWireRecord(
    long InstanceId,
    uint TemplateId,
    byte Status,
    IReadOnlyList<uint> Objectives,
    bool IsCheckSet,
    uint ObjectId,
    uint TypeId,
    uint ObjectId2,
    uint ObjectId3,
    int LeftTimeMilliseconds,
    uint TimedComponentId,
    long DoodadId,
    long AcceptTime,
    byte AcceptorKind,
    uint AcceptorId);

public sealed record CompletedQuestBlock(uint Index, byte[] Bits);

public sealed record QuestsSnapshotEvent(IReadOnlyList<QuestWireRecord> Quests) : GameEvent, IQuestProtocolEvent;
public sealed record CompletedQuestsEvent(IReadOnlyList<CompletedQuestBlock> Blocks) : GameEvent, IQuestProtocolEvent;
public sealed record QuestContextStartedEvent(QuestWireRecord Quest, uint ComponentId) : GameEvent, IQuestProtocolEvent;
public sealed record QuestContextUpdatedEvent(QuestWireRecord Quest, uint ComponentId, IReadOnlyList<uint> Parameters) : GameEvent, IQuestProtocolEvent;
public sealed record QuestContextFailedEvent(uint QuestId, byte Reason) : GameEvent, IQuestProtocolEvent;
public sealed record QuestContextCompletedEvent(int QuestId, int ComponentId) : GameEvent, IQuestProtocolEvent;
public sealed record QuestContextResetEvent(int QuestId) : GameEvent, IQuestProtocolEvent;
public sealed record QuestContextsResetEvent(IReadOnlyList<uint> QuestIds) : GameEvent, IQuestProtocolEvent;
public sealed record DoodadQuestMarkerEvent(uint DoodadObjectId, uint QuestId, bool IsCompletionMarker) : GameEvent, IQuestProtocolEvent;

/// <summary>
/// SCQuestList (0x195) is distinct from the active quest snapshot. Its nested row layout is not
/// described by the recovered declaration, so the rows remain explicit raw bytes.
/// </summary>
public sealed record NamedQuestListRawEvent(string Name, uint Count, byte[] RawRows) : GameEvent, IQuestProtocolEvent;

/// <summary>Quest packet layouts for the 10.0.2.13 game protocol.</summary>
public static class QuestProtocol
{
    public const ushort SCQuests = 0x132;
    public const ushort SCCompletedQuests = 0x133;
    public const ushort SCDoodadQuestAccept = 0x153;
    public const ushort SCQuestContextFailed = 0x18B;
    public const ushort SCQuestContextStarted = 0x18C;
    public const ushort SCQuestContextUpdated = 0x18E;
    public const ushort SCQuestContextCompleted = 0x190;
    public const ushort SCQuestContextReset = 0x191;
    public const ushort SCQuestContextResetBulk = 0x192;
    public const ushort SCDoodadCompleteQuest = 0x193;
    public const ushort SCQuestList = 0x195;

    public const ushort CSStartQuestContext = 0x117;
    public const ushort CSCompleteQuestContext = 0x118;
    public const ushort CSDropQuestContext = 0x119;
    public const ushort CSQuestTalkMade = 0x11C;

    public static IQuestProtocolEvent Parse(ushort opcode, byte[] body)
    {
        var r = new WireReader(body);
        return opcode switch
        {
            SCQuests => ParseQuests(r),
            SCCompletedQuests => ParseCompleted(r),
            SCDoodadQuestAccept => new DoodadQuestMarkerEvent(r.Bc(), r.U32(), false),
            SCQuestContextFailed => new QuestContextFailedEvent(r.U32(), r.U8()),
            SCQuestContextStarted => new QuestContextStartedEvent(ReadQuest(r), r.U32()),
            SCQuestContextUpdated => ParseUpdated(r),
            SCQuestContextCompleted => new QuestContextCompletedEvent(r.S32(), r.S32()),
            SCQuestContextReset => new QuestContextResetEvent(r.S32()),
            SCQuestContextResetBulk => ParseResetBulk(r),
            SCDoodadCompleteQuest => new DoodadQuestMarkerEvent(r.Bc(), r.U32(), true),
            SCQuestList => new NamedQuestListRawEvent(r.Str(), r.U32(), r.Rest()),
            _ => throw new ArgumentOutOfRangeException(nameof(opcode), opcode, "not a quest opcode"),
        };
    }

    private static QuestsSnapshotEvent ParseQuests(WireReader r)
    {
        var count = CheckedCount(r.S32(), 20, "quest");
        var quests = new List<QuestWireRecord>(count);
        for (var i = 0; i < count; i++)
            quests.Add(ReadQuest(r));
        return new QuestsSnapshotEvent(quests);
    }

    private static CompletedQuestsEvent ParseCompleted(WireReader r)
    {
        var count = CheckedCount(r.S32(), 100_000, "completed quest block");
        var blocks = new List<CompletedQuestBlock>(count);
        for (var i = 0; i < count; i++)
            blocks.Add(new CompletedQuestBlock(r.U32(), r.Bytes(8)));
        return new CompletedQuestsEvent(blocks);
    }

    private static QuestContextUpdatedEvent ParseUpdated(WireReader r)
    {
        var quest = ReadQuest(r);
        var values = r.Pisc(10);
        return new QuestContextUpdatedEvent(quest, values[0], values.Skip(1).ToArray());
    }

    private static QuestContextsResetEvent ParseResetBulk(WireReader r)
    {
        var count = r.U8();
        var ids = new List<uint>(count);
        for (var i = 0; i < count; i++)
            ids.Add(r.U32());
        return new QuestContextsResetEvent(ids);
    }

    private static QuestWireRecord ReadQuest(WireReader r) => new(
        r.S64(), r.U32(), r.U8(), r.Pisc(10), r.Bool(), r.Bc(), r.U32(), r.Bc(), r.Bc(),
        r.S32(), r.U32(), r.S64(), r.S64(), r.U8(), r.U32());

    private static int CheckedCount(int count, int maximum, string label)
    {
        if (count < 0 || count > maximum)
            throw new WireException($"invalid {label} count {count}");
        return count;
    }

    public static byte[] WriteStart(uint questContextId, uint npcObjectId = 0, uint doodadObjectId = 0, uint sphereId = 0) =>
        new WireWriter().U32(questContextId).Bc(npcObjectId).Bc(doodadObjectId).U32(sphereId).ToArray();

    public static byte[] WriteComplete(uint questContextId, uint npcObjectId = 0, uint doodadObjectId = 0, int selectedReward = 0) =>
        new WireWriter().U32(questContextId).Bc(npcObjectId).Bc(doodadObjectId).S32(selectedReward).ToArray();

    public static byte[] WriteDrop(uint questId) => new WireWriter().U32(questId).ToArray();

    public static byte[] WriteTalk(uint npcObjectId, uint questContextId, uint componentId, uint actionId) =>
        new WireWriter().Bc(npcObjectId).U32(questContextId).U32(componentId).U32(actionId).ToArray();
}
