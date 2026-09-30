#nullable enable

using AAEmu.GodotViewer.Data;
using AAEmu.GodotViewer.Net;
using AAEmu.GodotViewer.Client.Targeting;
using Godot;

namespace AAEmu.GodotViewer.Client;

/// <summary>Live target views backed by OnlineSession nodes and the main-thread combat reducer.</summary>
public sealed class UnitRegistry : ITargetableRegistry
{
    private readonly Dictionary<uint, TargetableUnit> _units = [];
    private readonly CombatState _combat;
    private readonly GameData? _data;
    private readonly DoodadModelResolver? _doodads;
    private readonly ulong _playerCharacterId;

    public UnitRegistry(CombatState combat, GameData? data, ulong playerCharacterId, DoodadModelResolver? doodads = null)
    {
        _combat = combat;
        _data = data;
        _doodads = doodads;
        _playerCharacterId = playerCharacterId;
    }

    public IEnumerable<ITargetable> All => _units.Values;

    public TargetableUnit Register(UnitSnapshot snapshot, Node3D node, bool isPlayer = false)
    {
        var target = Register(snapshot.UnitId, node, snapshot.Kind, snapshot.TemplateId, snapshot.Name,
            snapshot.OwnerId, isPlayer, snapshot.Level, snapshot.PhaseId);
        target.RefreshModelBounds();
        return target;
    }

    public TargetableUnit Register(uint id, Node3D node, UnitKind kind, uint templateId, string name = "",
        ulong ownerCharacterId = 0, bool isPlayer = false, sbyte level = 0, uint phaseId = 0)
    {
        var interaction = kind == UnitKind.Doodad
            ? _doodads?.GetInteractionKind(templateId, phaseId) ?? DoodadInteractionKind.None
            : DoodadInteractionKind.None;
        if (!_units.TryGetValue(id, out var target))
            _units[id] = target = new TargetableUnit(id, node, kind, templateId, name, ownerCharacterId,
                isPlayer, level, interaction, _combat, _data, _playerCharacterId);
        else
            target.Update(node, kind, templateId, name, ownerCharacterId, isPlayer, level, interaction);
        return target;
    }

    public bool Remove(uint id) => _units.Remove(id);

    public bool TryGet(uint id, out TargetableUnit? target) => _units.TryGetValue(id, out target);

    public sealed class TargetableUnit : ITargetable, ITargetableNameplate, ITargetOfTargetSource, ITargetableHoverInfo
    {
        private const float FallbackHeight = 1.8f;
        private readonly CombatState _combat;
        private readonly GameData? _data;
        private readonly ulong _playerCharacterId;
        private UnitKind _unitKind;
        private uint _templateId;
        private string _name;
        private ulong _ownerCharacterId;
        private bool _isPlayer;
        private sbyte _level;
        private DoodadInteractionKind _interaction;
        private Aabb? _bounds;

        internal TargetableUnit(uint id, Node3D node, UnitKind unitKind, uint templateId, string name,
            ulong ownerCharacterId, bool isPlayer, sbyte level, DoodadInteractionKind interaction,
            CombatState combat, GameData? data, ulong playerCharacterId)
        {
            Id = id;
            Node = node;
            _unitKind = unitKind;
            _templateId = templateId;
            _name = name;
            _ownerCharacterId = ownerCharacterId;
            _isPlayer = isPlayer;
            _level = level;
            _interaction = interaction;
            _combat = combat;
            _data = data;
            _playerCharacterId = playerCharacterId;
        }

