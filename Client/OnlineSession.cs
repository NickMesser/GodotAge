using System.Collections.Concurrent;
using AAEmu.GodotViewer.Net;
using Godot;
using Microsoft.Data.Sqlite;
using NVector3 = System.Numerics.Vector3;

namespace AAEmu.GodotViewer.Client;

/// <summary>
/// The in-world half of the client: turns the logged-in <see cref="GameClient"/>'s events into the scene (NPCs, other
/// players, doodads appearing, moving and leaving) and reports the local player's movement back to the server.
/// Positions from the server are Cry world metres (X east, Y north, Z up) with yaw 0 = north, the same conventions as
/// <see cref="PlayerController"/>.
/// </summary>
public partial class OnlineSession : Node3D
{
    private const double MoveSendInterval = 0.1; // captured for CSMoveUnit type 1 only; type 2/5 cadence is unknown

    internal GameClient Client;
    internal EnteredWorldEvent Entered;
    internal WorldStreamer World;
    internal PlayerController Player;
    internal ModelLibrary Models;
    internal CharacterBuilder Characters;
    internal DoodadModelResolver Doodads;
    internal Action<Action> Post;

    /// <summary>When set, the local runner appears as the Space Bunny placeholder instead of the database-built model.</summary>
    internal bool BunnyAvatar;

    private sealed class RemoteUnit
    {
        public UnitSnapshot Snapshot;
        public Node3D Node;
        public CharacterNode Character;
        public NVector3 Target;
        public float TargetYaw;
        public NVector3 Velocity;
        public UnitMovement Movement;
        public bool Grounded = true;
        public bool Flying;
        public bool Attached;
        /// <summary>Vehicles and ships: the full reported orientation (Godot axes), slerped towards like the position.</summary>
        public Quaternion? TargetRotation;
        /// <summary>The local driver simulates this vehicle; its server echo must not pull the node back.</summary>
        public bool LocallyDriven;
        public float RunSpeed;
        public float ObservedRunSpeed;
        public ulong LandPoseEndsAt;
        public Node3D? HousingVisual;
        public ushort? HousingTimelineId;
        public uint HousingModelId;
    }

    private sealed record AttachmentView(uint ParentUnitId, Node3D Child, Node OriginalParent, CharacterNode Character);

    private readonly Dictionary<uint, RemoteUnit> _units = [];
    private readonly Dictionary<uint, AttachmentView> _attachments = [];
    private readonly Dictionary<uint, UnitAttachedEvent> _pendingAttachments = [];
    private readonly BlockingCollection<Action> _doodadWork = new();
    // Doodad worker thread only: resolved models per template/phase (a prefab can have several parts).
    private readonly Dictionary<(uint, uint), List<(MeshRef Mesh, Transform3D Transform)>> _doodadParts = [];
    // Doodad worker thread only: normal-state slave visuals (static parts, skinned bodies, seats) by slave template id.
    private readonly Dictionary<uint, SlaveVisual> _slaveParts = [];
    private double _sinceLastMove;
    private bool _wasMoving;
    private float _lastSentHeading = float.NaN;
    private int _lastPlayerTransitionId;
    private uint _controlledMountUnitId;
    private uint _playerAttachedUnitId;
    private int _chatLines;
    private double _sinceStatus = 1;

    public string Status { get; private set; } = "";

    /// <summary>The server-authoritative unit the local player is attached to, or zero while on foot.</summary>
    public uint PlayerAttachedUnitId => _playerAttachedUnitId;

    /// <summary>Current remote units and their latest server positions, copied on the main thread for UI consumers.</summary>
    public IReadOnlyList<(UnitSnapshot Snapshot, NVector3 Position)> VisibleUnits =>
        _units.Values.Select(unit => (unit.Snapshot, unit.Target)).ToArray();

