#nullable enable

using AAEmu.GodotViewer.Data;
using AAEmu.GodotViewer.Effects;
using AAEmu.GodotViewer.Net;
using AAEmu.GodotViewer.Settings;
using Godot;
using NVector3 = System.Numerics.Vector3;

namespace AAEmu.GodotViewer.Client;

public partial class OnlineSession
{
    private readonly Dictionary<ushort, CastPlayback> _combatCasts = [];
    private uint _autoAttackTargetId;
    private uint _autoAttackSkillId;
    private ushort _autoAttackTimelineId;
    private bool _autoAttackCancelPending;
    private uint? _pendingServerTargetId;
    private bool _applyingServerTarget;
    private CharacterNode? _playerCharacter;
    private GameData? _combatData;
    private EffectPlayer? _effectPlayer;
    private CombatFloatingText? _floatingText;
    private readonly Dictionary<(uint UnitId, uint BuffIndex), List<Node3D>> _buffEffectNodes = [];
    private readonly HashSet<string> _particleLibrariesAttempted = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Path to the read-only client content database used for combat metadata.</summary>
    public string GameDatabasePath { get; set; } = ClientPaths.Database;

    /// <summary>Main-thread reducer for unit vitals, skill lists, casts, buffs, targets and combat state.</summary>
    public CombatState CombatState { get; private set; } = new();
    public GameData? ContentData => _combatData;
    public UnitRegistry? UnitRegistry { get; private set; }
    public TargetingController? Targeting { get; private set; }

    /// <summary>Raised on the main thread after CombatState has applied a dequeued event.</summary>
    public event Action<GameEvent>? GameEventApplied;
    public event Action<EnteredWorldEvent>? EnteredWorld;
    public event Action<ProgressEvent>? ProgressChanged;
    public event Action<ErrorEvent>? NetworkError;
    public event Action<WorldListEvent>? WorldList;
    public event Action<CharacterListEvent>? CharacterList;
    public event Action<UnitAppearedEvent>? UnitAppeared;
    public event Action<UnitMovedEvent>? UnitMoved;
    public event Action<UnitsRemovedEvent>? UnitsRemoved;
    public event Action<TeleportedEvent>? Teleported;
    public event Action<UnitPointsEvent>? UnitPoints;
    public event Action<ChatEvent>? ChatReceived;
    public event Action<LeavingWorldEvent>? LeavingWorld;
    public event Action<DisconnectedEvent>? Disconnected;
    public event Action<RawPacketEvent>? RawPacket;

