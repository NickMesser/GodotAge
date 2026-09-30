using Godot;

namespace AAEmu.GodotViewer;

/// <summary>Free-fly camera: hold the right mouse button to look, WASD to move, Q/E down and up, Shift for 5x speed.</summary>
public partial class FlyCamera : Camera3D
{
    public float Speed { get; set; } = 80f;

    private float _yaw;
    private float _pitch;
    private bool _looking;

    /// <summary>Yaw 0 looks along -Z (Cry north); positive yaw turns left.</summary>
    public void LookFrom(Vector3 position, float yawDegrees, float pitchDegrees)
    {
        Position = position;
        _yaw = Mathf.DegToRad(yawDegrees);
        _pitch = Mathf.DegToRad(pitchDegrees);
        ApplyRotation();
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (e is InputEventMouseButton button)
        {
            if (button.ButtonIndex == MouseButton.Right)
            {
                _looking = button.Pressed;
                Input.MouseMode = _looking ? Input.MouseModeEnum.Captured : Input.MouseModeEnum.Visible;
            }
            else if (button.Pressed && button.ButtonIndex == MouseButton.WheelUp)
                Speed *= 1.25f;
            else if (button.Pressed && button.ButtonIndex == MouseButton.WheelDown)
                Speed /= 1.25f;
        }
        else if (e is InputEventMouseMotion motion && _looking)
        {
            _yaw -= motion.Relative.X * 0.003f;
            _pitch = Mathf.Clamp(_pitch - motion.Relative.Y * 0.003f, -1.55f, 1.55f);
            ApplyRotation();
        }
    }

    public override void _Process(double delta)
    {
        var direction = Vector3.Zero;
        if (Input.IsKeyPressed(Key.W)) direction -= Basis.Z;
        if (Input.IsKeyPressed(Key.S)) direction += Basis.Z;
        if (Input.IsKeyPressed(Key.A)) direction -= Basis.X;
        if (Input.IsKeyPressed(Key.D)) direction += Basis.X;
        if (Input.IsKeyPressed(Key.E)) direction += Vector3.Up;
        if (Input.IsKeyPressed(Key.Q)) direction -= Vector3.Up;
        if (direction == Vector3.Zero)
            return;

        var boost = Input.IsKeyPressed(Key.Shift) ? 5f : 1f;
        Position += direction.Normalized() * Speed * boost * (float)delta;
    }

    private void ApplyRotation() => Basis = new Basis(Vector3.Up, _yaw) * new Basis(Vector3.Right, _pitch);
}