    public override void _Ready()
    {
        InitializeProtocolState();
        InitializeCombat();
        InitializeHousing();
        new Thread(() =>
        {
            foreach (var work in _doodadWork.GetConsumingEnumerable())
            {
                try
                {
                    work();
                }
                catch (Exception e)
                {
                    GD.PrintErr($"Doodad: {e.Message}");
                }
            }
        }) { IsBackground = true, Name = "DoodadResolver" }.Start();

        var self = Entered.Self;
        _playerAttachedUnitId = self?.AttachedToUnitId ?? 0;
        Player.Teleport(Entered.Position, Entered.Yaw);
        if (BunnyAvatar && SpaceBunnyRunner.Available && SpaceBunnyRunner.Build() is { } bunny)
        {
            // Temporary placeholder runner: dressed models are not layered onto the bunny yet.
            Player.SetModel(bunny);
            SetPlayerCharacter(bunny);
        }
        else if (self != null && Characters != null)
            Characters.BuildUnit(self.ModelId, Equipment(self), Appearance(self.Appearance), node =>
            {
                Player.SetModel(node);
                SetPlayerCharacter(node);
            });
        if (self is { AttachedToUnitId: not 0 })
            Post(() => AttachUnit(new UnitAttachedEvent(Entered.UnitId, unchecked((byte)self.AttachedPoint), self.AttachedToUnitId, 0)));
        GD.Print($"Entered world as {Entered.Name} (unit {Entered.UnitId}) in zone {Entered.ZoneId} at {Entered.Position}");
    }

    public override void _ExitTree()
    {
        DisposeCombat();
        _doodadWork.CompleteAdding();
        // Leave the world the way the real client quits, so the server saves and drops the session cleanly.
        try
        {
            Client?.LogoutAsync().Wait(TimeSpan.FromSeconds(15));
        }
        catch (Exception e)
        {
            GD.PrintErr($"Logout: {e.Message}");
        }
        Client?.Dispose();
    }

    public override void _Process(double delta)
    {
        for (var i = 0; i < 400 && Client.TryDequeue(out var ev); i++)
            Handle(ev);

        AdvanceTimeOfDay(delta);
        UpdateHousingPlacement();

        var blend = 1f - MathF.Exp(-(float)delta * 10f);
        foreach (var unit in _units.Values)
        {
            if (unit.Node == null || unit.Attached || unit.LocallyDriven)
                continue;
            var target = World.ToGodot(unit.Target.X, unit.Target.Y, unit.Target.Z);
            unit.Node.Position = unit.Node.Position.Lerp(target, blend);
            if (unit.TargetRotation is { } rotation)
                unit.Node.Quaternion = unit.Node.Quaternion.Normalized().Slerp(rotation, blend);
            else
                unit.Node.Rotation = new Vector3(0, Mathf.LerpAngle(unit.Node.Rotation.Y, unit.TargetYaw, blend), 0);
            if (unit.Character != null)
            {
                unit.Character.Speed = new Vector2(unit.Velocity.X, unit.Velocity.Y).Length();
                if (unit.Character.MovementPose == MovementPose.Land && unit.LandPoseEndsAt != 0 &&
                    Time.GetTicksMsec() >= unit.LandPoseEndsAt)
                {
                    unit.Character.MovementPose = unit.Character.Speed > 3.5f ? MovementPose.Run
                        : unit.Character.Speed > 0.2f ? MovementPose.Walk : MovementPose.Idle;
                    unit.LandPoseEndsAt = 0;
                }
            }
        }

        _sinceStatus += delta;
        if (_sinceStatus > 0.5)
        {
            _sinceStatus = 0;
            var counts = _units.Values.GroupBy(u => u.Snapshot.Kind).Select(g => $"{g.Count()} {g.Key}".ToLowerInvariant());
            Status = $"Online as {Entered.Name}: {string.Join(", ", counts)}";
        }
    }