    public event Action<UnitStateEvent>? UnitState;
    public event Action<UnitSkillListEvent>? UnitSkillList;
    public event Action<UnitDeathEvent>? UnitDeath;
    public event Action<ResurrectionPromptEvent>? ResurrectionPrompt;
    public event Action<CharacterResurrectedEvent>? CharacterResurrected;
    public event Action<ExperienceChangedEvent>? ExperienceChanged;
    public event Action<RecoverableExperienceChangedEvent>? RecoverableExperienceChanged;
    public event Action<AbilityExperienceChangedEvent>? AbilityExperienceChanged;
    public event Action<UnitLevelEvent>? UnitLevelChanged;
    public event Action<BreathChangedEvent>? BreathChanged;
    public event Action<CharacterDetailEvent>? CharacterDetail;
    public event Action<BuffCreatedEvent>? BuffCreated;
    public event Action<BuffRemovedEvent>? BuffRemoved;
    public event Action<BuffUpdatedEvent>? BuffUpdated;
    public event Action<SkillStartedEvent>? SkillStarted;
    public event Action<SkillFiredEvent>? SkillFired;
    public event Action<SkillEndedEvent>? SkillEnded;
    public event Action<SkillStoppedEvent>? SkillStopped;
    public event Action<DamageEvent>? Damage;
    public event Action<HealEvent>? Heal;
    public event Action<CombatTextEvent>? CombatText;
    public event Action<EnvironmentDamageEvent>? EnvironmentDamage;
    public event Action<CooldownListEvent>? Cooldowns;
    public event Action<CooldownResetEvent>? CooldownReset;
    public event Action<CooldownReduceEvent>? CooldownReduced;
    public event Action<ChargeCooldownChangedEvent>? ChargeCooldownChanged;
    public event Action<TargetChangedEvent>? TargetChanged;
    public event Action<CombatEngagedEvent>? CombatEngaged;
    public event Action<CombatClearedEvent>? CombatCleared;
    public event Action<CombatFirstHitEvent>? CombatFirstHit;
    public event Action<ForceAttackChangedEvent>? ForceAttackChanged;
    public event Action<CombatRelationshipsEvent>? CombatRelationshipsChanged;
    public event Action<AiAggroEvent>? AiAggro;
    public event Action<AggroTargetChangedEvent>? AggroTargetChanged;
    public event Action<AggroRemovedEvent>? AggroRemoved;
    public event Action<AttackFactionChangedEvent>? AttackFactionChanged;
    public event Action<UnitPostureChangedEvent>? UnitPostureChanged;
    public event Action<EmotionExpressedEvent>? EmotionExpressed;
    public event Action<UnitNameChangedEvent>? UnitNameChanged;
    public event Action<UnitFactionChangedEvent>? UnitFactionChanged;
    public event Action<SkillBookUpdatedEvent>? SkillBookUpdated;
    public event Action<AbilitiesSwappedEvent>? AbilitiesSwapped;
    public event Action<SkillLearnedEvent>? SkillLearned;
    public event Action<SkillsResetEvent>? SkillsReset;
    public event Action<SkillUnlockEvent>? SkillUnlock;
    public event Action<SkillPointsEvent>? SkillPointsChanged;
    public event Action<SkillActiveTypesEvent>? SkillActiveTypesChanged;
    public event Action<HeirSkillListEvent>? HeirSkillsChanged;
    public event Action<ActabilityEvent>? ActabilityChanged;
    public event Action<CharacterGamePointsEvent>? CharacterGamePoints;
    public event Action<GamePointsChangedEvent>? GamePointsChanged;
    public event Action<CharacterSubStatsEvent>? CharacterSubStats;
    public event Action<ServerAttributeValuesEvent>? ServerAttributeValues;
    public event Action<CombatResourcePointEvent>? CombatResourcePoint;
    public event Action<CombatResourceTransformEvent>? CombatResourceTransform;
    public event Action<CombatResourceUnitMoveEvent>? CombatResourceUnitMove;

    private void InitializeCombat()
    {
        CombatState = new CombatState(Entered.UnitId);
        CombatState.Apply(Entered);
        if (File.Exists(GameDatabasePath))
            _combatData = new GameData(GameDatabasePath);
        else
            GD.PrintErr($"Combat content database is missing: {GameDatabasePath}");

        UnitRegistry = new UnitRegistry(CombatState, _combatData, Entered.CharacterId, Doodads);
        UnitRegistry.Register(Entered.Self, Player, isPlayer: true);

        var keyBindings = new KeyBindings();
        var keyError = keyBindings.Load();
        if (keyError != Error.Ok)
            GD.PrintErr($"Could not load x2 key bindings: {keyError}");

        Targeting = new TargetingController
        {
            Name = "TargetingController",
            Registry = UnitRegistry,
            Camera = GetViewport()?.GetCamera3D(),
            EnableHotkeys = false,
            ResolveSelf = () => UnitRegistry.TryGet(Entered.UnitId, out var self) ? self : null,
            // No party-roster packet is wired yet, so stable F2-F5 member ordering is unavailable.
            ResolvePartyMember = null,
        };
        Targeting.TargetChanged += OnTargetChanged;
        Targeting.TargetDoubleClicked += OnTargetDoubleClicked;
        Targeting.TargetRightClicked += OnTargetRightClicked;
        AddChild(Targeting);

        _effectPlayer = new EffectPlayer { Name = "CombatEffectPlayer" };
        AddChild(_effectPlayer);
        _floatingText = new CombatFloatingText { Name = "CombatFloatingText" };
        AddChild(_floatingText);
        InitializeOverhead();
    }

