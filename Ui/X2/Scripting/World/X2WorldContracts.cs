#nullable enable
namespace AAEmu.GodotViewer.Ui.X2.Scripting.World;

// Contracts between the in-game x2ui (stage 6) Lua bindings and the live game state.
//
// Every X2 API family lives in its own class `X2<Family>Api` with
//     public static void Install(X2LuaHost host, X2GameContext context, IX2<Family>Data data)
// that registers its methods with host.Define(namespace, method, args => result). The data interface is declared next to
// the family and describes exactly what the family needs from the game (queries and commands); a Null<Family>Data
// implementation returns what the client shows for an empty/fresh character. The host (X2WorldBridge) adapts the network
// session and the state modules (combat, inventory, quests, social, settings, audio, game data) onto these interfaces.

/// <summary>Raises client UI events (the names the scripts register, e.g. "UNIT_HEALTH_CHANGED", "CHAT_MESSAGE").</summary>
public interface IX2Events
{
    void Fire(string eventName, params object?[] args);
}

/// <summary>A unit as the UI sees it (player, NPC, mount, ...). Values the client has not received yet are 0/"".</summary>
public sealed record X2UnitInfo
{
    public uint Id { get; init; }
    public string Name { get; init; } = "";
    /// <summary>"character", "npc", "mate", "slave", "housing", "transfer", "shipyard", "doodad".</summary>
    public string Type { get; init; } = "npc";
    public int Level { get; init; }
    public int HeirLevel { get; init; }
    public long Hp { get; init; }
    public long MaxHp { get; init; }
    public long Mp { get; init; }
    public long MaxMp { get; init; }
    /// <summary>Race name as X2Unit:GetRaceStr returns it ("nuian", "elf", ...); "" for NPCs.</summary>
    public string Race { get; init; } = "";
    /// <summary>"male" / "female" / "".</summary>
    public string Gender { get; init; } = "";
    public uint TemplateId { get; init; }
    public ulong CharacterId { get; init; }
    public uint FactionId { get; init; }
    public string FactionName { get; init; } = "";
    public string ExpeditionName { get; init; } = "";
    public uint ModelId { get; init; }
    public float X { get; init; }
    public float Y { get; init; }
    public float Z { get; init; }
    public bool IsDead { get; init; }
    public bool InCombat { get; init; }
    /// <summary>Combat abilities (enum_abilities ids) for characters, up to three.</summary>
    public IReadOnlyList<int> Abilities { get; init; } = [];
}

/// <summary>Units known to the client and the UI's unit tokens.</summary>
public interface IX2UnitData
{
    /// <summary>
    /// Resolves a unit token: "player", "target", "targettarget", "playerpet" / "playerpet1".."playerpet2",
    /// "team1".."team50", "watchtarget", or a unit id as a decimal string. Null when nothing matches.
    /// </summary>
    uint? Resolve(string token);

    X2UnitInfo? Get(uint unitId);

    IEnumerable<X2UnitInfo> All { get; }

    /// <summary>The own character's unit id (0 before world entry).</summary>
    uint PlayerId { get; }

    /// <summary>Current target unit id, 0 when none.</summary>
    uint TargetId { get; }

    /// <summary>Asks the game to target a unit (0 clears); the target change comes back as an event.</summary>
    void RequestTarget(uint unitId);
}

/// <summary>Everything a family may need besides its own data interface.</summary>
public sealed class X2GameContext
{
    public X2GameContext(IX2Events events, IX2UnitData units, IUiTextSource texts, Action<string> log)
    {
        Events = events;
        Units = units;
        Texts = texts;
        Log = log;
    }

    public IX2Events Events { get; }
    public IX2UnitData Units { get; }
    /// <summary>ui_texts (English); use for localised strings the API returns.</summary>
    public IUiTextSource Texts { get; }
    public Action<string> Log { get; }
}

/// <summary>No units (offline / before world entry).</summary>
public sealed class NullUnitData : IX2UnitData
{
    public static readonly NullUnitData Instance = new();
    public uint? Resolve(string token) => null;
    public X2UnitInfo? Get(uint unitId) => null;
    public IEnumerable<X2UnitInfo> All => [];
    public uint PlayerId => 0;
    public uint TargetId => 0;
    public void RequestTarget(uint unitId) { }
}

/// <summary>Events go to the current UI root (widgets registered with RegisterEvent, UIParent:SetEventHandler).</summary>
public sealed class RootEvents(Func<UiRoot?> root) : IX2Events
{
    public void Fire(string eventName, params object?[] args) => root()?.DispatchEvent(eventName, args);
}