    public override void _PhysicsProcess(double delta)
    {
        // Report our own movement like the real client: a packet per ~100 ms while moving, one stop packet after.
        _sinceLastMove += delta;
        if (DriveVehicle(delta))
            return;
        if (_controlledMountUnitId != 0 && _units.TryGetValue(_controlledMountUnitId, out var mount))
        {
            var mountedMoving = Player.ForwardInput != 0;
            var mountedVelocity = NVector3.Zero;
            var mountedGrounded = true;
            var predictionSpeed = mount.ObservedRunSpeed > 0 ? mount.ObservedRunSpeed : mount.RunSpeed;
            if (mountedMoving && predictionSpeed > 0)
            {
                var forward = new Vector2(-Mathf.Sin(Player.Heading), Mathf.Cos(Player.Heading));
                var right = new Vector2(Mathf.Cos(Player.Heading), Mathf.Sin(Player.Heading));
                var input = new Vector2(0, Player.ForwardInput / 127f);
                var direction = forward * input.Y + right * input.X;
                mountedVelocity = new NVector3(direction.X * predictionSpeed, direction.Y * predictionSpeed, 0);
                if (TryPredictMateMotion(mount, mountedVelocity, (float)delta,
                        out var predicted, out mountedVelocity, out mountedGrounded))
                    mount.Target = predicted;
                else
                {
                    mountedMoving = false;
                    mountedVelocity = NVector3.Zero;
                }
            }
            mount.TargetYaw = Player.Heading;
            mount.Velocity = mountedVelocity;
            Player.SetMountedMotion(new Vector2(mountedVelocity.X, mountedVelocity.Y).Length());
            var mountTurning = HeadingChanged(Player.Heading);
            if ((_sinceLastMove >= MoveSendInterval && (mountedMoving || mountTurning)) || mountedMoving != _wasMoving)
            {
                Client.SendMovement(mount.Target, Player.Heading,
                    mountedVelocity,
                    mountedMoving && mountedGrounded ? MoveFlags.Moving : MoveFlags.None, Player.ForwardInput,
                    stance: 1, actorFlags: mountedGrounded ? (ushort)4 : (ushort)0, unitId: _controlledMountUnitId);
                _sinceLastMove = 0;
                _lastSentHeading = Player.Heading;
            }
            _wasMoving = mountedMoving;
            return;
        }
        if (Player.IsMounted)
            return; // passengers have no movement authority; wait for the parent unit's server movement
        var moving = Player.NetworkVelocity.X != 0 || Player.NetworkVelocity.Y != 0 || !Player.OnGround;
        var turning = HeadingChanged(Player.Heading);
        if (Player.TransitionId != _lastPlayerTransitionId)
        {
            _lastPlayerTransitionId = Player.TransitionId;
            if (Player.LastTransition == MovementTransition.JumpStarted)
                Client.SendMovement(Player.TransitionPosition, Player.Heading, NVector3.Zero, MoveFlags.Jumping, 0,
                    stance: 1, actorFlags: 0x040C);
            else if (Player.LastTransition == MovementTransition.Landed)
                Client.SendMovement(Player.TransitionPosition, Player.Heading, NVector3.Zero, MoveFlags.Stopping, 0,
                    stance: 1, actorFlags: 0x0814);
            _sinceLastMove = 0;
            _lastSentHeading = Player.Heading;
        }
        else if ((moving || turning) && (_sinceLastMove >= MoveSendInterval || !_wasMoving))
        {
            var hasInput = Player.ForwardInput != 0 || Player.VerticalInput > 0;
            var flags = hasInput && (Player.OnGround || Player.IsSwimming) ? MoveFlags.Moving : MoveFlags.None;
            var forwardInput = Player.OnGround || Player.IsSwimming ? Player.ForwardInput : (sbyte)0;
            // Captures ground upward and diagonal-up swim input. Descending remains local presentation until captured.
            var verticalInput = Player.IsSwimming && Player.VerticalInput > 0 ? Player.VerticalInput : (sbyte)0;
            if (forwardInput != 0 && verticalInput != 0)
            {
                forwardInput = (sbyte)(Math.Sign(forwardInput) * 90);
                verticalInput = 90;
            }
            Client.SendMovement(Player.NetworkPosition, Player.Heading, Player.NetworkVelocity, flags, forwardInput,
                stance: Player.IsSwimming ? (sbyte)2 : (sbyte)1, actorFlags: Player.OnGround ? (ushort)4 : (ushort)0,
                verticalInput: verticalInput);
            _sinceLastMove = 0;
            _lastSentHeading = Player.Heading;
        }
        else if (!moving && _wasMoving)
        {
            Client.SendMovement(Player.NetworkPosition, Player.Heading, NVector3.Zero, MoveFlags.None, 0,
                stance: 1, actorFlags: 4);
            _sinceLastMove = 0;
            _lastSentHeading = Player.Heading;
        }
        _wasMoving = moving;
    }

    private bool HeadingChanged(float heading) =>
        float.IsNaN(_lastSentHeading) || Math.Abs(Mathf.AngleDifference(_lastSentHeading, heading)) > 0.01f;