    private void DisposeCombat()
    {
        foreach (var nodes in _buffEffectNodes.Values)
            foreach (var node in nodes)
                if (GodotObject.IsInstanceValid(node)) node.QueueFree();
        _buffEffectNodes.Clear();
        _questMarkers?.Dispose();
        _questMarkers = null;
        _questSpeech?.Dispose();
        _questSpeech = null;
        _emotes?.Dispose();
        _emotes = null;
        _combatData?.Dispose();
        _combatData = null;
    }

    private void ApplyCombatEvent(GameEvent ev)
    {
        CombatState.Apply(ev);
        ApplyCombatVisualEvent(ev);
        GameEventApplied?.Invoke(ev);
        PublishTypedEvent(ev);
    }

    private void PublishTypedEvent(GameEvent ev)
    {
        switch (ev)
        {
            case EnteredWorldEvent value: EnteredWorld?.Invoke(value); break;
            case ProgressEvent value: ProgressChanged?.Invoke(value); break;
            case ErrorEvent value: NetworkError?.Invoke(value); break;
            case WorldListEvent value: WorldList?.Invoke(value); break;
            case CharacterListEvent value: CharacterList?.Invoke(value); break;
            case UnitAppearedEvent value: UnitAppeared?.Invoke(value); break;
            case UnitMovedEvent value: UnitMoved?.Invoke(value); break;
            case UnitsRemovedEvent value: UnitsRemoved?.Invoke(value); break;
            case TeleportedEvent value: Teleported?.Invoke(value); break;
            case UnitPointsEvent value: UnitPoints?.Invoke(value); break;
            case ChatEvent value: ChatReceived?.Invoke(value); break;
            case LeavingWorldEvent value: LeavingWorld?.Invoke(value); break;
            case DisconnectedEvent value: Disconnected?.Invoke(value); break;
            case RawPacketEvent value: RawPacket?.Invoke(value); break;
            case UnitStateEvent value: UnitState?.Invoke(value); break;
            case UnitSkillListEvent value: UnitSkillList?.Invoke(value); break;
            case UnitDeathEvent value: UnitDeath?.Invoke(value); break;
            case ResurrectionPromptEvent value: ResurrectionPrompt?.Invoke(value); break;
            case CharacterResurrectedEvent value: CharacterResurrected?.Invoke(value); break;
            case ExperienceChangedEvent value: ExperienceChanged?.Invoke(value); break;
            case RecoverableExperienceChangedEvent value: RecoverableExperienceChanged?.Invoke(value); break;
            case AbilityExperienceChangedEvent value: AbilityExperienceChanged?.Invoke(value); break;
            case UnitLevelEvent value: UnitLevelChanged?.Invoke(value); break;
            case BreathChangedEvent value: BreathChanged?.Invoke(value); break;
            case CharacterDetailEvent value: CharacterDetail?.Invoke(value); break;
            case BuffCreatedEvent value: BuffCreated?.Invoke(value); break;
            case BuffRemovedEvent value: BuffRemoved?.Invoke(value); break;
            case BuffUpdatedEvent value: BuffUpdated?.Invoke(value); break;
            case SkillStartedEvent value: SkillStarted?.Invoke(value); break;
            case SkillFiredEvent value: SkillFired?.Invoke(value); break;
            case SkillEndedEvent value: SkillEnded?.Invoke(value); break;
            case SkillStoppedEvent value: SkillStopped?.Invoke(value); break;
            case DamageEvent value: Damage?.Invoke(value); break;
            case HealEvent value: Heal?.Invoke(value); break;
            case CombatTextEvent value: CombatText?.Invoke(value); break;
            case EnvironmentDamageEvent value: EnvironmentDamage?.Invoke(value); break;
            case CooldownListEvent value: Cooldowns?.Invoke(value); break;
            case CooldownResetEvent value: CooldownReset?.Invoke(value); break;
            case CooldownReduceEvent value: CooldownReduced?.Invoke(value); break;
            case ChargeCooldownChangedEvent value: ChargeCooldownChanged?.Invoke(value); break;
            case TargetChangedEvent value: TargetChanged?.Invoke(value); break;
            case CombatEngagedEvent value: CombatEngaged?.Invoke(value); break;
            case CombatClearedEvent value: CombatCleared?.Invoke(value); break;
            case CombatFirstHitEvent value: CombatFirstHit?.Invoke(value); break;
            case ForceAttackChangedEvent value: ForceAttackChanged?.Invoke(value); break;
            case CombatRelationshipsEvent value: CombatRelationshipsChanged?.Invoke(value); break;
            case AiAggroEvent value: AiAggro?.Invoke(value); break;
            case AggroTargetChangedEvent value: AggroTargetChanged?.Invoke(value); break;
            case AggroRemovedEvent value: AggroRemoved?.Invoke(value); break;
            case AttackFactionChangedEvent value: AttackFactionChanged?.Invoke(value); break;
            case UnitPostureChangedEvent value: UnitPostureChanged?.Invoke(value); break;
            case EmotionExpressedEvent value: EmotionExpressed?.Invoke(value); break;
            case UnitNameChangedEvent value: UnitNameChanged?.Invoke(value); break;
            case UnitFactionChangedEvent value: UnitFactionChanged?.Invoke(value); break;
            case SkillBookUpdatedEvent value: SkillBookUpdated?.Invoke(value); break;
            case AbilitiesSwappedEvent value: AbilitiesSwapped?.Invoke(value); break;
            case SkillLearnedEvent value: SkillLearned?.Invoke(value); break;
            case SkillsResetEvent value: SkillsReset?.Invoke(value); break;
            case SkillUnlockEvent value: SkillUnlock?.Invoke(value); break;
            case SkillPointsEvent value: SkillPointsChanged?.Invoke(value); break;
            case SkillActiveTypesEvent value: SkillActiveTypesChanged?.Invoke(value); break;
            case HeirSkillListEvent value: HeirSkillsChanged?.Invoke(value); break;
            case ActabilityEvent value: ActabilityChanged?.Invoke(value); break;
            case CharacterGamePointsEvent value: CharacterGamePoints?.Invoke(value); break;
            case GamePointsChangedEvent value: GamePointsChanged?.Invoke(value); break;
            case CharacterSubStatsEvent value: CharacterSubStats?.Invoke(value); break;
            case ServerAttributeValuesEvent value: ServerAttributeValues?.Invoke(value); break;
            case CombatResourcePointEvent value: CombatResourcePoint?.Invoke(value); break;
            case CombatResourceTransformEvent value: CombatResourceTransform?.Invoke(value); break;
            case CombatResourceUnitMoveEvent value: CombatResourceUnitMove?.Invoke(value); break;
        }
    }

