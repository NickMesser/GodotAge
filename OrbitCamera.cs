using Godot;
using AAEmu.GodotViewer.Data;

namespace AAEmu.GodotViewer;

/// <summary>Third-person ArcheAge-style orbit camera with mouse steering, zoom smoothing and loaded-world collision.</summary>
public partial class OrbitCamera : Camera3D
{
    private const float CameraCollisionPadding = 0.25f;
    private const float TerrainClearance = 0.2f;

    private float _targetDistance = 7f;
    private float _currentDistance = 7f;
    private float _pitch = -0.3f;
    private bool _initialized;
    private bool _rightDown;
    private bool _leftDown;
    private bool _mouseCaptured;
    private bool _snapToPlayerAfterTeleport;
    private Input.MouseModeEnum _previousMouseMode = Input.MouseModeEnum.Visible;
    private float _yawOffset;
    private Node3D? _cinemaTarget;
    private Node3D? _cinemaViewer;
    private float _cinemaTargetHeight;
    private QuestCameraData? _cinemaCamera;

    public Node3D? Target { get; set; }
    internal WorldStreamer? World { get; set; }

    /// <summary>Desired boom length in metres; the setter clamps it and preserves the existing --orbit option.</summary>
    public float Distance
    {
        get => _targetDistance;
        set
        {
            _targetDistance = Mathf.Clamp(value, MinimumDistance, MaximumDistance);
            if (!_initialized)
                _currentDistance = _targetDistance;
        }
    }

    [Export(PropertyHint.Range, "0,2,0.05,suffix:m")]
    public float MinimumDistance { get; set; } = 0f;

    [Export(PropertyHint.Range, "10,80,1,suffix:m")]
    public float MaximumDistance { get; set; } = 40f;

    /// <summary>Radians; zero looks along -Z (Cry north).</summary>
    public float Yaw { get; set; }

    /// <summary>Negative pitches place the camera above the target. The limits are provisional pending native evidence.</summary>
    public float Pitch
    {
        get => _pitch;
        set => _pitch = Mathf.Clamp(value, MinimumPitch, MaximumPitch);
    }

    [Export(PropertyHint.Range, "-1.5,0.8,0.01")]
    public float MinimumPitch { get; set; } = Mathf.DegToRad(-80f);

    [Export(PropertyHint.Range, "-1.5,0.8,0.01")]
    public float MaximumPitch { get; set; } = Mathf.DegToRad(38f);

    /// <summary>Mouse radians per pixel. The original third-person sensitivity was not present in the cvar dump.</summary>
    [Export(PropertyHint.Range, "0.0005,0.02,0.0005")]
    public float MouseSensitivity { get; set; } = 0.004f;

    public float TargetHeight { get; set; } = 1.6f;
    public float CurrentDistance => _currentDistance;
    public bool IsRightMouseHeld => _rightDown;
    public bool IsLeftMouseHeld => _leftDown;

    public override void _Ready()
    {
        _targetDistance = Mathf.Clamp(_targetDistance, MinimumDistance, MaximumDistance);
        _currentDistance = _targetDistance;
        _initialized = true;
        Fov = 60f;
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (_cinemaTarget != null)
            return;

        if (e is InputEventMouseButton button)
        {
            if (button.ButtonIndex == MouseButton.Right)
            {
                _rightDown = button.Pressed;
                UpdateMouseCapture();
            }
            else if (button.ButtonIndex == MouseButton.Left)
            {
                _leftDown = button.Pressed;
            }

            if (button.Pressed && (button.ButtonIndex == MouseButton.WheelUp || e.IsActionPressed("x2_zoom_in")))
                ZoomIn();
            else if (button.Pressed && (button.ButtonIndex == MouseButton.WheelDown || e.IsActionPressed("x2_zoom_out")))
                ZoomOut();
        }
        else if (e is InputEventMouseMotion motion && (_leftDown || _rightDown) && !(_leftDown && _rightDown))
        {
            Yaw -= motion.Relative.X * MouseSensitivity;
            Pitch -= motion.Relative.Y * MouseSensitivity;
            if (_rightDown)
            {
                _yawOffset = 0f;
                if (Target is PlayerController player)
                    player.CameraYaw = Yaw;
            }
            else
                _yawOffset = AngleDifference(PlayerHeading(), Yaw);
        }
        else if (e.IsActionPressed("x2_front_camera", exactMatch: true))
            SetCameraOffset(Mathf.Pi);
        else if (e.IsActionPressed("x2_back_camera", exactMatch: true))
            SetCameraOffset(0f);
        else if (e.IsActionPressed("x2_left_camera", exactMatch: true))
            SetCameraOffset(-Mathf.Pi * 0.5f);
        else if (e.IsActionPressed("x2_right_camera", exactMatch: true))
            SetCameraOffset(Mathf.Pi * 0.5f);
        else if (e.IsActionPressed("x2_cycle_camera_clockwise", exactMatch: true))
            CycleCamera(Mathf.Pi / 4f);
        else if (e.IsActionPressed("x2_cycle_camera_counter_clockwise", exactMatch: true))
            CycleCamera(-Mathf.Pi / 4f);
    }

