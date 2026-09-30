#nullable enable

using AAEmu.GodotViewer.Net;
using Godot;
using NQuaternion = System.Numerics.Quaternion;
using NVector3 = System.Numerics.Vector3;

namespace AAEmu.GodotViewer.Client;

/// <summary>One entry of the vehicle mode action bar (x2ui ISLOT_MODE_ACTION): a slave skill or a native function.</summary>
/// <param name="Type">"slave_skill" or "function", the value the original slot reports from GetBindedType.</param>
/// <param name="Function">For functions: "unbind" (dismount) or "showSlaveInfo" (the TOOLTIP_TEXT keys of their tooltips).</param>
public sealed record VehicleModeAction(string Type, uint SkillId, string Function);

/// <summary>
/// Vehicles and ships: summoning from the item, the hull/body visuals and seats, boarding, driving and dismounting.
/// Everything sent here mirrors a real-client capture (2026-09-29, farm wagon slave 60 and rowboat slave 15, see
/// docs/VehiclesProtocol.md); the server decides every outcome, the client only shows state and requests.
/// </summary>
public partial class OnlineSession
{
    /// <summary>Skill 35837 "내리기" (target type parent): what the mode bar's dismount slot casts on the slave (captured).</summary>
    private const uint VehicleUnmountSkillId = 35837;

    /// <summary>The real client streams CSMoveUnit type 2 every 98-130 ms while seated, idle included (captured).</summary>
    private const double VehicleMoveInterval = 0.105;

    /// <summary>
    /// Farm wagon cruise speed measured from the real client's type 2 stream (vel.y 4369 = 4.0 m/s, 0.44 m per 110 ms).
    /// The content database has no cruise-speed column for non-wheeled vehicles; this is used for every land vehicle.
    /// </summary>
    private const float LandVehicleCruiseSpeed = 4.0f;

    /// <summary>Reverse cruise relative to forward: the real farm wagon backed up at 3.95 m/s (vel.y -4310), so 1.</summary>
    private const float LandVehicleReverseRatio = 1f;

    private const string SeatNodePrefix = "Seat_";

    private sealed record SlaveVisual(
        List<(MeshRef Mesh, Transform3D Transform)> Meshes,
        List<(string ChrPath, Transform3D Transform)> Bodies,
        List<(string Name, Transform3D Transform)> Seats);

    private sealed class VehicleDrive
    {
        public uint UnitId;
        public VehicleTemplate Template = null!;
        public NVector3 Position;
        public NVector3 Velocity;
        public float Yaw;
        public float Speed;
        public float YawRate;
        public float Steering;
        public float Pitch;
        public float Roll;
        public NQuaternion Rotation = NQuaternion.Identity;
        public double SinceSend = double.MaxValue;
        public bool HadThrottle;
        public bool ReleasePending;
        public sbyte ShipThrottle;
        public sbyte ShipSteering;
        public string? PoseClip;
    }

    // X2_TRACE_VEHICLES=1: logs the server's copy of the locally driven vehicle once a second (sync diagnostics).
    private static readonly bool TraceVehicles = System.Environment.GetEnvironmentVariable("X2_TRACE_VEHICLES") == "1";
    private ulong _nextEchoLog;
    private VehicleCatalog? _vehicleCatalog;
    private VehicleDrive? _drive;
    private byte _playerAttachPoint = 0xFF;
    private readonly Dictionary<uint, List<uint>> _pendingDoodadParents = [];
    private readonly Dictionary<uint, byte> _seatPoints = [];
    // Main thread: the skinned body .chr per slave template (seat pose selection).
    private readonly Dictionary<uint, string> _slaveBodyPaths = [];

    private VehicleCatalog? VehicleCatalog =>
        _vehicleCatalog ??= File.Exists(GameDatabasePath) ? new VehicleCatalog(GameDatabasePath) : null;

    /// <summary>The slave the local player drives (seat 1), or zero.</summary>
    public uint DrivenVehicleUnitId => _drive?.UnitId ?? 0;

    /// <summary>Content facts about a live slave unit, or null.</summary>
    public VehicleTemplate? VehicleTemplateOf(uint unitId) =>
        _units.TryGetValue(unitId, out var unit) ? VehicleTemplateFor(unit) : null;

    private VehicleTemplate? VehicleTemplateFor(RemoteUnit unit)
    {
        if (unit.Snapshot.Kind != UnitKind.Slave)
            return null;
        var templateId = unit.Snapshot.TemplateId;
        if (templateId == 0 && MateSlaveState.MySlavesByObjectId.TryGetValue(unit.Snapshot.UnitId, out var mine))
            templateId = mine.TemplateId;
        return templateId == 0 ? null : VehicleCatalog?.Get(templateId);
    }

    /// <summary>Current speed in m/s of a vehicle (the local simulation while driving, else the server's velocity).</summary>
    public double VehicleSpeed(uint unitId)
    {
        if (_drive is { } drive && drive.UnitId == unitId && drive.Template.IsClientDrivenLand)
            return Math.Abs(drive.Speed);
        return _units.TryGetValue(unitId, out var unit)
            ? new Vector2(unit.Velocity.X, unit.Velocity.Y).Length() : 0;
    }