    private void RegisterCombatUnit(UnitSnapshot snapshot, Node3D node)
    {
        UnitRegistry?.Register(snapshot, node, snapshot.UnitId == Entered.UnitId);
        TryApplyPendingServerTarget();
        RefreshOverheadUnits();
    }

    private void SetPlayerCharacter(CharacterNode character)
    {
        _playerCharacter = character;
        if (UnitRegistry?.TryGet(Entered.UnitId, out var target) == true)
            target!.RefreshModelBounds();
        ReattachBuffEffects(Entered.UnitId);
        ApplyCombatVisualState(Entered.UnitId);
        RefreshOverheadUnits();
    }

    private void SetCombatCharacter(uint unitId, CharacterNode character)
    {
        if (UnitRegistry?.TryGet(unitId, out var target) == true)
            target!.RefreshModelBounds();
        ReattachBuffEffects(unitId);
        ApplyCombatVisualState(unitId);
        RefreshOverheadUnits();
    }

    private void RemoveCombatUnit(uint unitId)
    {
        ClearCasterSkillState(unitId);
        if (_autoAttackTargetId == unitId)
            ClearAutoAttackIntent();
        UnitRegistry?.Remove(unitId);
        RemoveBuffEffects(unitId);
        if (_pendingServerTargetId == unitId)
            _pendingServerTargetId = null;
        if (Targeting?.Target?.Id == unitId)
            Targeting.Clear();
        RefreshOverheadUnits();
    }

