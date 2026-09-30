#nullable enable

namespace AAEmu.GodotViewer.Net;

public interface IAssignmentProtocolEvent { }

public sealed record AchievementProgress(uint Id, uint Amount, long Complete, bool ItemSent = false, bool ItemSentByMail = false);
public sealed record AchievementsEvent(IReadOnlyList<AchievementProgress> Entries) : GameEvent, IAssignmentProtocolEvent;
public sealed record AchievementChangedEvent(uint Id, int Amount) : GameEvent, IAssignmentProtocolEvent;
public sealed record AchievementCompletedEvent(uint Id, long Complete) : GameEvent, IAssignmentProtocolEvent;
public sealed record AchievementResetEvent(uint Id, int Amount, long Complete) : GameEvent, IAssignmentProtocolEvent;
public sealed record AchievementItemSentEvent(uint Id, bool ByMail) : GameEvent, IAssignmentProtocolEvent;
public sealed record TodayAssignmentChangedEvent(int StepId, int GroupId, int QuestContextId, sbyte Status, bool Init) : GameEvent, IAssignmentProtocolEvent;
public sealed record TodayAssignmentGoalEvent(int Type) : GameEvent, IAssignmentProtocolEvent;
public sealed record TodayAssignmentItemSentEvent(uint Goal, bool ByMail) : GameEvent, IAssignmentProtocolEvent;
public sealed record TodayAssignmentResetCountEvent(uint Count, uint CountType) : GameEvent, IAssignmentProtocolEvent;
public sealed record TodayAssignmentAcceptAllEvent(ushort ErrorMessage) : GameEvent, IAssignmentProtocolEvent;

public static class AchievementProtocol
{
    public const ushort SCAchievements = 0x27F;
    public const ushort SCAchievementChanged = 0x280;
    public const ushort SCAchievementCompleted = 0x281;
    public const ushort SCAchievementReset = 0x282;
    public const ushort SCAchievementItemSent = 0x283;
    public const ushort SCTodayAssignmentItemSent = 0x284;
    public const ushort SCTodayAssignmentChanged = 0x285;
    public const ushort SCTodayAssignmentGoal = 0x286;
    public const ushort SCTodayAssignmentResetCount = 0x328;
    public const ushort SCTodayAssignmentAcceptAll = 0x329;

    public const ushort CSRequestTodayAssignment = 0x120;
    public const ushort CSTodayAssignmentAcceptAll = 0x121;
    public const ushort CSResetTodayAssignment = 0x122;
    public const ushort CSChangeAppellation = 0x14C;

    public static bool Owns(ushort opcode) => opcode is >= SCAchievements and <= SCTodayAssignmentGoal or
        SCTodayAssignmentResetCount or SCTodayAssignmentAcceptAll;

    public static GameEvent Parse(ushort opcode, byte[] body)
    {
        var r = new WireReader(body);
        GameEvent result = opcode switch
        {
            SCAchievements => ParseList(r),
            SCAchievementChanged => new AchievementChangedEvent(r.U32(), r.S32()),
            SCAchievementCompleted => new AchievementCompletedEvent(r.U32(), r.S64()),
            SCAchievementReset => new AchievementResetEvent(r.U32(), r.S32(), r.S64()),
            SCAchievementItemSent => new AchievementItemSentEvent(r.U32(), r.Bool()),
            SCTodayAssignmentItemSent => new TodayAssignmentItemSentEvent(r.U32(), r.Bool()),
            SCTodayAssignmentChanged => new TodayAssignmentChangedEvent(r.S32(), r.S32(), r.S32(), r.S8(), r.Bool()),
            SCTodayAssignmentGoal => new TodayAssignmentGoalEvent(r.S32()),
            SCTodayAssignmentResetCount => new TodayAssignmentResetCountEvent(r.U32(), r.U32()),
            SCTodayAssignmentAcceptAll => new TodayAssignmentAcceptAllEvent(r.U16()),
            _ => throw new ArgumentOutOfRangeException(nameof(opcode), opcode, "not an achievement or assignment opcode"),
        };
        if (r.Remaining != 0) throw new WireException($"assignment opcode {opcode:X3} has {r.Remaining} trailing bytes");
        return result;
    }

    private static AchievementsEvent ParseList(WireReader r)
    {
        var count = r.S32();
        if (count is < 0 or > 50) throw new WireException($"achievement packet count {count} exceeds 50");
        var entries = new List<AchievementProgress>(count);
        for (var i = 0; i < count; i++) entries.Add(new AchievementProgress(r.U32(), r.U32(), r.S64()));
        return new AchievementsEvent(entries);
    }