    /// <summary>Current yaw rate in degrees per second (the original's GetSiegeWeaponTurnSpeed read 2.8 while the
    /// wagon's reported yaw rate was decaying after a turn, so it is taken as |angular velocity| in deg/s).</summary>
    public double VehicleTurnRate(uint unitId)
    {
        if (_drive is { } drive && drive.UnitId == unitId && drive.Template.IsClientDrivenLand)
            return Math.Abs(Mathf.RadToDeg(drive.YawRate));
        return _units.TryGetValue(unitId, out var unit) && unit.Movement is { } movement
            ? Math.Abs(Mathf.RadToDeg(movement.AngularVelocity.Z)) : 0;
    }

    // ------------------------------------------------------------------ summon / despawn

    /// <summary>
    /// Uses a slave summon item the way the original client does: when that item already has this player's slave out
    /// (SCUpdatedSlaveSourceItem) the use becomes CSDespawnSlave; otherwise the item's summon_pos skill is cast at a
    /// point ahead of the character. Returns false for items that summon no slave (the caller then uses the item normally).
    /// </summary>
    public bool TryUseSummonSlaveItem(ItemSnapshot item)
    {
        if (Client?.Stage != ClientStage.InWorld || VehicleCatalog is not { } catalog)
            return false;
        var slaveId = catalog.SlaveForSummonItem(item.TemplateId);
        if (slaveId == 0)
            return false;

        var source = MateSlaveState.SourceItem(Entered.UnitId);
        var mine = MateSlaveState.MySlavesByObjectId.Values.FirstOrDefault(slave => _units.ContainsKey(slave.ObjectId));
        if (source != null && source.ItemId == item.ItemId && mine != null)
        {
            Client.SendGame(VehiclePacketWriters.DespawnSlave(mine.ObjectId));
            GD.Print($"[vehicle] despawn slave {mine.ObjectId} (item {item.ItemId} is its source)");
            return true;
        }

        var skillId = _combatData?.GetItem(item.TemplateId)?.UseSkillId ?? 0;
        if (skillId == 0 || catalog.Get(slaveId) is not { } template)
            return false;
        var (position, rotation) = SummonPlacement(template);
        Client.SendGame(CombatPacketWriters.StartItemSkillOnPosition((uint)skillId, Entered.UnitId, item.ItemId,
            item.TemplateId, 0, 0, position, rotation));
        GD.Print($"[vehicle] summon slave {slaveId} with item {item.TemplateId}/{item.ItemId} skill {skillId} at " +
                 $"({position.X:F1}, {position.Y:F1}, {position.Z:F1}) rot {rotation:F3}");
        return true;
    }

    /// <summary>
    /// The summon_pos point. Captured: farm wagon (spawn_x_offset 5) planted 5.0 m ahead at the terrain height; rowboat
    /// (offset 1.5) 2.5 m ahead at the water surface; both with rotation = heading + π/2 (heading π/2 gave 3.154).
    /// The one extra metre for boats and the rotation formula rest on these two samples.
    /// </summary>
    private (NVector3 Position, float Rotation) SummonPlacement(VehicleTemplate template)
    {
        var heading = Player.Heading;
        var forward = new NVector3(-MathF.Sin(heading), MathF.Cos(heading), 0);
        var distance = template.SpawnOffsetX + (template.IsBoat ? 1f : 0f);
        var at = Player.CryPosition + forward * distance;
        if (template.IsBoat)
        {
            // The water surface; where the viewer's ocean masks leave a below-sea-level spot dry (seen at the
            // Solzreed east coast, 15750/15400) the sea level is the surface the server checks against too.
            if (World.TryWaterSurfaceAt(at.X, at.Y, out var water))
                at.Z = water;
            else if (World.HasHeightsAt(at.X, at.Y) && World.TerrainHeightAt(at.X, at.Y) < World.OceanLevel)
                at.Z = World.OceanLevel;
            else
                at.Z = Player.CryPosition.Z;
        }
        else
            at.Z = VehicleGround(at.X, at.Y, Player.CryPosition.Z);
        return (at, heading + MathF.PI / 2);
    }

    // ------------------------------------------------------------------ board / dismount