    public override void _Process(double delta)
    {
        var right = Input.IsMouseButtonPressed(MouseButton.Right);
        var left = Input.IsMouseButtonPressed(MouseButton.Left);
        _rightDown = right;
        _leftDown = left;
        UpdateMouseCapture();

        if (Target == null)
            return;

        if (_cinemaTarget != null)
        {
            UpdateQuestCinemaCamera();
            return;
        }

        if (_snapToPlayerAfterTeleport)
        {
            _yawOffset = 0f;
            Yaw = PlayerHeading();
            _snapToPlayerAfterTeleport = false;
        }
        else if (!_leftDown && !_rightDown && Target is PlayerController)
            Yaw = PlayerHeading() + _yawOffset;

        if (_rightDown && Target is PlayerController player)
            player.CameraYaw = Yaw;

        var focus = Target.GlobalPosition + Vector3.Up * TargetHeight;
        var rotation = new Basis(Vector3.Up, Yaw) * new Basis(Vector3.Right, Pitch);
        var desired = focus + rotation * new Vector3(0, 0, _targetDistance);
        var safeDistance = ResolveSafeDistance(focus, desired, _targetDistance);

        // Retract at once to avoid clipping through a wall. Smooth the outward extension like a damped boom.
        if (safeDistance < _currentDistance)
            _currentDistance = safeDistance;
        else
            _currentDistance = Mathf.Lerp(_currentDistance, safeDistance, 1f - Mathf.Exp(-(float)delta * 8f));

        if (Target is PlayerController playerModel)
            playerModel.SetThirdPersonModelVisible(_currentDistance > 0.18f);

        var position = focus + rotation * new Vector3(0, 0, _currentDistance);
        GlobalPosition = position;
        LookAt(focus, Vector3.Up);
    }

    /// <summary>Re-centres the camera on the server-supplied heading and clears stale smoothing after teleport.</summary>
    public void ResetForTeleport()
    {
        _snapToPlayerAfterTeleport = true;
        _currentDistance = _targetDistance;
        CancelMouseCapture();
    }

    /// <summary>
    /// Frames a conversation partner from the player's side at upper-body height.  The normal orbit state is
    /// left untouched, so closing the directing window returns to the exact previous view on the next frame.
    /// </summary>
    public void BeginQuestCinema(Node3D target, float targetHeight, Node3D viewer, QuestCameraData? camera)
    {
        CancelMouseCapture();
        _cinemaTarget = target;
        _cinemaViewer = viewer;
        _cinemaTargetHeight = Mathf.Clamp(targetHeight, 1.2f, 4f);
        _cinemaCamera = camera;
        UpdateQuestCinemaCamera();
    }

    public void EndQuestCinema()
    {
        _cinemaTarget = null;
        _cinemaViewer = null;
        _cinemaCamera = null;
        if (Target is PlayerController player)
            player.SetThirdPersonModelVisible(_currentDistance > 0.18f);
    }

    public void CancelMouseCapture()
    {
        if (_mouseCaptured)
        {
            Input.MouseMode = _previousMouseMode;
            _mouseCaptured = false;
        }
        _rightDown = false;
        _leftDown = false;
    }

    public override void _ExitTree() => CancelMouseCapture();