    private bool TryPredictMateMotion(RemoteUnit mount, NVector3 horizontalVelocity, float delta,
        out NVector3 position, out NVector3 velocity, out bool grounded)
    {
        position = mount.Target;
        velocity = horizontalVelocity;
        grounded = true;
        var next = mount.Target + horizontalVelocity * delta;
        if (World == null || !World.HasHeightsAt(next.X, next.Y))
            return false;
        var floor = World.TerrainHeightAt(next.X, next.Y);
        if (World.TryWaterSurfaceAt(next.X, next.Y, out var water, mount.Target.Z) && water - floor > 0.8f)
            return false; // no capture-backed swimming state exists for a ridden land Mate

        if (IsInsideTree())
        {
            var from = World.ToGodot(mount.Target.X, mount.Target.Y, mount.Target.Z + 0.7f);
            var to = World.ToGodot(next.X, next.Y, mount.Target.Z + 0.7f);
            if (GetWorld3D().DirectSpaceState.IntersectRay(PhysicsRayQueryParameters3D.Create(from, to)).Count > 0)
                return false;
            var supportFrom = World.ToGodot(next.X, next.Y, mount.Target.Z + 1f);
            var supportTo = World.ToGodot(next.X, next.Y, mount.Target.Z - 6f);
            var support = GetWorld3D().DirectSpaceState.IntersectRay(PhysicsRayQueryParameters3D.Create(supportFrom, supportTo));
            if (support.Count > 0)
                floor = Math.Max(floor, ((Vector3)support["position"]).Y);
        }

        var vertical = mount.Target.Z > floor + 0.35f ? mount.Velocity.Z - Player.Gravity * delta : 0;
        next.Z = mount.Target.Z + vertical * delta;
        if (next.Z <= floor)
        {
            next.Z = floor;
            vertical = 0;
        }
        grounded = vertical == 0 && next.Z <= floor + 0.01f;
        position = next;
        velocity.Z = vertical;
        return true;
    }

