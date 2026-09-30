using Godot;

namespace AAEmu.GodotViewer;

/// <summary>
/// Temporary runner avatar: wraps the static "Space Bunny Alpha" GLB in the <see cref="CharacterNode"/>
/// contract (origin at the feet, facing -Z, canonical "idle"/"walk"/"run"/"jump"/"fall" clips on an
/// AnimationPlayer). The shipped GLB is a single rigid mesh with no skin or clips, so the locomotion
/// clips are synthesized here by bobbing/tilting an inner "Face" pivot — see
/// Assets/Characters/SpaceBunny/README.md. If a future variant of the GLB carries real clips whose
/// names start with the canonical names in any casing, those clips are aliased and win over synthesis.
/// </summary>
public static class SpaceBunnyRunner
{
    public const string ScenePath = "res://Assets/Characters/SpaceBunny/space_bunny_alpha.glb";

    /// <summary>Feet-to-ear height in metres; any unit scale in the GLB is fitted to it.</summary>
    private const float TargetHeight = 1.8f;

    /// <summary>Yaw applied to the imported model so the bunny faces -Z. Flip to MathF.PI if QA shows it running backwards.</summary>
    private const float FacingYaw = 0f;

    private const float TwoPi = MathF.PI * 2f;

    private static readonly string[] CanonicalClips = ["idle", "walk", "run", "jump", "fall"];

    /// <summary>
    /// True when the GLB is in the project. It is a local placeholder that is not part of the repository, so without it
    /// the viewer uses the race models from the client instead and the runner test has nothing to show.
    /// </summary>
    public static bool Available => ResourceLoader.Exists(ScenePath);

    /// <summary>Builds the bunny as a ready-to-attach CharacterNode. Returns null if the asset is missing.</summary>
    public static CharacterNode? Build()
    {
        if (!Available)
            return null;
        if (GD.Load<PackedScene>(ScenePath) is not { } packed || packed.Instantiate() is not Node3D instance)
        {
            GD.PrintErr($"SpaceBunnyRunner: could not load {ScenePath} (run godot --headless --import after copying the GLB).");
            return null;
        }
        var character = new CharacterNode { Name = "SpaceBunny" };
        var model = new Node3D { Name = "Model" };
        var face = new Node3D { Name = "Face", Rotation = new Vector3(0, FacingYaw, 0) };
        character.AddChild(model);
        model.AddChild(face);
        face.AddChild(instance);
        FitToHeight(instance, model);
        character.Animations = TryUseRealClips(instance) ?? SynthesizeClips(character);
        GD.Print($"SpaceBunny: model loaded, animation mode={(character.AnimationGraph == null && character.Animations != null ? "canonical clips" : "graph")}.");
        return character;
    }

    /// <summary>Scales the model to <see cref="TargetHeight"/> and drops its feet to y = 0, centring it over the origin.</summary>
    private static void FitToHeight(Node3D instance, Node3D model)
    {
        var aabb = default(Aabb);
        var any = false;
        AccumulateAabb(instance, Transform3D.Identity, ref aabb, ref any);
        if (!any || aabb.Size.Y <= 0f)
            return;
        model.Scale = Vector3.One * (TargetHeight / aabb.Size.Y);
        instance.Position = new Vector3(
            -(aabb.Position.X + aabb.Size.X / 2f), -aabb.Position.Y, -(aabb.Position.Z + aabb.Size.Z / 2f));
    }

    private static void AccumulateAabb(Node3D node, Transform3D xform, ref Aabb aabb, ref bool any)
    {
        if (node is MeshInstance3D { Mesh: { } mesh })
        {
            var bound = xform * mesh.GetAabb();
            aabb = any ? aabb.Merge(bound) : bound;
            any = true;
        }
        foreach (var child in node.GetChildren())
            if (child is Node3D child3d)
                AccumulateAabb(child3d, xform * child3d.Transform, ref aabb, ref any);
    }

    /// <summary>Uses the GLB's own AnimationPlayer when it carries at least two canonical clips; null means synthesize.</summary>
    private static AnimationPlayer? TryUseRealClips(Node3D instance)
    {
        var player = FindAnimationPlayer(instance);
        if (player == null)
            return null;
        AnimationLibrary? library = null;
        var matched = 0;
        foreach (var name in player.GetAnimationList())
        {
            var lower = name.ToString().ToLowerInvariant();
            var canonical = CanonicalClips.FirstOrDefault(c => lower.StartsWith(c, StringComparison.Ordinal));
            if (canonical == null || player.HasAnimation(canonical))
                continue;
            library ??= player.HasAnimationLibrary("")
                ? player.GetAnimationLibrary("")
                : new AnimationLibrary();
            library.AddAnimation(canonical, player.GetAnimation(name));
            matched++;
        }
        if (matched < 2)
            return null;
        if (!player.HasAnimationLibrary(""))
            player.AddAnimationLibrary("", library!);
        return player;
    }