    private void OnTargetChanged(ITargetable? target)
    {
        if (_applyingServerTarget)
            return;
        // A local choice supersedes a delayed SCTargetChanged for a unit that had not spawned yet.
        _pendingServerTargetId = null;
        if (Client == null || Client.Stage != ClientStage.InWorld)
            return;
        Client.SendGame(CombatPacketWriters.SelectTarget(target?.Id ?? 0));
    }

    private void ApplyPendingServerTarget()
    {
        if (_pendingServerTargetId is not { } id)
            return;
        if (id == 0)
        {
            ApplyServerTarget(null);
            _pendingServerTargetId = null;
        }
        else if (UnitRegistry?.TryGet(id, out _) == true)
        {
            ApplyServerTarget(id);
            _pendingServerTargetId = null;
        }
    }

    private void TryApplyPendingServerTarget() => ApplyPendingServerTarget();

    private void ApplyServerTarget(uint? id)
    {
        if (Targeting == null)
            return;
        _applyingServerTarget = true;
        try
        {
            if (id is null or 0)
                Targeting.Clear();
            else
                Targeting.Select(id.Value);
        }
        finally
        {
            _applyingServerTarget = false;
        }
    }

    private void OnTargetDoubleClicked(ITargetable? target)
    {
        if (target is { Relation: TargetRelation.Hostile, Dead: false })
            ToggleAutoAttack(target);
    }

    public bool UseSkill(uint skillId, NVector3? aimPosition = null)
    {
        if (Client == null || Client.Stage != ClientStage.InWorld || _combatData?.GetSkill(skillId) is not { } skill)
            return false;

        var caster = Entered.UnitId;
        CombatOutboundPacket packet;
        if (skill.TargetTypeId == 0) // enum_skill_target_type.self
            packet = CombatPacketWriters.StartSkillOnSelf(skillId, caster);
        else if (IsPositionSkill(skill.TargetTypeId))
        {
            var position = aimPosition ?? ResolveAimPosition(skill.TargetTypeId);
            packet = CombatPacketWriters.StartSkillOnPosition(skillId, caster, position, Player.Heading);
        }
        else if (skill.TargetTypeId == 9) // item casts need an item-caster/object body not used by this HUD bridge.
            return false;
        else if (Targeting?.Target is { } target)
            packet = CombatPacketWriters.StartSkillOnUnit(skillId, caster, target.Id);
        else
            return false;

        Client.SendGame(packet);
        return true;
    }

    private NVector3 ResolveAimPosition(int targetType)
    {
        if (targetType == 14) // source_pos
            return Player.CryPosition;
        // cursor_pos is explicitly driven by the mouse even when a unit is selected.
        if (targetType != 18 && Targeting?.Target is { } selected)
            return World.ToCry(selected.Node.GlobalPosition);
        var viewport = GetViewport();
        var camera = Targeting?.Camera ?? viewport?.GetCamera3D();
        if (camera != null && viewport != null)
        {
            var mouse = viewport.GetMousePosition();
            var origin = camera.ProjectRayOrigin(mouse);
            var direction = camera.ProjectRayNormal(mouse);
            var plane = new Plane(Vector3.Up, Player.GlobalPosition.Y);
            if (plane.IntersectsRay(origin, direction) is { } point)
                return World.ToCry(point);
        }
        if (Targeting?.Target is { } fallback)
            return World.ToCry(fallback.Node.GlobalPosition);
        return Player.CryPosition;
    }

    private static bool IsPositionSkill(int targetType) => targetType is 6 or 11 or 12 or 13 or 14 or 15 or 18 or 23;

    private uint ChooseAutoAttackSkill()
    {
        var known = CombatState.TryGetUnit(Entered.UnitId, out var player) ? player!.Skills : Array.Empty<uint>();
        foreach (var id in new uint[] { 2, 3, 4 })
            if (known.Contains(id) && _combatData?.GetSkill(id) != null)
                return id;
        foreach (var id in new uint[] { 2, 3, 4 })
            if (_combatData?.GetSkill(id) != null)
                return id;
        return 0;
    }

