#nullable enable
using System.Numerics;

namespace AAEmu.GodotViewer.Net;

/// <summary>
/// Units and axes of every position, rotation and velocity this library exposes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Position</b>: world metres in CryEngine axes, the same frame the server, object.dat and the
/// heightmap readers use: X grows east, Y grows north, Z is up (terrain height, sea level near 100).
/// A world cell of the viewer is 1024 m, so cell (cx, cy) spans X in [cx*1024, cx*1024+1024) and Y
/// likewise; no offset is applied here. The Godot viewer converts with <c>CryAxes.Point(x, y, z)</c>
/// = Godot (x, z, -y).
/// </para>
/// <para>
/// <b>Yaw</b>: radians of right-handed rotation about Cry +Z. Yaw 0 faces north (+Y), +π/2 faces west
/// (-X). The facing (forward) vector is (-sin yaw, cos yaw, 0), see <see cref="Forward"/>. Because
/// CryAxes maps Cry +Z to Godot +Y with a proper rotation, Godot <c>rotation.y = yaw</c>.
/// On the wire yaw is an sbyte with 127 steps per full turn (verified against the real client's
/// movement captures: heading byte 34 moved along (-0.994, -0.111), byte -25 along (0.945, 0.328)).
/// </para>
/// <para>
/// <b>Velocity</b>: metres per second in the same Cry axes. Unit movement packets carry each
/// component as a short with 32767 = 60 m/s (the real client sends 1092 while swimming up at
/// 2.0 m/s and |v| = 2949 while running at 5.4 m/s).
/// </para>
/// </remarks>
public static class WorldCoords
{
    /// <summary>Wire steps per full turn for sbyte headings.</summary>
    public const float HeadingStepsPerTurn = 127f;

    /// <summary>m/s represented by short.MaxValue in unit movement velocity.</summary>
    public const float VelocityFullScale = 60f;

    public static float HeadingToYaw(sbyte heading) => heading * (MathF.Tau / HeadingStepsPerTurn);

    /// <summary>
    /// The real client does not normalize yaw to ±π (captured headings range over -127..127), so only yaw
    /// beyond a full turn is wrapped; this keeps decode/encode round trips byte-exact.
    /// </summary>
    public static sbyte YawToHeading(float yaw)
    {
        var turns = yaw / MathF.Tau;
        if (turns is > 1f or < -1f)
            turns -= MathF.Truncate(turns);
        return (sbyte)Math.Clamp(MathF.Round(turns * HeadingStepsPerTurn), -127, 127);
    }

    /// <summary>Unit forward vector for a yaw (Cry axes).</summary>
    public static Vector3 Forward(float yaw) => new(-MathF.Sin(yaw), MathF.Cos(yaw), 0f);

    /// <summary>Yaw that faces along a horizontal direction.</summary>
    public static float YawFromDirection(float dx, float dy) => MathF.Atan2(-dx, dy);

    public static Vector3 VelocityFromWire(short x, short y, short z) =>
        new Vector3(x, y, z) * (VelocityFullScale / short.MaxValue);

    public static short VelocityToWire(float metresPerSecond) =>
        (short)Math.Clamp(MathF.Round(metresPerSecond * (short.MaxValue / VelocityFullScale)), short.MinValue, short.MaxValue);

    /// <summary>Yaw (radians about +Z) from a quaternion given as its x, y, z parts scaled by 32767 (w implied).</summary>
    public static float YawFromShortQuaternion(short qx, short qy, short qz)
    {
        float x = qx / 32767f, y = qy / 32767f, z = qz / 32767f;
        var n = x * x + y * y + z * z;
        var w = n < 1f ? MathF.Sqrt(1f - n) : 0f;
        return MathF.Atan2(2f * (w * z + x * y), 1f - 2f * (y * y + z * z));
    }
}

/// <summary>
/// The 10.0.2.13 11-byte quantized world position: |x|*512 and |y|*512 as u32, z as 22 bits over
/// [-100, 4096) (step 4196/2^22 m), then the sign bits of x (0x80) and y (0x40) in the last byte.
/// Mirrors AAEmu.Commons Helpers.ConvertPosition.
/// </summary>
public static class WorldPosition
{
    public static Vector3 Decode(ReadOnlySpan<byte> b)
    {
        var rawX = (uint)(b[0] | (b[1] << 8) | (b[2] << 16) | (b[3] << 24));
        var rawY = (uint)(b[4] | (b[5] << 8) | (b[6] << 16) | (b[7] << 24));
        var rawZ = (uint)(b[8] | (b[9] << 8) | ((b[10] & 0x3F) << 16));
        var x = rawX / 512f * ((b[10] & 0x80) != 0 ? -1f : 1f);
        var y = rawY / 512f * ((b[10] & 0x40) != 0 ? -1f : 1f);
        var z = (float)(rawZ * 4196.0 / 4194304.0 - 100.0);
        return new Vector3(x, y, z);
    }

    public static void Encode(Vector3 p, Span<byte> b)
    {
        var rawX = (uint)((long)(MathF.Abs(p.X) * 4096f) >> 3);
        var rawY = (uint)((long)(MathF.Abs(p.Y) * 4096f) >> 3);
        // Same float-precision arithmetic as the server, so both sides quantize identically.
        var rawZ = (uint)Math.Clamp((long)Math.Floor((p.Z + 100f) / 4196f * 4194304f + 0.5), 0, 0x3FFFFF);
        b[0] = (byte)rawX; b[1] = (byte)(rawX >> 8); b[2] = (byte)(rawX >> 16); b[3] = (byte)(rawX >> 24);
        b[4] = (byte)rawY; b[5] = (byte)(rawY >> 8); b[6] = (byte)(rawY >> 16); b[7] = (byte)(rawY >> 24);
        b[8] = (byte)rawZ; b[9] = (byte)(rawZ >> 8);
        b[10] = (byte)(((rawZ >> 16) & 0x3F) | (p.Y < 0 ? 0x40u : 0u) | (p.X < 0 ? 0x80u : 0u));
    }
}