    private void Handle(GameEvent ev)
    {
        ApplyProtocolEvent(ev);
        ApplyCombatEvent(ev);
        ApplyHousingEvent(ev);
        ApplyMateSlaveEvent(ev);
        switch (ev)
        {
            case UnitStateEvent state when state.Snapshot.Unit.UnitId == Entered.UnitId:
                _playerAttachedUnitId = state.Snapshot.Unit.AttachedToUnitId;
                VehicleUiStateChanged?.Invoke(state);
                break;
            case TimeOfDayEvent timeOfDay:
                ApplyTimeOfDay(timeOfDay);
                break;
            case SnowingEverywhereEvent snow:
                ApplySnowingEverywhere(snow.Enabled);
                break;
            case UnitAppearedEvent appeared when appeared.Unit.UnitId != Entered.UnitId:
                Spawn(appeared.Unit);
                break;
            case UnitMovedEvent moved when moved.Movement.UnitId != Entered.UnitId && _units.TryGetValue(moved.Movement.UnitId, out var unit):
                if (TraceVehicles && unit.LocallyDriven && moved.Movement.HasPosition && Time.GetTicksMsec() >= _nextEchoLog)
                {
                    _nextEchoLog = Time.GetTicksMsec() + 1000;
                    GD.Print($"[vehicle] server copy of driven {moved.Movement.UnitId}: {moved.Movement.Kind} at " +
                             $"({moved.Movement.Position.X:F1}, {moved.Movement.Position.Y:F1}, {moved.Movement.Position.Z:F1})");
                }
                if (!moved.Movement.HasPosition || unit.LocallyDriven)
                    break; // the driver's own client authored this vehicle position; the server only echoes it
                if (moved.Movement.Kind is MoveKind.Vehicle or MoveKind.Vehicle3 or MoveKind.Ship)
                {
                    var q = moved.Movement.Rotation;
                    unit.TargetRotation = new Quaternion(q.X, q.Z, -q.Y, q.W).Normalized(); // Cry (x, y, z) -> Godot (x, z, -y)
                }
                if (moved.Movement.HasPosition)
                    unit.Target = moved.Movement.Position;
                unit.TargetYaw = moved.Movement.Yaw;
                unit.Velocity = moved.Movement.Velocity;
                var mountSpeed = new Vector2(unit.Velocity.X, unit.Velocity.Y).Length();
                if (unit.Snapshot.Kind == UnitKind.Mate && mountSpeed > 0.2f)
                    unit.ObservedRunSpeed = mountSpeed;
                foreach (var attachment in _attachments.Values.Where(a => a.ParentUnitId == moved.Movement.UnitId))
                {
                    if (attachment.Child == Player)
                        Player.SetMountedMotion(mountSpeed);
                    else if (attachment.Character != null)
                    {
                        attachment.Character.Speed = mountSpeed;
                        attachment.Character.SetMovementPose(mountSpeed > 0.2f ? MovementPose.MountedRun : MovementPose.Mounted);
                    }
                }
                unit.Movement = moved.Movement;
                if (moved.Movement.Kind == MoveKind.Unit)
                {
                    unit.Character?.ApplyMovement(moved.Movement, unit.Grounded);
                    if (unit.Character?.MovementPose == MovementPose.Land)
                        unit.LandPoseEndsAt = Time.GetTicksMsec() + 240;
                    if (unit.Flying && unit.Character != null)
                        unit.Character.MovementPose = MovementPose.Glide;
                    unit.Grounded = MovementPresentation.IsGrounded(moved.Movement);
                }
                break;
            case UnitAttachedEvent attached:
                if (attached.ChildUnitId == Entered.UnitId)
                {
                    _playerAttachedUnitId = attached.ParentUnitId;
                    _playerAttachPoint = attached.Point;
                }
                AttachUnit(attached);
                VehicleUiStateChanged?.Invoke(attached);
                break;
            case UnitDetachedEvent detached:
                if (detached.ChildUnitId == Entered.UnitId)
                {
                    _playerAttachedUnitId = 0;
                    _playerAttachPoint = 0xFF;
                }
                DetachUnit(detached.ChildUnitId);
                VehicleUiStateChanged?.Invoke(detached);
                break;
            case UnitFlyingStateChangedEvent flying when flying.UnitId == Entered.UnitId:
                Player.SetServerGliding(flying.IsFlying);
                break;
            case UnitFlyingStateChangedEvent flying when _units.TryGetValue(flying.UnitId, out var flyingUnit):
                flyingUnit.Flying = flying.IsFlying;
                if (flyingUnit.Character != null)
                    flyingUnit.Character.MovementPose = flying.IsFlying ? MovementPose.Glide : MovementPose.Idle;
                break;
            case UnitsRemovedEvent removed:
                foreach (var id in removed.UnitIds)
                {
                    if (id == _playerAttachedUnitId) _playerAttachedUnitId = 0;
                    DetachChildren(id);
                    DetachUnit(id);
                    RemoveCombatUnit(id);
                    ForgetVehicle(id);
                    if (_units.Remove(id, out var gone) && GodotObject.IsInstanceValid(gone.Node))
                    {
                        ReleaseParentedDoodads(gone);
                        gone.Node.QueueFree();
                    }
                }
                break;
            case TeleportedEvent teleported:
                Player.Teleport(teleported.Position, Player.Heading);
                // The real client answers every SCTeleportUnit with CSTeleportEnded; World keeps dropping movement
                // (walking and the vehicle stream alike) until it arrives.
                Client.SendTeleportEnded(teleported.Position, Player.Heading);
                break;
            case ChatEvent chat when _chatLines++ < 200:
                GD.Print($"[chat {chat.ChatType}] {chat.SenderName}: {chat.Message}");
                break;
            case DisconnectedEvent disconnected:
                Status = $"Disconnected: {disconnected.Reason}";
                GD.PrintErr(Status);
                break;
            case ErrorEvent { Fatal: true } error:
                GD.PrintErr($"Network error: {error.Message}");
                break;
        }
    }