    private static AnimationPlayer? FindAnimationPlayer(Node node) =>
        node as AnimationPlayer ?? node.GetChildren().OfType<Node>().Select(FindAnimationPlayer).FirstOrDefault(p => p != null);

    /// <summary>Procedural stand-in clips: the rigid bunny bobs, leans and stretches on the "Face" pivot.</summary>
    private static AnimationPlayer SynthesizeClips(CharacterNode character)
    {
        var player = new AnimationPlayer { Name = "Animations" };
        character.AddChild(player);
        var library = new AnimationLibrary();
        library.AddAnimation("idle", Loop(2.0f, phase => Pose(phase, bob: 0.015f, tilt: 0f, sway: 0.012f)));
        library.AddAnimation("walk", Loop(0.8f, phase => Pose(phase, bob: 0.05f, tilt: -0.06f, sway: 0.025f)));
        library.AddAnimation("run", Loop(0.45f, phase => Pose(phase, bob: 0.11f, tilt: -0.15f, sway: 0.04f, stretch: 0.05f)));
        library.AddAnimation("jump", Jump());
        library.AddAnimation("fall", Loop(0.6f, phase => Pose(phase, bob: 0.02f, tilt: -0.10f, sway: 0.07f)));
        player.AddAnimationLibrary("", library);
        return player;
    }

    private static (Vector3 Position, Quaternion Rotation, Vector3 Scale) Pose(float phase, float bob, float tilt, float sway, float stretch = 0f)
    {
        var step = MathF.Abs(MathF.Sin(phase));
        var wobble = MathF.Abs(MathF.Sin(2f * phase));
        return (
            new Vector3(0, bob * step, 0),
            Quaternion.FromEuler(new Vector3(tilt * (0.8f + 0.2f * wobble), 0, sway * MathF.Sin(phase))),
            new Vector3(1 - stretch * step * 0.5f, 1 + stretch * step, 1 - stretch * step * 0.5f));
    }

    /// <summary>Eight cycle keys per clip; loop interpolation wraps the last key into the first.</summary>
    private static Animation Loop(float length, Func<float, (Vector3 Position, Quaternion Rotation, Vector3 Scale)> pose)
    {
        var anim = new Animation { Length = length, LoopMode = Animation.LoopModeEnum.Linear, Step = length / 16f };
        for (var kind = 0; kind < 3; kind++)
        {
            var (type, path, cubic) = kind switch
            {
                0 => (Animation.TrackType.Position3D, "Model/Face:position", true),
                1 => (Animation.TrackType.Rotation3D, "Model/Face:rotation", false),
                _ => (Animation.TrackType.Scale3D, "Model/Face:scale", true),
            };
            var track = anim.AddTrack(type);
            anim.TrackSetPath(track, path);
            anim.TrackSetInterpolationLoopWrap(track, true);
            if (cubic)
                anim.TrackSetInterpolationType(track, Animation.InterpolationType.Cubic);
            for (var i = 0; i < 8; i++)
            {
                var t = length * i / 8f;
                var p = pose(TwoPi * i / 8f);
                anim.TrackInsertKey(track, t, kind switch
                {
                    0 => p.Position,
                    1 => p.Rotation,
                    _ => p.Scale,
                });
            }
        }
        return anim;
    }

    /// <summary>One-shot launch stretch; the pose is held until the fall clip takes over.</summary>
    private static Animation Jump()
    {
        var anim = new Animation { Length = 0.45f, LoopMode = Animation.LoopModeEnum.None, Step = 0.05f };
        var position = anim.AddTrack(Animation.TrackType.Position3D);
        anim.TrackSetPath(position, "Model/Face:position");
        anim.TrackInsertKey(position, 0f, Vector3.Zero);
        anim.TrackInsertKey(position, 0.15f, new Vector3(0, 0.12f, 0));
        anim.TrackInsertKey(position, 0.45f, new Vector3(0, 0.04f, 0));
        var rotation = anim.AddTrack(Animation.TrackType.Rotation3D);
        anim.TrackSetPath(rotation, "Model/Face:rotation");
        anim.TrackInsertKey(rotation, 0f, Quaternion.FromEuler(new Vector3(-0.15f, 0, 0)));
        anim.TrackInsertKey(rotation, 0.45f, Quaternion.FromEuler(new Vector3(0.10f, 0, 0)));
        var scale = anim.AddTrack(Animation.TrackType.Scale3D);
        anim.TrackSetPath(scale, "Model/Face:scale");
        anim.TrackInsertKey(scale, 0f, Vector3.One);
        anim.TrackInsertKey(scale, 0.12f, new Vector3(0.92f, 1.12f, 0.92f));
        anim.TrackInsertKey(scale, 0.45f, new Vector3(0.98f, 1.02f, 0.98f));
        return anim;
    }
}

