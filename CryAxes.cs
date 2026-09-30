using Godot;

namespace AAEmu.GodotViewer;

/// <summary>
/// CryEngine is Z-up with X east and Y north; Godot is Y-up. A Cry point (x, y, z) is shown at (x, z, -y).
/// That mapping is a proper rotation (determinant +1), so triangle winding and handedness are preserved.
/// </summary>
internal static class CryAxes
{
    public static Vector3 Point(float x, float y, float z) => new(x, z, -y);

    public static Vector3 Point(System.Numerics.Vector3 v) => new(v.X, v.Z, -v.Y);

    /// <summary>
    /// Converts a CryEngine affine transform (row-major 3x4 or the upper rows of a 4x4 whose translation is in
    /// the last column) into a Godot transform acting on Godot-axis model vertices, shifted by <paramref name="originOffset"/>
    /// (Cry metres, added to the translation before conversion).
    /// </summary>
    public static Transform3D Transform(System.Numerics.Matrix4x4 m, System.Numerics.Vector3 originOffset)
    {
        // Cry basis columns are the images of the Cry X, Y, Z axes: (M11, M21, M31), (M12, M22, M32), (M13, M23, M33).
        // In Godot axes the model's X, Y(up), Z(south) axes are the images of Cry X, Z and -Y.
        var cx = Point(m.M11, m.M21, m.M31);
        var cy = Point(m.M12, m.M22, m.M32);
        var cz = Point(m.M13, m.M23, m.M33);
        var basis = new Basis(cx, cz, -cy);
        var origin = Point(m.M14 + originOffset.X, m.M24 + originOffset.Y, m.M34 + originOffset.Z);
        return new Transform3D(basis, origin);
    }

    /// <summary>
    /// Same as <see cref="Transform"/> for a matrix in System.Numerics' own row-vector convention
    /// (world = Vector3.Transform(local, m), translation in M41..M43), as the cell readers return.
    /// </summary>
    public static Transform3D FromRowVector(System.Numerics.Matrix4x4 m, System.Numerics.Vector3 originOffset) =>
        Transform(System.Numerics.Matrix4x4.Transpose(m), originOffset);
}
