using Godot;
using NVector3 = System.Numerics.Vector3;

namespace AAEmu.GodotViewer;

/// <summary>
/// The local player: walks on the loaded terrain with WASD relative to the camera, jumps with Space, swims at the
/// ocean surface. Position and heading are kept in Cry world coordinates (what the server speaks); the node's Godot
/// transform follows them every frame.
/// </summary>
public partial class PlayerController : Node3D
{
    /// <summary>Run speed in metres per second.</summary>
    public float RunSpeed { get; set; } = 5.4f;
    public float WalkSpeed { get; set; } = 2.0f;
    public float JumpSpeed { get; set; } = 5.5f;
    public float Gravity { get; set; } = 18f;
    public float SwimSpeed { get; set; } = 3.4f;
    public float SwimVerticalSpeed { get; set; } = 2.4f;
    public float TurnSpeed { get; set; } = 2.2f;
    public float GlideSpeed { get; set; } = 8f;
    public float GlideFallSpeed { get; set; } = 1.4f;

    /// <summary>Cry world position (metres, Z up).</summary>
    public NVector3 CryPosition { get; set; }
    /// <summary>Heading in radians, Cry convention: 0 faces north (+Y), positive turns towards west (counter-clockwise from above).</summary>
    public float Heading { get; set; }
    /// <summary>Current velocity in Cry axes (metres per second).</summary>
    public NVector3 CryVelocity { get; private set; }
    public NVector3 NetworkPosition => CryPosition - _localOnlyOffset;
    public NVector3 NetworkVelocity { get; private set; }
    public bool IsMoving => CryVelocity.X != 0 || CryVelocity.Y != 0;
    public bool OnGround { get; private set; } = true;
    public bool IsSwimming { get; private set; }
    public bool IsGliding { get; private set; }
    public bool Walking { get; private set; }
    public bool AutoRunning { get; private set; }
    public bool IsMounted { get; private set; }
    public MovementPose MovementPose { get; private set; }
    public MovementTransition LastTransition { get; private set; }
    public int TransitionId { get; private set; }
    public NVector3 TransitionPosition { get; private set; }
    public sbyte ForwardInput { get; private set; }
    public sbyte StrafeInput { get; private set; }
    public sbyte VerticalInput { get; private set; }
    public bool HasMovementInput { get; private set; }
    public float FloorHeight { get; private set; }
    public float? WaterHeight { get; private set; }
    public string MovementState => IsGliding ? "glide" : IsSwimming ? "swim" :
        MovementPose == MovementPose.Land ? "land" : OnGround ? "grounded" :
        MovementPose == MovementPose.JumpRise ? "jump" : "fall";
    /// <summary>Seconds of simulated forward running left (for scripted tests); counts down while positive.</summary>
    public double AutoRunSeconds { get; set; }
    /// <summary>False while the loading cover is up; prevents local movement input and motion packets.</summary>
    public bool InputEnabled { get; set; } = true;

    internal WorldStreamer World;
    /// <summary>Camera yaw in radians (Godot: 0 looks along -Z = Cry north), used to make WASD camera-relative.</summary>
    public float CameraYaw { get; set; }
    private float _verticalSpeed;
    private Node3D _model;
    private float _landPoseSeconds;
    private bool _jumpHeld;
    private float _mountedSpeed;
    private NVector3 _localOnlyOffset;

