using Godot;
using NVector3 = System.Numerics.Vector3;

namespace AAEmu.GodotViewer;

/// <summary>
/// A built character's face mesh kept with its unmorphed bind positions and morph targets, so another appearance modifier
/// (the customization screen's face sliders and presets) is shown by re-uploading the face surfaces on the main thread
/// instead of rebuilding the whole character. The weights come from <see cref="CharacterAssetResolver.FaceMorphWeights"/>,
/// the same rule the full build uses, so an in-place update and a rebuild give the same mesh.
/// </summary>
public sealed class FaceMorphState
{
    private readonly ChrModel _model;
    private readonly byte[] _targetsXml;
    private readonly ChrSubmesh[] _drawn;
    private readonly NVector3[][] _base;
    private readonly Godot.Collections.Array[] _surfaces;
    private readonly Dictionary<string, (int Surface, int Vertex, NVector3 Delta)[]> _entries = new(StringComparer.OrdinalIgnoreCase);
    private byte[] _shown;
    private ArrayMesh _mesh;

    /// <param name="model">The face .chr (its morph targets are read, its positions are not used).</param>
    /// <param name="drawn">The submeshes that became surfaces, in surface order.</param>
    /// <param name="basePositions">Their bind positions before any face morph.</param>
    /// <param name="surfaces">The surface arrays the mesh was built from (their vertex array is replaced on update).</param>
    /// <param name="shownModifier">The modifier the built mesh shows.</param>
    internal FaceMorphState(ChrModel model, byte[] targetsXml, ChrSubmesh[] drawn, NVector3[][] basePositions,
        Godot.Collections.Array[] surfaces, byte[] shownModifier)
    {
        _model = model;
        _targetsXml = targetsXml;
        _drawn = drawn;
        _base = basePositions;
        _surfaces = surfaces;
        _shown = shownModifier ?? [];
    }

    /// <summary>Main thread, once the face MeshInstance3D exists.</summary>
    internal void Attach(ArrayMesh mesh) => _mesh = mesh;

    /// <summary>Main thread: shows <paramref name="modifier"/> on the face; false when it already did (nothing to do).</summary>
    public bool Show(byte[] modifier)
    {
        modifier ??= [];
        if (_mesh == null || !GodotObject.IsInstanceValid(_mesh) || modifier.AsSpan().SequenceEqual(_shown))
            return false;
        var positions = new NVector3[_base.Length][];
        for (var i = 0; i < _base.Length; i++)
            positions[i] = (NVector3[])_base[i].Clone();
        foreach (var (name, weight) in CharacterAssetResolver.FaceMorphWeights(_targetsXml, modifier))
            foreach (var (surface, vertex, delta) in Entries(name))
                positions[surface][vertex] += delta * weight;

        var count = _mesh.GetSurfaceCount();
        var materials = new Material[count];
        for (var i = 0; i < count; i++)
            materials[i] = _mesh.SurfaceGetMaterial(i);
        _mesh.ClearSurfaces();
        for (var i = 0; i < _surfaces.Length; i++)
        {
            _surfaces[i][(int)Mesh.ArrayType.Vertex] = positions[i].Select(CryAxes.Point).ToArray();
            _mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, _surfaces[i]);
            if (i < materials.Length)
                _mesh.SurfaceSetMaterial(i, materials[i]);
        }
        _shown = (byte[])modifier.Clone();
        return true;
    }

    /// <summary>The (surface, vertex, delta) triples of a morph target over the drawn submeshes, cached per name.</summary>
    private (int Surface, int Vertex, NVector3 Delta)[] Entries(string name)
    {
        if (_entries.TryGetValue(name, out var cached))
            return cached;
        var target = _model.FindMorphTarget(name);
        var list = new List<(int, int, NVector3)>();
        if (target != null)
        {
            var delta = new Dictionary<int, NVector3>(target.Vertices.Length);
            for (var i = 0; i < target.Vertices.Length; i++)
                delta[target.Vertices[i]] = target.Deltas[i];
            for (var s = 0; s < _drawn.Length; s++)
                for (var v = 0; v < _drawn[s].SourceVertices.Length; v++)
                    if (delta.TryGetValue(_drawn[s].SourceVertices[v], out var d))
                        list.Add((s, v, d));
        }
        return _entries[name] = list.ToArray();
    }
}

public partial class CharacterNode
{
    /// <summary>The face's in-place morph state (playable-race faces only), null for other characters.</summary>
    public FaceMorphState FaceMorph { get; internal set; }
}