    private void AttachUnit(UnitAttachedEvent attached)
    {
        if (attached.Point == 0xFF)
            return;
        if (!_units.TryGetValue(attached.ParentUnitId, out var parent) || parent.Node == null ||
            parent.Snapshot.Kind == UnitKind.Mate && parent.Character == null ||
            attached.ChildUnitId != Entered.UnitId && (!_units.TryGetValue(attached.ChildUnitId, out var pendingChild) || pendingChild.Node == null))
        {
            _pendingAttachments[attached.ChildUnitId] = attached;
            return;
        }
        Node3D child;
        CharacterNode character = null;
        if (attached.ChildUnitId == Entered.UnitId)
            child = Player;
        else if (_units.TryGetValue(attached.ChildUnitId, out var remote) && remote.Node != null)
        {
            child = remote.Node;
            character = remote.Character;
        }
        else
            return;

        DetachUnit(attached.ChildUnitId);
        if (_units.TryGetValue(attached.ChildUnitId, out var attachedRemote))
            attachedRemote.Attached = true;
        var originalParent = child.GetParent();
        Node attachmentParent = parent.Node;
        // Actor mounts expose rider bones. Slave hull attachment points also name cannons, sails,
        // ladders, and other equipment; their static prefabs do not expose verified rider anchors.
        // Keep the server's parent relation (so the camera follows the hull), but do not attach a
        // player to an unrelated bone on a vehicle or ship.
        var boneName = attached.Point == 1 ? "bone_spine_driver" : "bone_spine_passenger";
        if (parent.Snapshot.Kind == UnitKind.Slave && SeatAnchor(parent, attached.Point) is { } seat)
            attachmentParent = seat;
        else if (parent.Snapshot.Kind != UnitKind.Slave &&
            parent.Node.FindChild("Skeleton", recursive: true, owned: false) is Skeleton3D skeleton && skeleton.FindBone(boneName) >= 0)
        {
            var bone = new BoneAttachment3D { Name = $"Rider_{attached.ChildUnitId}", BoneName = boneName };
            skeleton.AddChild(bone);
            attachmentParent = bone;
        }
        child.Reparent(attachmentParent, keepGlobalTransform: false);
        child.Position = Vector3.Zero;
        child.Rotation = Vector3.Zero;
        _attachments[attached.ChildUnitId] = new AttachmentView(attached.ParentUnitId, child, originalParent, character);
        _pendingAttachments.Remove(attached.ChildUnitId);
        if (character != null) character.Speed = 0;
        character?.SetMovementPose(MovementPose.Mounted);
        if (attached.ChildUnitId == Entered.UnitId)
        {
            Player.SetMounted(true);
            _controlledMountUnitId = attached.Point == 1 && parent.Snapshot.Kind == UnitKind.Mate
                ? attached.ParentUnitId : 0;
        }
        if (parent.Snapshot.Kind == UnitKind.Slave)
            PlaySeatPose(attached.ChildUnitId, parent, attached.Point);
    }

    private void DetachUnit(uint childId)
    {
        _pendingAttachments.Remove(childId);
        StopSeatPose(childId);
        if (!_attachments.Remove(childId, out var view) || !GodotObject.IsInstanceValid(view.Child) ||
            !GodotObject.IsInstanceValid(view.OriginalParent))
            return;
        var transform = view.Child.GlobalTransform;
        var attachment = view.Child.GetParent() as BoneAttachment3D;
        view.Child.Reparent(view.OriginalParent, keepGlobalTransform: true);
        view.Child.GlobalTransform = transform;
        attachment?.QueueFree();
        if (_units.TryGetValue(childId, out var remote))
        {
            remote.Attached = false;
            remote.Target = World.ToCry(remote.Node.GlobalPosition);
            remote.Character?.SetMovementPose(MovementPose.Idle);
        }
        view.Character?.SetMovementPose(MovementPose.Idle);
        if (childId == Entered.UnitId)
        {
            _controlledMountUnitId = 0;
            Player.SetMounted(false);
            Client.ResetMovementHeartbeat(Player.CryPosition, Player.Heading);
        }
    }

    private void DetachChildren(uint parentId)
    {
        foreach (var childId in _attachments.Where(pair => pair.Value.ParentUnitId == parentId)
                     .Select(pair => pair.Key).ToArray())
            DetachUnit(childId);
    }