    private void UpdateQuestCinemaCamera()
    {
        if (_cinemaTarget == null || !GodotObject.IsInstanceValid(_cinemaTarget) || !_cinemaTarget.IsInsideTree())
        {
            EndQuestCinema();
            return;
        }

        // quest_cameras offsets are in the NPC's local Cry frame: X right, Y forward (the side it faces, where the camera
        // stands), Z up. A unit node is rotated by its heading only, and Cry +Y maps to Godot -Z, so the NPC's forward is
        // -Basis.Z and its right +Basis.X (normalized: the node may be scaled).
        var basis = _cinemaTarget.GlobalBasis;
        var towardViewer = -basis.Z;
        towardViewer.Y = 0f;
        towardViewer = towardViewer.LengthSquared() < 0.0001f ? Vector3.Back : towardViewer.Normalized();
        var right = basis.X;
        right.Y = 0f;
        right = right.LengthSquared() < 0.0001f ? towardViewer.Cross(Vector3.Up).Normalized() : right.Normalized();
        var eye = _cinemaTarget.GlobalPosition + Vector3.Up * (_cinemaTargetHeight * 0.68f);
        var focus = eye;
        if (_cinemaCamera is { } camera)
        {
            // Both offsets are measured from the NPC (at eye height), not chained: the camera stands at the camera offset
            // and looks at the NPC offset point. Checked against the original's debug overlay for auctioneer 10857
            // (row 19: camera (0, 4.1, 0), npc (-0.5, 0, 0.15); NPC yaw -39.4 deg): camera (14373.7, 15497.4) looking at
            // heading 133.6, overlay CAM=14373 15497 (133) (luna/parity/cinema_cam_check.py). Chaining them moved the
            // camera 0.5 m sideways into the door prop beside the auctioneer.
            focus += right * camera.NpcOffsetX + towardViewer * camera.NpcOffsetY + Vector3.Up * camera.NpcOffsetZ;
            GlobalPosition = eye + right * camera.CameraOffsetX + towardViewer * camera.CameraOffsetY +
                Vector3.Up * camera.CameraOffsetZ;
        }
        else
        {
            var portraitDistance = Mathf.Clamp(_cinemaTargetHeight * 1.35f, 2.2f, 4.2f);
            GlobalPosition = focus + towardViewer * portraitDistance + Vector3.Up * (_cinemaTargetHeight * 0.08f);
        }
        LookAt(focus, Vector3.Up);
    }

    private void ZoomIn() => Distance = _targetDistance / 1.15f;
    private void ZoomOut() => Distance = _targetDistance * 1.15f;

    private void SetCameraOffset(float offset)
    {
        _yawOffset = offset;
        Yaw = PlayerHeading() + offset;
    }

    private void CycleCamera(float amount)
    {
        _yawOffset += amount;
        while (_yawOffset > Mathf.Pi) _yawOffset -= Mathf.Tau;
        while (_yawOffset < -Mathf.Pi) _yawOffset += Mathf.Tau;
        Yaw = PlayerHeading() + _yawOffset;
    }

    private float PlayerHeading() => Target is PlayerController player ? player.Heading : Yaw;

    private static float AngleDifference(float from, float to)
    {
        var difference = to - from;
        while (difference > Mathf.Pi) difference -= Mathf.Tau;
        while (difference < -Mathf.Pi) difference += Mathf.Tau;
        return difference;
    }

    private void UpdateMouseCapture()
    {
        if (_rightDown && !_mouseCaptured)
        {
            _previousMouseMode = Input.MouseMode;
            Input.MouseMode = Input.MouseModeEnum.Captured;
            _mouseCaptured = true;
        }
        else if (!_rightDown && _mouseCaptured)
        {
            Input.MouseMode = _previousMouseMode;
            _mouseCaptured = false;
        }
    }

    private float ResolveSafeDistance(Vector3 focus, Vector3 desired, float distance)
    {
        if (distance <= 0.01f || !IsInsideTree())
            return distance;

        var allowed = distance;
        var query = PhysicsRayQueryParameters3D.Create(focus, desired);
        var hit = GetWorld3D().DirectSpaceState.IntersectRay(query);
        if (hit.Count > 0 && hit.TryGetValue("position", out var hitValue))
            allowed = Mathf.Min(allowed, focus.DistanceTo((Vector3)hitValue) - CameraCollisionPadding);

        if (World != null)
        {
            const int samples = 20;
            for (var i = 1; i <= samples; i++)
            {
                var fraction = i / (float)samples;
                var point = focus.Lerp(desired, fraction);
                var cry = World.ToCry(point);
                if (!World.HasHeightsAt(cry.X, cry.Y))
                    continue;
                if (point.Y < World.TerrainHeightAt(cry.X, cry.Y) + TerrainClearance)
                {
                    allowed = Mathf.Min(allowed, distance * fraction - CameraCollisionPadding);
                    break;
                }
            }
        }

        return Mathf.Clamp(allowed, 0f, distance);
    }
}
