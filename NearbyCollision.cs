using Godot;

namespace AAEmu.GodotViewer;

/// <summary>
/// Main thread. Keeps static physics bodies for the solid placements around the player, so the player can stand on
/// floors, bridges and rocks. Bodies are built from the models' render triangles with the instance transform baked in
/// (so mirrored and non-uniformly scaled instances need no special handling), a few per update.
/// </summary>
internal sealed class NearbyCollision(Node3D root, WorldStreamer world, ModelLibrary models)
{
    private const float LoadRange = 45f;
    private const float DropRange = 70f;
    private const int CreatePerUpdate = 16;

    private readonly Dictionary<Collidable, StaticBody3D> _active = new(ReferenceEqualityComparer.Instance);
    private double _nextUpdate;

    public int ActiveBodies => _active.Count;

    public void Update(Vector3 position, double now)
    {
        if (now < _nextUpdate)
            return;
        _nextUpdate = now + 0.2;

        foreach (var (c, body) in _active.ToArray())
            if (c.Centre.DistanceTo(position) - c.Radius > DropRange)
            {
                body.QueueFree();
                _active.Remove(c);
            }

        var created = 0;
        foreach (var c in world.CollidablesNear(position, LoadRange).OrderBy(c => c.Centre.DistanceTo(position) - c.Radius))
        {
            if (_active.ContainsKey(c))
                continue;
            if (created++ >= CreatePerUpdate)
                break;
            var body = Create(c);
            if (body == null)
                continue;
            root.AddChild(body);
            _active[c] = body;
        }
    }

    private StaticBody3D Create(Collidable c)
    {
        var faces = c.Faces;
        if (faces == null)
        {
            var local = models.CollisionFaces(c.CollisionKey);
            if (local == null || local.Length < 3)
                return null;
            faces = new Vector3[local.Length];
            for (var i = 0; i < local.Length; i++)
                faces[i] = c.Transform * local[i];
        }
        var body = new StaticBody3D { Name = "Collision" };
        body.AddChild(new CollisionShape3D { Shape = new ConcavePolygonShape3D { Data = faces, BackfaceCollision = true } });
        return body;
    }
}