    private void ToggleAutoAttack(ITargetable? explicitTarget = null)
    {
        if (Client == null || Client.Stage != ClientStage.InWorld)
            return;
        var target = explicitTarget ?? Targeting?.Target;
        if (target is not { Relation: TargetRelation.Hostile, Dead: false })
            return;

        // Wait for the server timeline needed to cancel the previous request. Starting another
        // basic attack here would make the two otherwise identical timelines ambiguous.
        if (_autoAttackCancelPending)
            return;

        if (_autoAttackTargetId == target.Id)
        {
            StopAutoAttack();
            return;
        }
        StopAutoAttack();
        if (_autoAttackCancelPending)
            return;
        var skillId = ChooseAutoAttackSkill();
        if (skillId == 0)
            return;
        Client.SendGame(CombatPacketWriters.StartAutoAttack(skillId, Entered.UnitId, target.Id));
        _autoAttackSkillId = skillId;
        _autoAttackTargetId = target.Id;
    }

    private void StopAutoAttack()
    {
        if (_autoAttackTimelineId == 0 && CombatState.TryGetUnit(Entered.UnitId, out var player) &&
            player!.CurrentCast is { SkillId: 2 or 3 or 4 } cast)
            _autoAttackTimelineId = cast.TimelineId;
        if (_autoAttackTimelineId != 0 && Client != null && Client.Stage == ClientStage.InWorld)
        {
            Client.SendGame(CombatPacketWriters.StopAutoAttack(_autoAttackTimelineId, 0, Entered.UnitId));
            ClearAutoAttackIntent();
            return;
        }

        if (_autoAttackTargetId != 0 && _autoAttackSkillId is 2 or 3 or 4)
        {
            // CSStopCasting needs the server-assigned timeline. Remember the cancellation until
            // the matching SCSkillStarted arrives instead of losing a rapid toggle-off request.
            _autoAttackTimelineId = 0;
            _autoAttackTargetId = 0;
            _autoAttackCancelPending = true;
            return;
        }

        ClearAutoAttackIntent();
    }

    private void ClearAutoAttackIntent()
    {
        _autoAttackSkillId = 0;
        _autoAttackTimelineId = 0;
        _autoAttackTargetId = 0;
        _autoAttackCancelPending = false;
    }

    private void ClearCasterSkillState(uint unitId)
    {
        foreach (var timelineId in _combatCasts
                     .Where(pair => pair.Value.CasterUnitId == unitId)
                     .Select(pair => pair.Key)
                     .ToArray())
            _combatCasts.Remove(timelineId);
        if (unitId == Entered.UnitId)
            ClearAutoAttackIntent();
    }

    public override void _UnhandledInput(InputEvent inputEvent)
    {
        if (HandleHousingUnhandledInput(inputEvent))
        {
            GetViewport().SetInputAsHandled();
            return;
        }

        if (Targeting == null)
            return;

        if (inputEvent.IsActionPressed("x2_cycle_hostile_forward", exactMatch: true))
            Targeting.SelectNext(reverse: false);
        else if (inputEvent.IsActionPressed("x2_cycle_hostile_backward", exactMatch: true))
            Targeting.SelectNext(reverse: true);
        else if (inputEvent.IsActionPressed("x2_round_target", exactMatch: true))
            Targeting.Select(Entered.UnitId);
        else if (inputEvent.IsActionPressed("x2_toggle_force_attack", exactMatch: true))
            ToggleAutoAttack();
        else if (inputEvent.IsActionPressed("x2_do_interaction_1", exactMatch: true))
        {
            if (Targeting.Target is { } target && BeginWorldInteraction(target, pickedWithRightMouse: false))
                GetViewport().SetInputAsHandled();
            return;
        }
        else
        {
            for (var member = 1; member <= 4; member++)
            {
                if (!inputEvent.IsActionPressed($"x2_team_target_{member}", exactMatch: true))
                    continue;
                // Team ordering requires a party roster; relation alone cannot identify F2-F5 slots.
                return;
            }
            // Action-bar keys are handled by the UI (X2UiLayer.UseActionSlot): the bar's contents decide what they do.
            return;
        }

        GetViewport().SetInputAsHandled();
    }
}