        public uint Id { get; }
        public uint TemplateId => _templateId;
        public Node3D Node { get; private set; }
        public string Name => CurrentState?.Name is { Length: > 0 } name ? name
            : _name.Length > 0 ? _name : Npc?.Name ?? $"{Kind} {Id}";
        public TargetKind Kind => _unitKind switch
        {
            UnitKind.Npc => TargetKind.Npc,
            UnitKind.Character => TargetKind.Player,
            UnitKind.Doodad => TargetKind.Doodad,
            UnitKind.Mate => TargetKind.Mate,
            UnitKind.Slave => TargetKind.Vehicle,
            _ => TargetKind.Other,
        };
        public TargetRelation Relation => ResolveRelation();
        public bool Dead => CurrentState?.IsDead ?? false;
        public int Level
        {
            get
            {
                var current = CurrentState?.Level ?? 0;
                return current > 0 ? current : _level > 0 ? _level : Npc?.Level ?? 0;
            }
        }
        public bool HasDialogue => Npc?.HasDialogue ?? false;
        public bool CanLoot => Dead && (Npc?.HasLootPack ?? false);
        public DoodadInteractionKind Interaction => _interaction;
        public float Height => _bounds is { } bounds ? Mathf.Max(FallbackHeight * 0.35f, bounds.Size.Y) : FallbackHeight;
        public Aabb? Bounds => _bounds;
        public string GuildOrTitle => "";
        public float HealthFraction
        {
            get
            {
                var state = CurrentState;
                if (state?.MaxHp is not { } max || max <= 0)
                    return 0f;
                return Mathf.Clamp((float)state.Hp / max, 0f, 1f);
            }
        }
        public uint? TargetId => CurrentState is { TargetUnitId: > 0 } state ? state.TargetUnitId : null;

        private CombatUnitState? CurrentState => _combat.TryGetUnit(Id, out var value) ? value : null;
        private NpcData? Npc => _unitKind is UnitKind.Npc or UnitKind.Mate ? _data?.GetNpc(_templateId) : null;

        internal void Update(Node3D node, UnitKind unitKind, uint templateId, string name,
            ulong ownerCharacterId, bool isPlayer, sbyte level, DoodadInteractionKind interaction)
        {
            Node = node;
            _unitKind = unitKind;
            _templateId = templateId;
            _name = name;
            _ownerCharacterId = ownerCharacterId;
            _isPlayer = isPlayer;
            _level = level;
            _interaction = interaction;
        }

        public void RefreshModelBounds()
        {
            if (!GodotObject.IsInstanceValid(Node) || !Node.IsInsideTree())
            {
                _bounds = null;
                return;
            }

            var rootInverse = Node.GlobalTransform.AffineInverse();
            var found = false;
            var merged = new Aabb();
            void Visit(Node parent)
            {
                foreach (var child in parent.GetChildren())
                {
                    if (child is MeshInstance3D meshInstance && meshInstance.Mesh != null)
                    {
                        var local = rootInverse * meshInstance.GlobalTransform;
                        var meshBounds = meshInstance.GetAabb();
                        for (var corner = 0; corner < 8; corner++)
                        {
                            var point = meshBounds.Position + new Vector3(
                                (corner & 1) == 0 ? 0 : meshBounds.Size.X,
                                (corner & 2) == 0 ? 0 : meshBounds.Size.Y,
                                (corner & 4) == 0 ? 0 : meshBounds.Size.Z);
                            point = local * point;
                            merged = found ? merged.Expand(point) : new Aabb(point, Vector3.Zero);
                            found = true;
                        }
                    }
                    Visit(child);
                }
            }

            Visit(Node);
            _bounds = found && merged.Size.LengthSquared() > 0.0001f ? merged : null;
        }

        private TargetRelation ResolveRelation()
        {
            if (_isPlayer || Id == _combat.PlayerUnitId ||
                (_unitKind == UnitKind.Mate && _ownerCharacterId != 0 && _ownerCharacterId == _playerCharacterId))
                return TargetRelation.Friendly;

            var local = _combat.TryGetUnit(_combat.PlayerUnitId, out var player) ? player : null;
            var localFaction = local?.FactionId is > 0 ? (int)local.FactionId : (int)(local?.MotherFactionId ?? 0);
            var state = CurrentState;
            var faction = state?.FactionId is > 0 ? (int)state.FactionId : (int)(state?.MotherFactionId ?? 0);
            if (faction == 0 && Npc is { FactionId: > 0 } npc)
                faction = npc.FactionId;

            if (localFaction > 0 && faction > 0 && _data?.GetFactionRelation(localFaction, faction) is { } relation)
                return relation switch
                {
                    1 => TargetRelation.Hostile,
                    2 => TargetRelation.Neutral,
                    3 => TargetRelation.Friendly,
                    _ => TargetRelation.Neutral,
                };

            // NPC aggression is the only available fallback proxy for attackability in this DB.
            return _unitKind == UnitKind.Npc && Npc is { Aggression: > 0 }
                ? TargetRelation.Hostile
                : TargetRelation.Neutral;
        }
    }
}
