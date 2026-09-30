using AAEmu.GodotViewer.Net;

namespace AAEmu.GodotViewer;

/// <summary>Presentation state shared by local prediction and remote movement animation.</summary>
public enum MovementPose
{
    Idle,
    Walk,
    Run,
    JumpRise,
    Fall,
    Land,
    SwimIdle,
    SwimMove,
    SwimDive,
    Glide,
    Mounted,
    MountedRun,
}

public enum MovementTransition
{
    None,
    JumpStarted,
    Landed,
}

public static class MovementPresentation
{
    private const ushort OnGroundActorFlag = 0x0004;

    /// <summary>Derives a visual pose only from fields decoded for Unit movement.</summary>
    public static MovementPose FromUnitMovement(UnitMovement movement, bool wasGrounded)
    {
        if (movement.Kind != MoveKind.Unit)
            return HorizontalSpeed(movement) > 0.2f ? MovementPose.Run : MovementPose.Idle;
        if (movement.Stance == 2)
            return movement.DeltaZ < 0 || movement.Velocity.Z < -0.2f
                ? MovementPose.SwimDive
                : HorizontalSpeed(movement) > 0.2f ? MovementPose.SwimMove : MovementPose.SwimIdle;

        var grounded = IsGrounded(movement);
        if (grounded && !wasGrounded)
            return MovementPose.Land;
        if (!grounded)
            return movement.Velocity.Z > 0.15f ? MovementPose.JumpRise : MovementPose.Fall;
        var speed = HorizontalSpeed(movement);
        return speed > 3.5f ? MovementPose.Run : speed > 0.2f ? MovementPose.Walk : MovementPose.Idle;
    }

    public static bool IsGrounded(UnitMovement movement) =>
        movement.Kind == MoveKind.Unit && movement.Flags != (byte)MoveFlags.Jumping &&
        (movement.ActorFlags & OnGroundActorFlag) != 0;

    public static float HorizontalSpeed(UnitMovement movement) =>
        new System.Numerics.Vector2(movement.Velocity.X, movement.Velocity.Y).Length();
}