    /// <summary>
    /// The interaction key (or right click) on a slave: CSBindSlave with the slave's interaction skill (12076 "점유" for
    /// the farm wagon, captured as <c>5e00 4b00 2c2f0000</c>). The client checks the skill's max_range first: the real
    /// client sent nothing at 4.9 m from the wagon and bound at 2.4 m (max_range 4). Hulls without an interaction skill
    /// (the rowboat) are boarded through their helm doodad instead (doodad func skill 14916).
    /// </summary>
    private bool BeginVehicleInteraction(ITargetable target)
    {
        if (Client.Stage != ClientStage.InWorld || !_units.TryGetValue(target.Id, out var unit) ||
            VehicleTemplateFor(unit) is not { InteractionSkillId: > 0 } template)
            return false;
        var range = VehicleCatalog?.SkillMaxRange(template.InteractionSkillId) ?? 0;
        var distance = NVector3.Distance(Player.CryPosition, unit.Target);
        if (range > 0 && distance > range)
        {
            GD.Print($"[vehicle] {template.Name}: {distance:F1} m is beyond the interaction range {range} m");
            return false;
        }
        short? timeline = unit.Snapshot.TimelineId;
        if (timeline == null && MateSlaveState.MySlavesByObjectId.TryGetValue(unit.Snapshot.UnitId, out var mine))
            timeline = mine.TimelineId;
        if (timeline == null)
            return false;
        Client.SendGame(VehiclePacketWriters.BindSlave(timeline.Value, (int)template.InteractionSkillId));
        GD.Print($"[vehicle] bind slave {unit.Snapshot.UnitId} tl {timeline} skill {template.InteractionSkillId} at {distance:F1} m");
        FaceTarget(target);
        return true;
    }

    /// <summary>The mode bar's dismount function: CSStartSkill 35837 from the player on the parent slave (captured).</summary>
    public bool RequestVehicleUnmount()
    {
        if (Client?.Stage != ClientStage.InWorld || _playerAttachedUnitId == 0 ||
            !_units.TryGetValue(_playerAttachedUnitId, out var unit) || unit.Snapshot.Kind != UnitKind.Slave)
            return false;
        Client.SendGame(CombatPacketWriters.StartSkillOnUnit(VehicleUnmountSkillId, Entered.UnitId, _playerAttachedUnitId));
        GD.Print($"[vehicle] unmount from {_playerAttachedUnitId}");
        return true;
    }

    /// <summary>
    /// The mode action bar while the player holds a slave's driver seat, in the original's order: the slave's mount
    /// skills (slave_mount_skills rows), then the dismount and slave-info functions (captured 8 slots for the wagon).
    /// </summary>
    public IReadOnlyList<VehicleModeAction> VehicleModeActions()
    {
        if (_playerAttachedUnitId == 0 || _playerAttachPoint != 1 ||
            !_units.TryGetValue(_playerAttachedUnitId, out var unit) || VehicleTemplateFor(unit) is not { } template)
            return [];
        var actions = template.MountSkillIds.Select(id => new VehicleModeAction("slave_skill", id, "")).ToList();
        actions.Add(new VehicleModeAction("function", 0, "unbind"));
        actions.Add(new VehicleModeAction("function", 0, "showSlaveInfo"));
        return actions;
    }

    /// <summary>
    /// A slave skill from the mode bar: CSStartSkill with the slave as a mount caster (type 3 + mount_skills.id), aimed
    /// at the slave for self-targeted skills (captured for the wagon horn) and at the current target otherwise.
    /// </summary>
    public bool UseVehicleSkill(uint skillId)
    {
        if (Client?.Stage != ClientStage.InWorld || _playerAttachedUnitId == 0 ||
            !_units.TryGetValue(_playerAttachedUnitId, out var unit) || VehicleTemplateFor(unit) is not { } template)
            return false;
        var index = template.MountSkillIds.ToList().IndexOf(skillId);
        if (index < 0)
            return false;
        var targetType = VehicleCatalog?.SkillTargetType(skillId) ?? 0;
        var target = targetType == 0 || Targeting?.Target is not { } selected ? _playerAttachedUnitId : selected.Id;
        Client.SendGame(CombatPacketWriters.StartMountSkillOnUnit(skillId, _playerAttachedUnitId,
            template.MountSkillRowIds[index], target));
        GD.Print($"[vehicle] mode skill {skillId} (mount skill {template.MountSkillRowIds[index]}) on {target}");
        return true;
    }

    // ------------------------------------------------------------------ driving

    /// <summary>
    /// Runs while the player holds a slave's driver seat. Land vehicles (AAEmu IsClientDrivenLandVehicle) are simulated
    /// here and streamed as CSMoveUnit type 2 exactly as the real client does; ships are simulated by the zone and only
    /// receive CSMoveUnit type 5 helm requests. Returns true when the vehicle owns this physics frame.
    /// </summary>
    private bool DriveVehicle(double delta)
    {
        if (_playerAttachedUnitId == 0 || _playerAttachPoint != 1 ||
            !_units.TryGetValue(_playerAttachedUnitId, out var unit) || unit.Node == null ||
            VehicleTemplateFor(unit) is not { } template || !(template.IsClientDrivenLand || template.IsBoat))
        {
            EndDrive();
            return false;
        }
        if (_drive == null || _drive.UnitId != unit.Snapshot.UnitId)
            BeginDrive(unit, template);
        var drive = _drive!;
        drive.SinceSend += delta;
        var forward = Math.Sign(Player.ForwardInput);
        var turn = Math.Sign(Player.TurnInput);
        if (template.IsClientDrivenLand && template.Land != null)
            StepLandVehicle(drive, unit, template.Land, forward, turn, (float)delta);
        else
            StepShipHelm(drive, forward, turn);
        return true;
    }