    public override void _Ready()
    {
        // Placeholder body until character models are wired in: a capsule plus a nose showing the facing.
        _model = new Node3D { Name = "Body" };
        AddChild(_model);
        _model.AddChild(new MeshInstance3D
        {
            Mesh = new CapsuleMesh { Radius = 0.35f, Height = 1.8f },
            Position = new Vector3(0, 0.9f, 0),
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.85f, 0.7f, 0.3f) },
        });
        _model.AddChild(new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Vector3(0.15f, 0.15f, 0.4f) },
            Position = new Vector3(0, 1.5f, -0.4f),
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.2f, 0.2f, 0.2f) },
        });
    }

    /// <summary>Replaces the placeholder with a real model node (origin at the feet, facing -Z).</summary>
    public void SetModel(Node3D model)
    {
        _model?.QueueFree();
        _model = model;
        AddChild(model);
    }

    internal void SetThirdPersonModelVisible(bool visible)
    {
        if (_model != null)
            _model.Visible = visible;
    }

    public override void _UnhandledInput(InputEvent inputEvent)
    {
        if (!InputEnabled)
            return;
        if (inputEvent is not InputEventKey { Pressed: true, Echo: false } key)
            return;
        if (ActionOrKey(key, "x2_toggle_walk", Key.Period))
            Walking = !Walking;
        else if (ActionOrKey(key, "x2_autorun", Key.Numlock))
            AutoRunning = !AutoRunning;
    }

    public void Teleport(NVector3 cryPosition, float heading)
    {
        CryPosition = cryPosition;
        Heading = heading;
        _verticalSpeed = 0;
        _landPoseSeconds = 0;
        _localOnlyOffset = NVector3.Zero;
        NetworkVelocity = NVector3.Zero;
        IsSwimming = false;
        IsGliding = false;
        OnGround = true;
        FloorHeight = World != null && World.HasHeightsAt(cryPosition.X, cryPosition.Y)
            ? World.TerrainHeightAt(cryPosition.X, cryPosition.Y) : cryPosition.Z;
        WaterHeight = World != null && World.TryWaterSurfaceAt(cryPosition.X, cryPosition.Y, out var surface, cryPosition.Z)
            ? surface : null;
        // An offline seabed spawn (or an online floor-level transfer into deep water) begins afloat.
        // Preserve explicit underwater/server positions above the floor so diving is not cancelled.
        if (WaterHeight is float water && water - FloorHeight > 1.8f &&
            cryPosition.Z <= FloorHeight + 0.5f)
        {
            CryPosition = new NVector3(cryPosition.X, cryPosition.Y, water - 1.35f);
            IsSwimming = true;
            OnGround = false;
        }
        MovementPose = IsSwimming ? MovementPose.SwimIdle : MovementPose.Idle;
        GD.Print($"Movement state: {MovementState}, pose={MovementPose}, floor={FloorHeight:F2}, player={CryPosition.Z:F2}, water={WaterHeight?.ToString("F2") ?? "none"}");
        SyncTransform();
    }

    public override void _PhysicsProcess(double delta)
    {
        if (!InputEnabled)
        {
            CryVelocity = NVector3.Zero;
            NetworkVelocity = NVector3.Zero;
            ForwardInput = 0;
            StrafeInput = 0;
            VerticalInput = 0;
            HasMovementInput = false;
            return;
        }

        if (IsMounted)
        {
            var bothMouseButtons = Input.IsMouseButtonPressed(MouseButton.Left) && Input.IsMouseButtonPressed(MouseButton.Right);
            var rightSteering = Input.IsMouseButtonPressed(MouseButton.Right) && !Input.IsMouseButtonPressed(MouseButton.Left);
            var mountedForward = bothMouseButtons ? 1f : Axis("x2_moveback", Key.S, "x2_moveforward", Key.W);
            var mountedStrafe = bothMouseButtons ? 0f : Axis("x2_moveleft", Key.Q, "x2_moveright", Key.E);
            var mountedTurn = rightSteering || bothMouseButtons ? 0f : Axis("x2_turnright", Key.D, "x2_turnleft", Key.A);
            if (rightSteering || bothMouseButtons)
                Heading = CameraYaw;
            else
                Heading += mountedTurn * TurnSpeed * (float)delta;
            var mountedInput = new Vector2(mountedStrafe, mountedForward);
            if (mountedInput.LengthSquared() > 1) mountedInput = mountedInput.Normalized();
            CryVelocity = NVector3.Zero;
            ForwardInput = (sbyte)Mathf.RoundToInt(Mathf.Clamp(mountedForward, -1, 1) * 127);
            StrafeInput = (sbyte)Mathf.RoundToInt(mountedInput.X * 127);
            VerticalInput = 0;
            HasMovementInput = mountedInput != Vector2.Zero;
            if (World != null)
                CryPosition = World.ToCry(GlobalPosition);
            MovementPose = _mountedSpeed > 0.2f ? MovementPose.MountedRun : MovementPose.Mounted;
            if (_model is CharacterNode rider)
            {
                rider.Speed = _mountedSpeed;
                rider.SetMovementPose(MovementPose);
            }
            return;
        }
        var dt = (float)delta;
        LastTransition = MovementTransition.None;
        var forwardInput = Axis("x2_moveback", Key.S, "x2_moveforward", Key.W);
        var strafeInput = Axis("x2_moveleft", Key.Q, "x2_moveright", Key.E);
        var turnInput = Axis("x2_turnright", Key.D, "x2_turnleft", Key.A);
        var bothMouseButtonsDown = Input.IsMouseButtonPressed(MouseButton.Left) && Input.IsMouseButtonPressed(MouseButton.Right);
        var rightSteeringDown = Input.IsMouseButtonPressed(MouseButton.Right) && !Input.IsMouseButtonPressed(MouseButton.Left);
        if (bothMouseButtonsDown)
        {
            forwardInput = 1;
            strafeInput = 0;
            turnInput = 0;
        }
        else if (rightSteeringDown)
            turnInput = 0;
        var jumpHeld = Pressed("x2_jump", Key.Space);
        var jumpPressed = jumpHeld && !_jumpHeld;
        _jumpHeld = jumpHeld;
        if (AutoRunSeconds > 0)
        {
            AutoRunSeconds -= dt;
            forwardInput = 1;
        }
        if (forwardInput < 0) AutoRunning = false;
        if (AutoRunning) forwardInput = Math.Max(forwardInput, 1);
        if (rightSteeringDown || bothMouseButtonsDown)
            Heading = CameraYaw;
        else
            Heading += turnInput * TurnSpeed * dt;

        var input = new Vector2(strafeInput, forwardInput);
        if (input.LengthSquared() > 1) input = input.Normalized();
        HasMovementInput = input != Vector2.Zero;
        ForwardInput = (sbyte)Mathf.RoundToInt(Mathf.Clamp(forwardInput, -1, 1) * 127);
        StrafeInput = (sbyte)Mathf.RoundToInt(input.X * 127);
        VerticalInput = 0;

        var velocity = NVector3.Zero;
        var localOnlyVelocity = NVector3.Zero;
        if (input != Vector2.Zero)
        {
            var forward = new Vector2(-Mathf.Sin(Heading), Mathf.Cos(Heading));
            var right = new Vector2(Mathf.Cos(Heading), Mathf.Sin(Heading));
            var move = forward * input.Y + right * input.X;
            var speed = IsSwimming ? SwimSpeed : IsGliding ? GlideSpeed : Walking && !bothMouseButtonsDown ? WalkSpeed : RunSpeed;
            velocity = new NVector3(move.X * speed, move.Y * speed, 0);
            var supported = forward * Mathf.Clamp(forwardInput, -1, 1) * speed;
            localOnlyVelocity = velocity - new NVector3(supported.X, supported.Y, 0);
        }
        // An active glider always advances along its heading, including with no held movement key.
        if (IsGliding)
        {
            velocity = new NVector3(-Mathf.Sin(Heading) * GlideSpeed, Mathf.Cos(Heading) * GlideSpeed, 0);
            localOnlyVelocity = NVector3.Zero;
        }

        var next = CryPosition + velocity * dt;
        var previousPose = MovementPose;
        var floor = SolidFloorAt(next);
        FloorHeight = floor;
        var water = 0f;
        var hasWater = World != null && World.TryWaterSurfaceAt(next.X, next.Y, out water, next.Z);
        WaterHeight = hasWater ? water : null;
        if (IsGliding && hasWater && next.Z <= water)
        {
            IsGliding = false;
            IsSwimming = water - floor > 0.8f;
        }
        if (!IsGliding && hasWater && water - floor > 1.8f && (IsSwimming || next.Z < water - 0.2f))
        {
            if (!IsSwimming && OnGround && next.Z <= floor + 0.5f)
                next.Z = water - 1.35f;
            IsSwimming = true;
        }
        if (IsSwimming && (!hasWater || floor >= water - 0.8f))
        {
            IsSwimming = false;
            next.Z = floor;
            _localOnlyOffset.Z = 0;
            OnGround = true;
        }

        if (IsSwimming)
        {
            OnGround = false;
            IsGliding = false;
            var vertical = Axis("x2_down", Key.X, "x2_jump", Key.Space);
            VerticalInput = (sbyte)Mathf.RoundToInt(vertical * 127);
            _verticalSpeed = vertical * SwimVerticalSpeed;
            var swimMagnitude = MathF.Sqrt(input.LengthSquared() + vertical * vertical);
            if (swimMagnitude > 1f)
            {
                velocity.X /= swimMagnitude;
                velocity.Y /= swimMagnitude;
                localOnlyVelocity.X /= swimMagnitude;
                localOnlyVelocity.Y /= swimMagnitude;
                _verticalSpeed /= swimMagnitude;
            }
            if (Math.Abs(vertical) < 0.01f)
                _verticalSpeed = Mathf.Clamp((water - 1.35f - next.Z) * 2f, -1.2f, 1.2f);
            var beforeVertical = next.Z;
            next.Z = Mathf.Clamp(next.Z + _verticalSpeed * dt, floor + 0.2f, water - 0.15f);
            if (vertical < 0 && dt > 0)
                localOnlyVelocity.Z = (next.Z - beforeVertical) / dt;
        }
        else if (OnGround && jumpPressed)
        {
            _verticalSpeed = JumpSpeed;
            OnGround = false;
            SetTransition(MovementTransition.JumpStarted, NetworkPosition);
        }
        else if (OnGround && floor < next.Z - 0.35f)
            OnGround = false; // walked off an edge
        if (!OnGround && !IsSwimming)
        {
            if (IsGliding)
            {
                _verticalSpeed = -GlideFallSpeed;
            }
            else
                _verticalSpeed -= Gravity * dt;
            next.Z += _verticalSpeed * dt;
            if (next.Z <= floor)
            {
                next.Z = floor;
                OnGround = true;
                IsGliding = false;
                _verticalSpeed = 0;
                _landPoseSeconds = 0.24f;
                SetTransition(MovementTransition.Landed, next - (_localOnlyOffset + localOnlyVelocity * dt));
            }
        }
        else if (!IsSwimming)
            next.Z = floor;

        CryVelocity = new NVector3(velocity.X, velocity.Y, OnGround ? 0 : _verticalSpeed);
        var networkVelocity = CryVelocity - localOnlyVelocity;
        if (IsSwimming && VerticalInput < 0)
            networkVelocity.Z = 0;
        NetworkVelocity = networkVelocity;
        _localOnlyOffset += localOnlyVelocity * dt;
        _landPoseSeconds = Math.Max(0, _landPoseSeconds - dt);
        MovementPose = IsGliding ? MovementPose.Glide
            : IsSwimming ? (VerticalInput < 0 ? MovementPose.SwimDive : input == Vector2.Zero ? MovementPose.SwimIdle : MovementPose.SwimMove)
            : _landPoseSeconds > 0 ? MovementPose.Land
            : !OnGround ? (_verticalSpeed > 0.15f ? MovementPose.JumpRise : MovementPose.Fall)
            : input == Vector2.Zero ? MovementPose.Idle : Walking ? MovementPose.Walk : MovementPose.Run;
        if (_model is CharacterNode character)
        {
            character.Speed = new Vector2(velocity.X, velocity.Y).Length();
            character.MovementPose = MovementPose;
        }
        CryPosition = next;
        if (MovementPose != previousPose)
            GD.Print($"Movement state: {MovementState}, pose={MovementPose}, floor={FloorHeight:F2}, player={CryPosition.Z:F2}, water={WaterHeight?.ToString("F2") ?? "none"}");
        SyncTransform();
    }

    /// <summary>
    /// Highest walkable surface under a position: terrain or a solid model found by a ray cast
    /// down from a little above the feet (so the player steps up 0.7 m but doesn't jump onto roofs overhead).
    /// </summary>
    private float SolidFloorAt(NVector3 p)
    {
        var floor = World != null && World.HasHeightsAt(p.X, p.Y) ? World.TerrainHeightAt(p.X, p.Y) : p.Z;
        if (World == null || !IsInsideTree())
            return floor;
        var from = World.ToGodot(p.X, p.Y, p.Z + 0.7f);
        var to = World.ToGodot(p.X, p.Y, p.Z - 40f);
        var hit = GetWorld3D().DirectSpaceState.IntersectRay(PhysicsRayQueryParameters3D.Create(from, to));
        if (hit.Count > 0)
            floor = Math.Max(floor, ((Vector3)hit["position"]).Y);
        return floor;
    }

    private void SetTransition(MovementTransition transition, NVector3 position)
    {
        LastTransition = transition;
        TransitionPosition = position;
        TransitionId++;
    }

    public void SetMounted(bool mounted)
    {
        IsMounted = mounted;
        _localOnlyOffset = NVector3.Zero;
        NetworkVelocity = NVector3.Zero;
        _mountedSpeed = 0;
        AutoRunning = false;
        HasMovementInput = false;
        if (!mounted && World != null)
            CryPosition = World.ToCry(GlobalPosition);
        if (_model is CharacterNode character)
            character.SetMovementPose(mounted ? MovementPose.Mounted : MovementPose.Idle);
    }

    public void SetMountedMotion(float speed)
    {
        _mountedSpeed = speed;
        if (_model is not CharacterNode character)
            return;
        character.Speed = speed;
        character.SetMovementPose(speed > 0.2f ? MovementPose.MountedRun : MovementPose.Mounted);
    }

    public void SetServerGliding(bool gliding)
    {
        if (OnGround || IsSwimming) gliding = false;
        IsGliding = gliding;
    }

    private static float Axis(string negativeAction, Key negativeKey, string positiveAction, Key positiveKey) =>
        (Pressed(positiveAction, positiveKey) ? 1 : 0) - (Pressed(negativeAction, negativeKey) ? 1 : 0);

    private static bool Pressed(string action, Key fallback) =>
        InputMap.HasAction(action) ? Input.IsActionPressed(action) : Input.IsKeyPressed(fallback);

    private static bool ActionOrKey(InputEventKey key, string action, Key fallback) =>
        InputMap.HasAction(action) ? key.IsActionPressed(action) : key.Keycode == fallback;


    private void SyncTransform()
    {
        if (World == null)
            return;
        Position = World.ToGodot(CryPosition.X, CryPosition.Y, CryPosition.Z);
        // Cry heading 0 = north = Godot -Z, which is Godot yaw 0; both turn counter-clockwise seen from above.
        Rotation = new Vector3(0, Heading, 0);
    }
}
