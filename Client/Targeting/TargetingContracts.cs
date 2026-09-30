#nullable enable
using Godot;

namespace AAEmu.GodotViewer.Client;

public enum TargetKind
{
    Npc,
    Player,
    Doodad,
    Mate,
    Vehicle,
    Other,
}

public enum TargetRelation
{
    Hostile,
    Neutral,
    Friendly,
    Party,
}

/// <summary>A live, allocation-free view of objects which the local client may target.</summary>
public interface ITargetableRegistry
{
    IEnumerable<ITargetable> All { get; }
}

/// <summary>
/// Minimal scene-facing target data. <see cref="Bounds"/> is in <see cref="Node"/> local space;
/// when omitted, click targeting uses an upright capsule derived from <see cref="Height"/>.
/// </summary>
public interface ITargetable
{
    uint Id { get; }
    Node3D Node { get; }
    string Name { get; }
    TargetKind Kind { get; }
    TargetRelation Relation { get; }
    bool Dead { get; }
    float Height { get; }
    Aabb? Bounds { get; }
}

/// <summary>Optional richer nameplate data. Health is normalized to the inclusive 0..1 range.</summary>
public interface ITargetableNameplate
{
    string GuildOrTitle { get; }
    float HealthFraction { get; }
}

/// <summary>Optional source for resolving the selected unit's own target.</summary>
public interface ITargetOfTargetSource
{
    uint? TargetId { get; }
}