    private void BeginDrive(RemoteUnit unit, VehicleTemplate template)
    {
        EndDrive();
        _drive = new VehicleDrive
        {
            UnitId = unit.Snapshot.UnitId, Template = template,
            Position = unit.Target, Yaw = unit.TargetYaw,
        };
        if (unit.TargetRotation is { } rotation)
            _drive.Rotation = new NQuaternion(rotation.X, -rotation.Z, rotation.Y, rotation.W);
        unit.LocallyDriven = template.IsClientDrivenLand;
        GD.Print($"[vehicle] driving {template.Name} ({template.KindKey}) unit {unit.Snapshot.UnitId} " +
                 $"{(template.IsBoat ? "helm requests" : "client simulation")}");
    }

    private void EndDrive()
    {
        if (_drive is not { } drive)
            return;
        _drive = null;
        if (_units.TryGetValue(drive.UnitId, out var unit))
        {
            unit.LocallyDriven = false;
            unit.Target = drive.Position;
            unit.TargetYaw = drive.Yaw;
        }
        if (drive.Template.IsBoat && Client?.Stage == ClientStage.InWorld && (drive.ShipThrottle != 0 || drive.ShipSteering != 0))
            Client.SendVehicleMovement(VehiclePacketWriters.MoveShipRequest(drive.UnitId, Client.PhysicsTime, 0, null, null, 0, 0, 0));
    }

    private void StepLandVehicle(VehicleDrive drive, RemoteUnit unit, LandVehiclePhysics physics, int forward, int turn, float dt)
    {
        var target = forward > 0 ? LandVehicleCruiseSpeed : forward < 0 ? -LandVehicleCruiseSpeed * LandVehicleReverseRatio : 0;
        var speedingUp = target != 0 && MathF.Abs(target) > MathF.Abs(drive.Speed) &&
                         (drive.Speed == 0 || MathF.Sign(target) == MathF.Sign(drive.Speed));
        var rate = 1f / MathF.Max(0.05f, speedingUp ? physics.LinearInertia : physics.LinearDecelInertia);
        drive.Speed = MoveToward(drive.Speed, target, rate * dt);

        // Steering follows the A/D input over rot_inertia seconds (captured 0.70 then 1.0 within 0.25 s); the yaw rate is
        // steering x angVel, mirrored while backing up (the real wagon turned right with A in reverse and left with A
        // at a standstill, captured angVel.z -0.40 / +0.40).
        var angular = physics.AngularVelocity > 0 ? physics.AngularVelocity : 0.4f;
        var steeringUp = MathF.Abs(turn) > MathF.Abs(drive.Steering);
        var steerRate = 1f / MathF.Max(0.05f, steeringUp ? physics.RotationInertia : physics.RotationDecelInertia);
        drive.Steering = MoveToward(drive.Steering, turn, steerRate * dt);
        drive.YawRate = drive.Steering * angular * (drive.Speed < -0.05f ? -1f : 1f);
        drive.Yaw += drive.YawRate * dt;

        var heading = new NVector3(-MathF.Sin(drive.Yaw), MathF.Cos(drive.Yaw), 0);
        var next = drive.Position + heading * (drive.Speed * dt);
        if (drive.Speed != 0 && !VehicleCanEnter(drive, next, heading * MathF.Sign(drive.Speed)))
        {
            drive.Speed = 0;
            next = drive.Position;
        }
        next.Z = VehicleGround(next.X, next.Y, drive.Position.Z);
        var climb = dt > 0 ? (next.Z - drive.Position.Z) / dt : 0;
        drive.Velocity = new NVector3(heading.X * drive.Speed, heading.Y * drive.Speed, drive.Speed == 0 ? 0 : climb);
        drive.Position = next;

        var (pitch, roll) = TerrainTilt(drive);
        var settle = 1f - MathF.Exp(-8f * dt);
        drive.Pitch += (pitch - drive.Pitch) * settle;
        drive.Roll += (roll - drive.Roll) * settle;
        drive.Rotation = NQuaternion.CreateFromAxisAngle(NVector3.UnitZ, drive.Yaw) *
                         NQuaternion.CreateFromAxisAngle(NVector3.UnitX, drive.Pitch) *
                         NQuaternion.CreateFromAxisAngle(NVector3.UnitY, drive.Roll);

        unit.Target = drive.Position;
        unit.TargetYaw = drive.Yaw;
        unit.Velocity = drive.Velocity;
        unit.Node.Position = World.ToGodot(drive.Position.X, drive.Position.Y, drive.Position.Z);
        unit.Node.Quaternion = new Quaternion(drive.Rotation.X, drive.Rotation.Z, -drive.Rotation.Y, drive.Rotation.W).Normalized();

        // The real client flags the first packet after the throttle key is released with a trailing 1 (seen twice).
        if (drive.HadThrottle && forward == 0)
            drive.ReleasePending = true;
        drive.HadThrottle = forward != 0;

        if (drive.SinceSend < VehicleMoveInterval)
            return;
        drive.SinceSend = 0;
        var (qx, qy, qz) = WorldCoords.QuaternionToShorts(drive.Rotation);
        static short Velocity(float metresPerSecond) =>
            (short)Math.Clamp(MathF.Round(metresPerSecond * 32767f / 30f), -32767, 32767);
        Client.SendVehicleMovement(VehiclePacketWriters.MoveVehicle(drive.UnitId, Client.PhysicsTime, 0, null, null,
            drive.Position, Velocity(drive.Velocity.X), Velocity(drive.Velocity.Y), Velocity(drive.Velocity.Z),
            qx, qy, qz, 0, 0, drive.YawRate, drive.Steering, 0, [], (sbyte)(drive.ReleasePending ? 1 : 0)));
        drive.ReleasePending = false;
    }