    public static byte[] WriteRequestTodayAssignment(uint realStep, sbyte request) =>
        new WireWriter().U32(realStep).S8(request).ToArray();
    public static byte[] WriteResetTodayAssignment(uint realStep, ulong moneyAmount) =>
        new WireWriter().U32(realStep).U64(moneyAmount).ToArray();
    public static byte[] WriteAcceptAllTodayAssignment(sbyte todayType, IReadOnlyList<uint> realSteps)
    {
        ArgumentNullException.ThrowIfNull(realSteps);
        if (realSteps.Count > 64) throw new ArgumentOutOfRangeException(nameof(realSteps));
        var writer = new WireWriter().S8(todayType).U32((uint)realSteps.Count);
        foreach (var step in realSteps) writer.U32(step);
        return writer.ToArray();
    }
}

public sealed class AssignmentPacketParserFamily : IPacketParserFamily
{
    public bool TryParse(ushort opcode, byte[] body, out IReadOnlyList<GameEvent> events)
    {
        if (!AchievementProtocol.Owns(opcode)) { events = []; return false; }
        events = [AchievementProtocol.Parse(opcode, body)];
        return true;
    }
}

public sealed class AchievementState
{
    private readonly Dictionary<uint, AchievementProgress> _progress = [];
    public IReadOnlyDictionary<uint, AchievementProgress> ProgressById => _progress;
    public IReadOnlyCollection<uint> CompletedIds => _progress.Values.Where(x => x.Complete != 0).Select(x => x.Id).ToArray();
    public void Clear() => _progress.Clear();
    public void Apply(IAssignmentProtocolEvent value)
    {
        switch (value)
        {
            case AchievementsEvent list:
                foreach (var entry in list.Entries) _progress[entry.Id] = entry;
                break;
            case AchievementChangedEvent changed:
                _progress[changed.Id] = Current(changed.Id) with { Amount = unchecked((uint)Math.Max(0, changed.Amount)) };
                break;
            case AchievementCompletedEvent completed:
                _progress[completed.Id] = Current(completed.Id) with { Complete = completed.Complete };
                break;
            case AchievementResetEvent reset:
                _progress[reset.Id] = Current(reset.Id) with { Amount = unchecked((uint)Math.Max(0, reset.Amount)), Complete = reset.Complete };
                break;
            case AchievementItemSentEvent sent:
                _progress[sent.Id] = Current(sent.Id) with { ItemSent = true, ItemSentByMail = sent.ByMail };
                break;
        }
    }
    private AchievementProgress Current(uint id) => _progress.TryGetValue(id, out var value) ? value : new(id, 0, 0);
}

public sealed record TodayAssignmentEntry(int StepId, int GroupId, int QuestContextId, sbyte Status);

public sealed class TodayAssignmentState
{
    private readonly Dictionary<int, TodayAssignmentEntry> _entries = [];
    private readonly Dictionary<uint, uint> _resetCounts = [];
    private readonly Dictionary<uint, bool> _sentGoals = [];
    public IReadOnlyDictionary<int, TodayAssignmentEntry> EntriesByStepId => _entries;
    public IReadOnlyDictionary<uint, uint> ResetCountsByType => _resetCounts;
    public IReadOnlyDictionary<uint, bool> SentGoalsById => _sentGoals;
    public int? GoalType { get; private set; }
    public ushort? LastAcceptAllError { get; private set; }
    public void Clear() { _entries.Clear(); _resetCounts.Clear(); _sentGoals.Clear(); GoalType = null; LastAcceptAllError = null; }
    public void Apply(IAssignmentProtocolEvent value)
    {
        switch (value)
        {
            case TodayAssignmentChangedEvent changed:
                _entries[changed.StepId] = new(changed.StepId, changed.GroupId, changed.QuestContextId, changed.Status);
                break;
            case TodayAssignmentResetCountEvent reset:
                _resetCounts[reset.CountType] = reset.Count;
                break;
            case TodayAssignmentGoalEvent goal:
                GoalType = goal.Type;
                break;
            case TodayAssignmentItemSentEvent sent:
                _sentGoals[sent.Goal] = sent.ByMail;
                break;
            case TodayAssignmentAcceptAllEvent accepted:
                LastAcceptAllError = accepted.ErrorMessage;
                break;
        }
    }
}