    private void Spawn(UnitSnapshot s)
    {
        if (_units.TryGetValue(s.UnitId, out var known) && SameLook(known.Snapshot, s))
        {
            known.Snapshot = s;
            known.Target = s.Position;
            known.TargetYaw = s.Yaw;
            RegisterCombatUnit(s, known.Node);
            return;
        }
        if (_units.ContainsKey(s.UnitId))
        {
            DetachChildren(s.UnitId);
            DetachUnit(s.UnitId);
        }
        if (_units.Remove(s.UnitId, out var old) && GodotObject.IsInstanceValid(old.Node))
        {
            ReleaseParentedDoodads(old);
            old.Node.QueueFree(); // the look changed: rebuild
        }

        var unit = new RemoteUnit
        {
            Snapshot = s, Target = s.Position, TargetYaw = s.Yaw,
            RunSpeed = s.Kind == UnitKind.Mate ? ResolveRunSpeed(s.ModelId) : 0,
            HousingModelId = s.Kind == UnitKind.Housing ? s.ModelId : 0,
        };
        _units[s.UnitId] = unit;
        unit.Node = new Node3D
        {
            Name = $"{s.Kind}_{s.UnitId}",
            Position = World.ToGodot(s.Position.X, s.Position.Y, s.Position.Z),
            Rotation = new Vector3(0, s.Yaw, 0),
        };
        AddChild(unit.Node);
        if (s.Kind == UnitKind.Doodad && s.ParentUnitId != 0)
            ParentDoodad(unit);
        AdoptPendingDoodads(s.UnitId);
        ReplayPendingAttachments(s.UnitId);
        if (s.AttachedToUnitId != 0)
            Post(() => AttachUnit(new UnitAttachedEvent(s.UnitId, unchecked((byte)s.AttachedPoint), s.AttachedToUnitId, 0)));
        RegisterCombatUnit(s, unit.Node);

        switch (s.Kind)
        {
            case UnitKind.Doodad:
                QueueDoodad(unit);
                break;
            case UnitKind.Housing:
                QueueHousingFromSpawn(unit);
                break;
            case UnitKind.Slave:
                QueueSlave(unit);
                break;
            case UnitKind.Npc or UnitKind.Mate or UnitKind.Character:
                var placeholder = new MeshInstance3D
                {
                    Mesh = new CapsuleMesh { Radius = 0.3f, Height = 1.7f },
                    Position = new Vector3(0, 0.85f, 0),
                    MaterialOverride = new StandardMaterial3D { AlbedoColor = s.Kind == UnitKind.Character ? new Color(0.3f, 0.5f, 0.9f) : new Color(0.8f, 0.4f, 0.3f) },
                };
                unit.Node.AddChild(placeholder);
                if (Characters == null)
                    break;
                void Ready(CharacterNode character)
                {
                    if (!_units.TryGetValue(s.UnitId, out var current) || current != unit)
                    {
                        character.QueueFree(); // the unit left or was re-sent meanwhile
                        return;
                    }
                    placeholder.QueueFree();
                    unit.Node.AddChild(character);
                    unit.Character = character;
                    if (unit.Movement != null)
                        character.ApplyMovement(unit.Movement, unit.Grounded);
                    if (unit.Flying)
                        character.MovementPose = MovementPose.Glide;
                    if (unit.Attached)
                        character.SetMovementPose(MovementPose.Mounted);
                    SetCombatCharacter(s.UnitId, character);
                    ReplayPendingAttachments(s.UnitId);
                }
                if (s.Kind is UnitKind.Npc or UnitKind.Mate)
                    Characters.BuildNpc(s.TemplateId, Ready);
                else
                    Characters.BuildUnit(s.ModelId, Equipment(s), Appearance(s.Appearance), Ready);
                break;
        }
    }

    private void ReplayPendingAttachments(uint unitId)
    {
        foreach (var pending in _pendingAttachments.Values
                     .Where(a => a.ChildUnitId == unitId || a.ParentUnitId == unitId).ToArray())
            AttachUnit(pending);
    }

    private float ResolveRunSpeed(uint actorModelId)
    {
        if (actorModelId == 0 || !File.Exists(GameDatabasePath))
            return 0;
        try
        {
            using var db = new SqliteConnection($"Data Source={GameDatabasePath};Mode=ReadOnly");
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = """
                SELECT min(gs.ai_move_speed_run, gs.max_speed)
                FROM models m
                JOIN game_stances gs ON gs.actor_model_id=m.sub_id
                WHERE m.id=$model AND m.sub_type='ActorModel' AND gs.stance_id=1
                LIMIT 1
                """;
            command.Parameters.AddWithValue("$model", actorModelId);
            return command.ExecuteScalar() is { } value ? Convert.ToSingle(value) : 0;
        }
        catch (SqliteException)
        {
            return 0;
        }
    }

    /// <summary>Resolves a doodad's model on the worker thread, then attaches it to the unit's node.</summary>
    private void QueueDoodad(RemoteUnit unit)
    {
        var s = unit.Snapshot;
        if (Doodads == null)
            return;
        _doodadWork.Add(() =>
        {
            var parts = DoodadParts(s.TemplateId, s.PhaseId);
            if (parts.Count == 0)
                return;
            var scale = s.Scale > 0 ? s.Scale : 1f;
            Post(() =>
            {
                if (!_units.TryGetValue(s.UnitId, out var current) || current != unit)
                    return;
                var root = new Node3D { Name = "Model", Scale = Vector3.One * scale };
                unit.Node.AddChild(root);
                foreach (var (mesh, transform) in parts)
                    if (Models.GetMesh(mesh) is { } godotMesh)
                        root.AddChild(new MeshInstance3D { Mesh = godotMesh, Transform = transform, Layers = ModelLibrary.ObjectLayer });
            });
        });
    }