    /// <summary>
    /// CSMoveUnit type 5 while at a ship's helm: throttle ±127 for W/S, steering +127 starboard (D) / -127 port (A)
    /// (AAEmu BoatWaterlineDriveRules: "Type-5 +127 is starboard"). Sent on every change and then at the land-vehicle
    /// cadence; the real client's type 5 cadence and key mapping were not captured (its rowboat helm could not be taken).
    /// </summary>
    private void StepShipHelm(VehicleDrive drive, int forward, int turn)
    {
        var throttle = (sbyte)(forward > 0 ? 127 : forward < 0 ? -127 : 0);
        var steering = (sbyte)(turn > 0 ? -127 : turn < 0 ? 127 : 0);
        var changed = throttle != drive.ShipThrottle || steering != drive.ShipSteering;
        drive.ShipThrottle = throttle;
        drive.ShipSteering = steering;
        UpdateRowingPose(drive, forward, turn);
        if (!changed && drive.SinceSend < VehicleMoveInterval)
            return;
        drive.SinceSend = 0;
        Client.SendVehicleMovement(VehiclePacketWriters.MoveShipRequest(drive.UnitId, Client.PhysicsTime, 0, null, null,
            throttle, steering, 0));
    }

    private void UpdateRowingPose(VehicleDrive drive, int forward, int turn)
    {
        if (drive.Template.KindId != 4)
            return;
        var clip = forward > 0 ? turn > 0 ? "fist_mo_rowing_fl" : turn < 0 ? "fist_mo_rowing_fr" : "fist_mo_rowing_f"
            : forward < 0 ? turn > 0 ? "fist_mo_rowing_bl" : turn < 0 ? "fist_mo_rowing_br" : "fist_mo_rowing_b"
            : "fist_mo_rowing_idle";
        if (clip == drive.PoseClip)
            return;
        drive.PoseClip = clip;
        _playerCharacter?.PlayPresentationAction(clip, loop: true);
    }

    private static float MoveToward(float value, float target, float step) =>
        value < target ? MathF.Min(value + step, target) : MathF.Max(value - step, target);

    /// <summary>Ground under a land vehicle: terrain, raised onto a collider surface just below the current height.</summary>
    private float VehicleGround(float x, float y, float currentZ)
    {
        if (World == null || !World.HasHeightsAt(x, y))
            return currentZ;
        var floor = World.TerrainHeightAt(x, y);
        if (IsInsideTree())
        {
            var from = World.ToGodot(x, y, currentZ + 1.2f);
            var to = World.ToGodot(x, y, currentZ - 6f);
            var hit = GetWorld3D().DirectSpaceState.IntersectRay(PhysicsRayQueryParameters3D.Create(from, to));
            if (hit.Count > 0)
                floor = Math.Max(floor, ((Vector3)hit["position"]).Y);
        }
        return floor;
    }

    private bool VehicleCanEnter(VehicleDrive drive, NVector3 next, NVector3 direction)
    {
        if (World == null || !World.HasHeightsAt(next.X, next.Y))
            return false;
        var floor = World.TerrainHeightAt(next.X, next.Y);
        if (World.TryWaterSurfaceAt(next.X, next.Y, out var water, drive.Position.Z) && water - floor > 1.2f)
            return false; // a land vehicle does not float; the real client's physics would stop it at the shore too
        if (!IsInsideTree())
            return true;
        var reach = MathF.Max(1f, drive.Template.LengthY * 0.5f);
        var from = World.ToGodot(drive.Position.X, drive.Position.Y, drive.Position.Z + 0.9f);
        var ahead = next + direction * reach;
        var to = World.ToGodot(ahead.X, ahead.Y, drive.Position.Z + 0.9f);
        return GetWorld3D().DirectSpaceState.IntersectRay(PhysicsRayQueryParameters3D.Create(from, to)).Count == 0;
    }

    /// <summary>Pitch (nose up positive) and roll (right side down positive) from the terrain under the vehicle's box.</summary>
    private (float Pitch, float Roll) TerrainTilt(VehicleDrive drive)
    {
        if (World == null)
            return (0, 0);
        var halfLength = MathF.Max(1f, drive.Template.LengthY * 0.5f);
        var halfWidth = MathF.Max(0.5f, drive.Template.WidthX * 0.5f);
        var forward = new NVector3(-MathF.Sin(drive.Yaw), MathF.Cos(drive.Yaw), 0);
        var right = new NVector3(MathF.Cos(drive.Yaw), MathF.Sin(drive.Yaw), 0);
        float H(NVector3 p) => World.HasHeightsAt(p.X, p.Y) ? World.TerrainHeightAt(p.X, p.Y) : drive.Position.Z;
        var front = H(drive.Position + forward * halfLength);
        var back = H(drive.Position - forward * halfLength);
        var left = H(drive.Position - right * halfWidth);
        var rightH = H(drive.Position + right * halfWidth);
        return (MathF.Atan2(front - back, 2 * halfLength), MathF.Atan2(left - rightH, 2 * halfWidth));
    }

    // ------------------------------------------------------------------ visuals and seats

    /// <summary>
    /// Resolves a slave's normal-state visual on the static-model worker: CGF hull parts (with their $driver /
    /// $passengerN helper nodes as seats) and skinned AnimObject bodies (the farm wagon's transfers_trailer_a_body.chr,
    /// whose skeleton carries the $driver bone). Unsupported assets keep the placeholder and a diagnostic.
    /// </summary>
    private void QueueSlave(RemoteUnit unit)
    {
        var s = unit.Snapshot;
        var placeholder = new MeshInstance3D
        {
            Mesh = new CapsuleMesh { Radius = 0.3f, Height = 1.7f },
            Position = new Vector3(0, 0.85f, 0),
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.8f, 0.4f, 0.3f) },
        };
        unit.Node.AddChild(placeholder);
        if (s.TemplateId == 0 || !File.Exists(GameDatabasePath))
        {
            GD.PrintErr($"Slave {s.UnitId}: cannot resolve template {s.TemplateId} without the content database.");
            return;
        }

        _doodadWork.Add(() =>
        {
            var visual = SlaveParts(s.TemplateId);
            if (visual.Meshes.Count == 0 && visual.Bodies.Count == 0)
                return;
            var scale = s.Scale > 0 ? s.Scale : 1f;
            Post(() =>
            {
                if (!_units.TryGetValue(s.UnitId, out var current) || current != unit || !GodotObject.IsInstanceValid(unit.Node))
                    return;
                if (visual.Meshes.Count > 0)
                    placeholder.QueueFree();
                var root = new Node3D { Name = "SlaveModel", Scale = Vector3.One * scale };
                unit.Node.AddChild(root);
                foreach (var (mesh, transform) in visual.Meshes)
                    if (Models.GetMesh(mesh) is { } godotMesh)
                        root.AddChild(new MeshInstance3D { Mesh = godotMesh, Transform = transform, Layers = ModelLibrary.ObjectLayer });
                foreach (var (name, transform) in visual.Seats)
                    if (root.GetNodeOrNull(SeatNodePrefix + name) == null)
                        root.AddChild(new Node3D { Name = SeatNodePrefix + name, Transform = transform.Orthonormalized() });
                foreach (var (chrPath, transform) in visual.Bodies)
                {
                    _slaveBodyPaths[s.TemplateId] = chrPath;
                    Characters?.BuildModel(chrPath, body =>
                    {
                        if (!_units.TryGetValue(s.UnitId, out var live) || live != unit || !GodotObject.IsInstanceValid(root))
                        {
                            body.QueueFree();
                            return;
                        }
                        if (GodotObject.IsInstanceValid(placeholder))
                            placeholder.QueueFree();
                        body.Transform = transform.ScaledLocal(body.Scale);
                        body.Name = "SlaveBody";
                        root.AddChild(body);
                        RefreshSeats(s.UnitId);
                    });
                }
                RefreshSeats(s.UnitId);
            });
        });
    }

    /// <summary>Doodad worker thread: normal-state static parts, skinned bodies and seat helpers of a slave template.</summary>
    private SlaveVisual SlaveParts(uint slaveTemplateId)
    {
        if (_slaveParts.TryGetValue(slaveTemplateId, out var cached))
            return cached;
        var visual = new SlaveVisual([], [], []);
        SlaveModelResolution resolution;
        try
        {
            resolution = new SlaveModelResolver(GameDatabasePath).Resolve(slaveTemplateId);
        }
        catch (Exception e)
        {
            GD.PrintErr($"Slave template {slaveTemplateId}: {e.Message}");
            return _slaveParts[slaveTemplateId] = visual;
        }
        foreach (var diagnostic in resolution.Diagnostics)
            GD.PrintErr($"Slave template {slaveTemplateId}: {diagnostic}");
        if (!resolution.IsRenderable)
            return _slaveParts[slaveTemplateId] = visual;

        if (resolution.Kind == SlaveModelKind.Cgf)
        {
            AddSlaveCgf(visual, resolution.PakPath!, null, Transform3D.Identity);
            return _slaveParts[slaveTemplateId] = visual;
        }

        var prefab = SlaveModelResolver.ResolvePrefab(resolution, PakFiles.Read, out var prefabDiagnostic);
        if (prefab is null)
        {
            GD.PrintErr($"Slave template {slaveTemplateId}: {prefabDiagnostic}");
            return _slaveParts[slaveTemplateId] = visual;
        }
        foreach (var part in prefab.Parts)
        {
            var transform = CryAxes.FromRowVector(part.Transform, NVector3.Zero);
            if (part.ModelPath.EndsWith(".cgf", StringComparison.OrdinalIgnoreCase))
                AddSlaveCgf(visual, part.ModelPath, part.MaterialPath, transform);
            else if (part.ModelPath.EndsWith(".chr", StringComparison.OrdinalIgnoreCase))
                visual.Bodies.Add((part.ModelPath, transform));
            else
                GD.PrintErr($"Slave template {slaveTemplateId}: prefab part '{part.ModelPath}' is not a supported model.");
        }
        GD.Print($"Slave template {slaveTemplateId}: {visual.Meshes.Count} static parts, {visual.Bodies.Count} skinned bodies, " +
                 $"seats {string.Join(",", visual.Seats.Select(seat => seat.Name))}");
        return _slaveParts[slaveTemplateId] = visual;
    }

    private void AddSlaveCgf(SlaveVisual visual, string path, string? material, Transform3D transform)
    {
        if (Models.Request(path, material, transform.Basis.Determinant() < 0) is { } mesh)
            visual.Meshes.Add((mesh, transform));
        if (PakFiles.Read(path) is not { } bytes)
            return;
        try
        {
            foreach (var node in CgfModelReader.Read(bytes).Nodes)
                if (node.Name.StartsWith("$driver", StringComparison.OrdinalIgnoreCase) ||
                    node.Name.StartsWith("$passenger", StringComparison.OrdinalIgnoreCase))
                    visual.Seats.Add((node.Name.ToLowerInvariant(),
                        transform * CryAxes.FromRowVector(node.World, NVector3.Zero)));
        }
        catch (Exception e) when (e is InvalidDataException or IndexOutOfRangeException or ArgumentException)
        {
            GD.PrintErr($"Slave hull {path}: helper nodes unreadable ({e.Message})");
        }
    }

    private static string? SeatName(byte point) => point switch
    {
        1 => "$driver",
        >= 2 and <= 8 => $"$passenger{point - 2}",
        _ => null,
    };

    /// <summary>The seat node for an attach point: a CGF helper node, or a node at the skinned body's seat bone rest.</summary>
    private Node3D? SeatAnchor(RemoteUnit parent, byte point)
    {
        if (SeatName(point) is not { } name || parent.Node?.GetNodeOrNull<Node3D>("SlaveModel") is not { } root)
            return null;
        if (root.GetNodeOrNull<Node3D>(SeatNodePrefix + name) is { } seat)
            return seat;
        if (root.GetNodeOrNull("SlaveBody") is not Node body || body.GetNodeOrNull<Skeleton3D>("Skeleton") is not { } skeleton)
            return null;
        var bone = -1;
        for (var i = 0; i < skeleton.GetBoneCount() && bone < 0; i++)
            if (string.Equals(skeleton.GetBoneName(i), name, StringComparison.OrdinalIgnoreCase))
                bone = i;
        if (bone < 0)
            return null;
        // Position only: the bone's own axes are a rig convention, the rider faces the hull's forward (Cry +Y).
        var anchor = new Node3D { Name = SeatNodePrefix + name };
        skeleton.AddChild(anchor);
        anchor.Position = skeleton.GetBoneGlobalRest(bone).Origin;
        var hullBasis = root.GlobalTransform.Basis.Orthonormalized();
        anchor.GlobalBasis = hullBasis;
        return anchor;
    }

    /// <summary>Moves riders already seated on a slave onto its seat nodes once the (asynchronous) model exists.</summary>
    private void RefreshSeats(uint slaveUnitId)
    {
        if (!_units.TryGetValue(slaveUnitId, out var parent))
            return;
        foreach (var (childId, view) in _attachments.Where(pair => pair.Value.ParentUnitId == slaveUnitId).ToArray())
        {
            if (!_seatPoints.TryGetValue(childId, out var point) || SeatAnchor(parent, point) is not { } seat ||
                !GodotObject.IsInstanceValid(view.Child) || view.Child.GetParent() == seat)
                continue;
            view.Child.Reparent(seat, keepGlobalTransform: false);
            view.Child.Position = Vector3.Zero;
            view.Child.Rotation = Vector3.Zero;
        }
    }

    /// <summary>
    /// Seat pose. The content database names no clip for these seats (models.mount_pose_id 0, vehicle/ship
    /// char_anim_steer_* empty), so the clip is chosen by the player CAL's names: fist_ac_trailer_steering /
    /// fist_ac_trailer_sit_idle for the transfers_trailer bodies, fist_mo_rowing_* for boats, fist_ac_steer_sit_idle
    /// for other helms and fist_ac_steercar_idle for other land vehicles.
    /// </summary>
    private void PlaySeatPose(uint childId, RemoteUnit parent, byte point)
    {
        _seatPoints[childId] = point;
        var template = VehicleTemplateFor(parent);
        var trailer = _slaveBodyPaths.TryGetValue(parent.Snapshot.TemplateId, out var body) &&
                      body.Contains("transfers_trailer", StringComparison.OrdinalIgnoreCase);
        var clip = template?.IsBoat == true
            ? template.KindId == 4 && point == 1 ? "fist_mo_rowing_idle" : "fist_ac_steer_sit_idle"
            : trailer ? point == 1 ? "fist_ac_trailer_steering" : "fist_ac_trailer_sit_idle"
            : point == 1 ? "fist_ac_steercar_idle" : "fist_ac_trailer_sit_idle";
        CharacterFor(childId)?.PlayPresentationAction(clip, loop: true);
        if (_drive != null && childId == Entered.UnitId)
            _drive.PoseClip = clip;
    }

    private void StopSeatPose(uint childId)
    {
        if (_seatPoints.Remove(childId))
            CharacterFor(childId)?.StopAction();
    }

    // ------------------------------------------------------------------ doodads bound to slaves

    /// <summary>Doodads bound to a slave (helm, seats, lamps, backpack boxes) carry parent-local positions.</summary>
    private void ParentDoodad(RemoteUnit unit)
    {
        var s = unit.Snapshot;
        if (!_units.TryGetValue(s.ParentUnitId, out var parent) || parent.Node == null || !GodotObject.IsInstanceValid(parent.Node))
        {
            if (!_pendingDoodadParents.TryGetValue(s.ParentUnitId, out var waiting))
                _pendingDoodadParents[s.ParentUnitId] = waiting = [];
            if (!waiting.Contains(s.UnitId))
                waiting.Add(s.UnitId);
            unit.Node.Visible = false;
            unit.Attached = true;
            return;
        }
        unit.Node.Reparent(parent.Node, keepGlobalTransform: false);
        unit.Node.Position = CryAxes.Point(s.Position);
        unit.Node.Rotation = new Vector3(0, s.Yaw, 0);
        unit.Node.Visible = true;
        unit.Attached = true;
    }

    private void AdoptPendingDoodads(uint parentId)
    {
        if (!_pendingDoodadParents.Remove(parentId, out var waiting))
            return;
        foreach (var childId in waiting)
            if (_units.TryGetValue(childId, out var child) && child.Node != null && GodotObject.IsInstanceValid(child.Node))
                ParentDoodad(child);
    }

    /// <summary>Takes a removed slave's bound doodads out of its node before that node is freed (their own removal follows).</summary>
    private void ReleaseParentedDoodads(RemoteUnit gone)
    {
        foreach (var child in _units.Values.Where(u => u.Snapshot.Kind == UnitKind.Doodad &&
                     u.Snapshot.ParentUnitId == gone.Snapshot.UnitId && u.Node != null &&
                     GodotObject.IsInstanceValid(u.Node) && u.Node.GetParent() == gone.Node).ToArray())
        {
            child.Node.Reparent(this, keepGlobalTransform: true);
            child.Node.Visible = false;
        }
    }

    private void ForgetVehicle(uint unitId)
    {
        if (_drive?.UnitId == unitId)
            EndDrive();
        _pendingDoodadParents.Remove(unitId);
    }

    /// <summary>Scenario probe: terrain and water surface the viewer knows at a Cry position.</summary>
    public string WaterProbe(float x, float y)
    {
        if (World == null || !World.HasHeightsAt(x, y))
            return $"({x:F0}, {y:F0}) not loaded";
        var water = World.TryWaterSurfaceAt(x, y, out var surface) ? $"{surface:F2}" : "none";
        return $"({x:F0}, {y:F0}) terrain {World.TerrainHeightAt(x, y):F2} water {water} ocean level {World.OceanLevel:F1}";
    }

    /// <summary>One-line vehicle state for scenario logs.</summary>
    public string VehicleDebug()
    {
        var attached = _playerAttachedUnitId != 0 && _units.TryGetValue(_playerAttachedUnitId, out var unit)
            ? $"{unit.Snapshot.Kind} {unit.Snapshot.UnitId} point {_playerAttachPoint} at ({unit.Target.X:F1}, {unit.Target.Y:F1}, {unit.Target.Z:F1}) yaw {Mathf.RadToDeg(unit.TargetYaw):F0}"
            : "none";
        var drive = _drive is { } d
            ? $"drive speed {d.Speed:F2} m/s yawRate {d.YawRate:F2} rad/s throttle {d.ShipThrottle} steer {d.ShipSteering}"
            : "not driving";
        var slaves = string.Join("; ", _units.Values.Where(u => u.Snapshot.Kind == UnitKind.Slave)
            .Select(u => $"{u.Snapshot.UnitId} t{u.Snapshot.TemplateId} ({u.Target.X:F1}, {u.Target.Y:F1}, {u.Target.Z:F1})"));
        var mine = string.Join("; ", MateSlaveState.MySlavesByObjectId.Values.Select(m =>
            $"{m.ObjectId} ({(m.PositionX >> 32) / 4096f:F1}, {(m.PositionY >> 32) / 4096f:F1}, {m.PositionZ:F1})"));
        return $"attached {attached}; {drive}; slaves [{slaves}]; SCMySlave [{mine}]; mode actions {VehicleModeActions().Count}";
    }
}