    /// <summary>
    /// Doodad worker thread. The meshes of a doodad template in a phase: one static model, or the parts of a prefab
    /// (workbenches, houses, crops ...), each with its transform relative to the doodad. Empty when nothing can be drawn.
    /// </summary>
    private List<(MeshRef Mesh, Transform3D Transform)> DoodadParts(uint templateId, uint phaseId)
    {
        if (_doodadParts.TryGetValue((templateId, phaseId), out var cached))
            return cached;
        var parts = new List<(MeshRef, Transform3D)>();
        var visual = Doodads.Resolve(templateId, phaseId);
        if (visual.ModelPath != null && visual.Kind is DoodadModelKind.Cgf or DoodadModelKind.Cga)
        {
            if (Models.Request(visual.ModelPath, null, false) is { } mesh)
                parts.Add((mesh, Transform3D.Identity));
        }
        else if (visual.ModelPath != null && visual.Kind == DoodadModelKind.Prefab && PakFiles.Read(visual.ModelPath) is { } library)
        {
            var prefab = PrefabReader.ResolveWithReferences(library, visual.Member ?? "", PakFiles.Read, visual.ModelPath);
            foreach (var part in prefab.Parts)
            {
                var transform = CryAxes.FromRowVector(part.Transform, NVector3.Zero);
                if (Models.Request(part.ModelPath, part.MaterialPath, transform.Basis.Determinant() < 0) is { } mesh)
                    parts.Add((mesh, transform));
            }
        }
        return _doodadParts[(templateId, phaseId)] = parts;
    }

    private static bool SameLook(UnitSnapshot a, UnitSnapshot b) =>
        a.Kind == b.Kind && a.TemplateId == b.TemplateId && a.ModelId == b.ModelId && a.PhaseId == b.PhaseId &&
        a.Equipment.Count == b.Equipment.Count &&
        a.Equipment.Zip(b.Equipment).All(p => p.First.Slot == p.Second.Slot && p.First.TemplateId == p.Second.TemplateId && p.First.ImageTemplateId == p.Second.ImageTemplateId);

    /// <summary>Equipped item templates by equip slot, preferring the look (costume/skin) template when one is set.</summary>
    private static Dictionary<int, long> Equipment(UnitSnapshot s)
    {
        var equipment = new Dictionary<int, long>();
        foreach (var e in s.Equipment)
        {
            var template = e.ImageTemplateId != 0 ? e.ImageTemplateId : e.TemplateId;
            if (template != 0)
                equipment[e.Slot] = template;
        }
        return equipment;
    }

    /// <summary>The server's UnitCustomModelParams as the character resolver's appearance values.</summary>
    private static UnitAppearance Appearance(AppearanceParams a)
    {
        if (a == null || a.Tier == AppearanceTier.None)
            return null;
        var look = new UnitAppearance
        {
            HairColorId = a.HairColor,
            SkinColorId = a.SkinColor,
            DefaultHairColor = a.DefaultHairColor,
            TwoToneHairColor = a.TwoToneHairColor,
            TwoToneFirstWidth = a.TwoToneFirstWidth,
            TwoToneSecondWidth = a.TwoToneSecondWidth,
            BodyNormalMapId = a.BodyNormalMap,
        };
        if (a.Face is { } face)
        {
            look.FaceNormalMapId = face.NormalMapId;
            look.FaceNormalMapWeight = face.NormalMapWeight;
            look.DecalIds[0] = face.MovableDecalAssetId;
            look.DecalWeights[0] = face.MovableDecalWeight;
            for (var i = 0; i < face.FixedDecalAssetIds.Length && i + 1 < look.DecalIds.Length; i++)
            {
                look.DecalIds[i + 1] = face.FixedDecalAssetIds[i];
                if (i < face.FixedDecalWeights.Length)
                    look.DecalWeights[i + 1] = face.FixedDecalWeights[i];
            }
            look.LipColor = face.LipColor;
            look.LeftPupilColor = face.LeftPupilColor;
            look.RightPupilColor = face.RightPupilColor;
            look.EyebrowColor = face.EyebrowColor;
            look.DecoColor = face.DecoColor;
            look.Modifier = face.Modifier;
        }
        return look;
    }
}
