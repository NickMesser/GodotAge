using System.Data.Common;
using Godot;
using Microsoft.Data.Sqlite;
using NMatrix = System.Numerics.Matrix4x4;
using NVector3 = System.Numerics.Vector3;

namespace AAEmu.GodotViewer;

/// <summary>
/// Builds animated characters (players, NPCs) from the game database and the pak: resolves a model id plus equipment and
/// appearance to files (<see cref="CharacterAssetResolver"/>), reads the skeleton, skinned parts, rigid attachments and
/// locomotion clips on a worker thread, and creates the Godot nodes on the main thread through <c>post</c>.
/// Builds run one at a time on a single background queue (the resolver and database connection aren't thread-safe).
/// </summary>
internal sealed class CharacterBuilder : IDisposable
{
    private readonly ModelLibrary _models;
    private readonly Action<Action> _post;
    private readonly DbConnection _db;
    private readonly CharacterAssetResolver _resolver;
    private readonly Dictionary<string, ChrSkeleton> _skeletons = [];
    private readonly Dictionary<string, CafAnimation> _clips = [];
    private readonly Dictionary<string, AnimationLibrary> _libraries = [];
    private readonly Dictionary<string, CalFile> _animationLists = [];
    private readonly Dictionary<string, Animation> _actionAnimations = [];
    private readonly System.Collections.Concurrent.BlockingCollection<Action> _queue = new();

    public CharacterBuilder(ModelLibrary models, Action<Action> post, string gameDbPath)
    {
        _models = models;
        _post = post;
        _db = new SqliteConnection($"Data Source={gameDbPath};Mode=ReadOnly");
        _db.Open();
        _resolver = new CharacterAssetResolver(_db, PakFiles.Exists, PakFiles.Read);
        new Thread(() =>
        {
            foreach (var work in _queue.GetConsumingEnumerable())
            {
                try
                {
                    work();
                }
                catch (Exception e)
                {
                    GD.PrintErr($"Character build failed: {e}");
                }
            }
        }) { IsBackground = true, Name = "CharacterBuilder" }.Start();
    }

    /// <summary>Default look of a playable race model (10 = nuian male, 11 = nuian female, ...). <paramref name="ready"/> runs on the main thread.</summary>
    public void BuildPlayerDefault(long modelId, Action<CharacterNode> ready) =>
        _queue.Add(() => Build(_resolver.ResolvePlayerDefault(modelId), $"model {modelId}", ready));

    /// <summary>A character-creation/model-view preview: race defaults plus the supplied preview overrides.</summary>
    public void BuildPlayerPreview(long modelId, IReadOnlyDictionary<int, long> equipment, UnitAppearance appearance,
        Action<CharacterNode> ready) =>
        _queue.Add(() => Build(_resolver.ResolvePlayerPreview(modelId, equipment, appearance), $"preview model {modelId}", ready,
            keepFaceMorph: true));

    /// <summary>An NPC by template id, dressed and scaled as the database describes it.</summary>
    public void BuildNpc(long npcTemplateId, Action<CharacterNode> ready) =>
        _queue.Add(() => Build(_resolver.ResolveNpc(npcTemplateId), $"npc {npcTemplateId}", ready));

    /// <summary>A unit as the server describes it: model id, equipped item templates by equip slot, appearance.</summary>
    public void BuildUnit(long modelId, Dictionary<int, long> equipment, UnitAppearance appearance, Action<CharacterNode> ready) =>
        _queue.Add(() => Build(_resolver.ResolveUnit(modelId, equipment, appearance), $"unit model {modelId}", ready));

    public void Dispose()
    {
        _queue.CompleteAdding();
        _db.Dispose();
    }

    private sealed record SkinnedPart(Godot.Collections.Array[] Surfaces, string[] MaterialKeys, (int Bone, Transform3D InverseBind)[] Binds,
        FaceMorphState FaceMorph = null);

    private sealed record RigidPart(MeshRef Mesh, string Bone, Transform3D Offset);

    /// <summary>Worker thread: reads and converts everything, then queues node creation.</summary>
    /// <param name="keepFaceMorph">Keep the face's morph data on the node (<see cref="CharacterNode.FaceMorph"/>) so the creation
    /// preview can show modifier changes in place; world characters don't need it.</param>
    private void Build(CharacterAssets assets, string label, Action<CharacterNode> ready, bool keepFaceMorph = false)
    {
        var master = Skeleton(assets.SkeletonPath);
        if (master == null)
        {
            GD.PrintErr($"{label}: no skeleton ({assets.Kind} {assets.ModelFile}) {string.Join("; ", assets.Warnings)}");
            return;
        }

        if (System.Environment.GetEnvironmentVariable("GV_PARTS_TRACE") == "1")
            foreach (var part in assets.Parts)
                GD.Print($"{label} part {part.Slot} slot {part.EquipSlot} item {part.ItemId} [{part.Source}] {part.ModelPath} mtl '{part.MaterialPath}'");

        // Read the skinned parts first: their "Cut" sub-materials are volumes that hide body and hair triangles.
        var loaded = new List<(CharacterPart Part, ChrModel Model, string MtlPath, MtlFile Mtl)>();
        var cuts = new List<ChrSubmesh>();
        var rigid = new List<RigidPart>();
        foreach (var part in assets.Parts)
        {
            if (!PakFiles.Exists(part.ModelPath))
                continue;
            if (part.IsSkinned && part.ModelPath.EndsWith(".chr", StringComparison.OrdinalIgnoreCase))
            {
                var model = ChrModelReader.Read(PakFiles.Read(part.ModelPath), master);
                var mtlPath = part.MaterialPath.Length > 0 ? part.MaterialPath
                    : MtlReader.ResolveModelMaterial(part.ModelPath, model.MaterialName, PakFiles.Exists);
                var mtl = _models.Mtl(mtlPath);
                foreach (var s in model.Submeshes)
                    if (ChrCutVolumes.IsCutMaterial(mtl?.ForSubset(s.MaterialIndex)))
                        cuts.Add(s);
                loaded.Add((part, model, mtlPath, mtl));
            }
            else if (part.ModelPath.EndsWith(".cgf", StringComparison.OrdinalIgnoreCase))
            {
                var mesh = _models.Request(part.ModelPath, part.MaterialPath, false);
                var bone = part.SheathBoneName.Length > 0 ? part.SheathBoneName : part.BoneName;
                if (mesh != null && bone.Length > 0)
                {
                    var offset = CryAxes.FromRowVector(part.BoneOffset, NVector3.Zero);
                    rigid.Add(new RigidPart(mesh, bone, offset.ScaledLocal(Vector3.One * part.Scale)));
                }
            }
        }

        var skinned = new List<SkinnedPart>();
        foreach (var (part, model, mtlPath, mtl) in loaded)
        {
            if (part.Slot is "body" or "hair")
                ChrCutVolumes.RemoveInside(model, cuts);
            // the face keeps its unmorphed positions so a later modifier can be shown in place (FaceMorphState)
            var faceBase = keepFaceMorph && part.Slot == "face" && assets.FaceTargetsPath.Length > 0
                ? model.Submeshes.Select(s => (NVector3[])s.Positions.Clone()).ToArray() : null;
            if (part.Slot == "face")
                foreach (var (name, weight) in assets.FaceMorphs)
                {
                    var target = model.FindMorphTarget(name);
                    if (target != null)
                        model.ApplyMorphTarget(target, weight);
                }
            var binds = model.SkinBinds(master)
                .Select(b => (Math.Max(0, b.MasterBone), CryAxes.FromRowVector(b.InverseBind, NVector3.Zero)))
                .ToArray();
            model.FlipWinding();
            var surfaces = new List<Godot.Collections.Array>();
            var materials = new List<string>();
            var drawn = new List<int>();
            for (var si = 0; si < model.Submeshes.Count; si++)
            {
                var s = model.Submeshes[si];
                var sub = mtl?.ForSubset(s.MaterialIndex);
                if (s.Indices.Length == 0 || sub != null && (sub.IsNoDraw || ChrCutVolumes.IsCutMaterial(sub)))
                    continue;
                surfaces.Add(SkinnedArrays(s));
                materials.Add(_models.RequestCharacterMaterial(assets, part, mtlPath, s.MaterialIndex));
                drawn.Add(si);
            }
            FaceMorphState faceMorph = null;
            if (faceBase != null && surfaces.Count > 0 && PakFiles.Read(assets.FaceTargetsPath) is { } targetsXml)
                faceMorph = new FaceMorphState(model, targetsXml, drawn.Select(i => model.Submeshes[i]).ToArray(),
                    drawn.Select(i => faceBase[i]).ToArray(), surfaces.ToArray(), assets.FaceModifier);
            if (surfaces.Count > 0)
                skinned.Add(new SkinnedPart(surfaces.ToArray(), materials.ToArray(), binds, faceMorph));
        }

        var library = Locomotion(assets, master) ??
            (assets.AnimationListPath.Length > 0 ? new AnimationLibrary() : null);
        var bones = master.Bones.Select(b => (b.Name, b.ParentIndex, BindRest: CryAxes.FromRowVector(b.BindLocal, NVector3.Zero))).ToArray();
        var scale = assets.Scale > 0 ? assets.Scale : 1f;
        GD.Print($"{label}: {assets.Kind} {assets.ModelFile}, {skinned.Count} skinned + {rigid.Count} rigid parts, clips {string.Join(" ", library?.GetAnimationList() ?? [])}");

        _post(() =>
        {
            var node = new CharacterNode { Name = "Character", Scale = Vector3.One * scale };
            var skeleton = new Skeleton3D { Name = "Skeleton" };
            node.AddChild(skeleton);
            for (var i = 0; i < bones.Length; i++)
            {
                skeleton.AddBone(bones[i].Name);
                if (bones[i].ParentIndex >= 0)
                    skeleton.SetBoneParent(i, bones[i].ParentIndex);
                skeleton.SetBoneRest(i, bones[i].BindRest);
            }
            skeleton.ResetBonePoses();
            node.Skeleton = skeleton;

            foreach (var part in skinned)
            {
                var skin = new Skin();
                foreach (var (bone, inverseBind) in part.Binds)
                    skin.AddBind(bone, inverseBind);
                var mesh = new ArrayMesh();
                for (var i = 0; i < part.Surfaces.Length; i++)
                {
                    mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, part.Surfaces[i]);
                    mesh.SurfaceSetMaterial(i, _models.GetMaterial(part.MaterialKeys[i]));
                }
                var instance = new MeshInstance3D { Mesh = mesh, Skin = skin };
                skeleton.AddChild(instance);
                instance.Skeleton = instance.GetPathTo(skeleton);
                if (part.FaceMorph != null)
                {
                    part.FaceMorph.Attach(mesh);
                    node.FaceMorph = part.FaceMorph;
                }
            }
            foreach (var part in rigid)
            {
                var mesh = _models.GetMesh(part.Mesh);
                if (mesh == null || skeleton.FindBone(part.Bone) < 0)
                    continue;
                var attachment = new BoneAttachment3D { BoneName = part.Bone };
                skeleton.AddChild(attachment);
                attachment.AddChild(new MeshInstance3D { Mesh = mesh, Transform = part.Offset });
            }
            if (library != null)
            {
                // The cached base library is shared. Each graph needs its own container because it installs lazy clips.
                var instanceLibrary = new AnimationLibrary();
                foreach (var animationName in library.GetAnimationList())
                    instanceLibrary.AddAnimation(animationName, library.GetAnimation(animationName));
                // The player's root is the character node, so the baked tracks' "Skeleton:<bone>" paths resolve.
                var player = new AnimationPlayer { Name = "Animations" };
                node.AddChild(player);
                player.AddAnimationLibrary("", instanceLibrary);
                node.Animations = player;
                node.AnimationListPath = assets.AnimationListPath;
                node.AnimationGraph = new CharacterAnimationGraph(node, skeleton, instanceLibrary,
                    (clip, clipReady) =>
                    {
                        var request = clip == "@movement:" + nameof(MovementPose.Glide) &&
                                      !string.IsNullOrWhiteSpace(assets.GliderAnimation)
                            ? "@glider:" + assets.GliderAnimation
                            : clip;
                        RequestAction(assets.SkeletonPath, assets.AnimationListPath, request, clipReady);
                    });
            }
            ready(node);
        });
    }

    private static Godot.Collections.Array SkinnedArrays(ChrSubmesh s)
    {
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = s.Positions.Select(CryAxes.Point).ToArray();
        if (s.Normals != null)
            arrays[(int)Mesh.ArrayType.Normal] = s.Normals.Select(CryAxes.Point).ToArray();
        if (s.Tangents != null && s.Normals != null)
        {
            var tangents = new float[s.Tangents.Length * 4];
            for (var i = 0; i < s.Tangents.Length; i++)
            {
                var t = CryAxes.Point(s.Tangents[i].X, s.Tangents[i].Y, s.Tangents[i].Z);
                tangents[i * 4] = t.X;
                tangents[i * 4 + 1] = t.Y;
                tangents[i * 4 + 2] = t.Z;
                tangents[i * 4 + 3] = s.Tangents[i].W;
            }
            arrays[(int)Mesh.ArrayType.Tangent] = tangents;
        }
        if (s.UVs != null)
            arrays[(int)Mesh.ArrayType.TexUV] = s.UVs.Select(uv => new Vector2(uv.X, uv.Y)).ToArray();
        arrays[(int)Mesh.ArrayType.Bones] = s.BoneIndices;
        arrays[(int)Mesh.ArrayType.Weights] = s.BoneWeights;
        arrays[(int)Mesh.ArrayType.Index] = s.Indices;
        return arrays;
    }

    private ChrSkeleton Skeleton(string path)
    {
        if (string.IsNullOrEmpty(path))
            return null;
        if (_skeletons.TryGetValue(path, out var cached))
            return cached;
        var bytes = PakFiles.Read(path);
        return _skeletons[path] = bytes == null ? null : ChrModelReader.ReadSkeleton(bytes);
    }

    /// <summary>
    /// The character's idle / walk / run clips baked into Godot animations, shared by every character with the same
    /// skeleton and clips, or null when it has none.
    /// </summary>
    private AnimationLibrary Locomotion(CharacterAssets assets, ChrSkeleton master)
    {
        if (string.IsNullOrEmpty(assets.AnimationListPath) || !PakFiles.Exists(assets.AnimationListPath))
            return null;
        var cal = CalReader.Load(assets.AnimationListPath, PakFiles.Read);
        var (idle, walk, run) = CharacterAssetResolver.LocomotionClips(cal, PakFiles.Read);
        var key = $"{assets.SkeletonPath}|{idle}|{walk}|{run}";
        if (_libraries.TryGetValue(key, out var cached))
            return cached;
        var library = new AnimationLibrary();
        foreach (var (name, path) in new[] { ("idle", idle), ("walk", walk), ("run", run) })
            if (Clip(path) is { } clip)
                library.AddAnimation(name, Bake(master, clip, loop: true));
        return _libraries[key] = library.GetAnimationList().Count > 0 ? library : null;
    }

    private CafAnimation Clip(string clipPath)
    {
        if (string.IsNullOrEmpty(clipPath))
            return null;
        if (!_clips.TryGetValue(clipPath, out var clip))
        {
            var bytes = PakFiles.Read(clipPath);
            _clips[clipPath] = clip = bytes == null ? null : CafReader.Read(bytes);
        }
        return clip;
    }

    /// <summary>Queues name resolution and CAF baking on the character worker, then returns on the main thread.</summary>
    private void RequestAction(string skeletonPath, string calPath, string name, Action<Animation> ready)
    {
        if (_queue.IsAddingCompleted)
            return;
        _queue.Add(() =>
        {
            Animation animation = null;
            try
            {
                var master = Skeleton(skeletonPath);
                var path = ResolveActionPath(calPath, name);
                if (master != null && path != null)
                {
                    var key = $"{skeletonPath}|{path}";
                    if (!_actionAnimations.TryGetValue(key, out animation))
                    {
                        var clip = Clip(path);
                        animation = clip == null ? null : Bake(master, clip, loop: false);
                        _actionAnimations[key] = animation;
                    }
                }
                if (animation == null)
                    GD.PrintErr($"Animation '{name}' is not a CAF clip in {calPath}");
            }
            catch (Exception e)
            {
                GD.PrintErr($"Animation '{name}' failed: {e}");
            }
            var result = animation;
            _post(() => ready(result));
        });
    }

    private string ResolveActionPath(string calPath, string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;
        // Accept a direct pak CAF path for diagnostics/viewer tools; normal gameplay supplies the .cal alias.
        var direct = CdfReader.PakPath(name);
        if (direct.EndsWith(".caf", StringComparison.OrdinalIgnoreCase) && PakFiles.Exists(direct))
            return direct;
        if (string.IsNullOrEmpty(calPath) || !PakFiles.Exists(calPath))
            return null;
        if (!_animationLists.TryGetValue(calPath, out var cal))
            _animationLists[calPath] = cal = CalReader.Load(calPath, PakFiles.Read);

        if (name.StartsWith("@movement:", StringComparison.Ordinal))
            return ResolveMovementPath(cal, name[10..]);
        if (name.StartsWith("@glider:", StringComparison.Ordinal))
            return ResolveMovementPath(cal, nameof(MovementPose.Glide), name[8..]);

        var alias = name;
        for (var redirects = 0; redirects < 4; redirects++)
        {
            var path = cal.Find(alias);
            // Some DB situation keys use _ub while the player .cal calls the same motion _mub.
            if (path == null && alias.EndsWith("_ub", StringComparison.OrdinalIgnoreCase))
                path = cal.Find(alias[..^3] + "_mub");
            if (path == null && (alias.StartsWith("fist_ac_stage_", StringComparison.OrdinalIgnoreCase) ||
                                 alias.StartsWith("loginstage_", StringComparison.OrdinalIgnoreCase)))
                path = cal.Find("idle");
            if (path == null)
                return null;
            if (path.EndsWith(".caf", StringComparison.OrdinalIgnoreCase))
                return path;
            if (!path.EndsWith(".lmg", StringComparison.OrdinalIgnoreCase))
                return null;
            var bytes = PakFiles.Read(path);
            if (bytes == null)
                return null;
            alias = LmgReader.Parse(bytes).ForwardClip;
            if (string.IsNullOrEmpty(alias))
                return null;
        }
        return null;
    }

    /// <summary>Selects a real clip from this skeleton's CAL by semantic tokens; no global animation id is assumed.</summary>
    private string ResolveMovementPath(CalFile cal, string pose, string gliderFamily = "")
    {
        string[][] tokenSets = pose switch
        {
            nameof(MovementPose.JumpRise) => [["jump", "start"], ["jump", "up"], ["jump"]],
            nameof(MovementPose.Fall) => [["fall"], ["jump", "loop"], ["jump"]],
            nameof(MovementPose.Land) => [["jump", "land"], ["land"]],
            nameof(MovementPose.SwimIdle) => [["idle", "swim"], ["swim", "idle"], ["swim"]],
            nameof(MovementPose.SwimMove) => [["swim", "_f"], ["swim", "forward"], ["swim", "move"], ["swim"]],
            nameof(MovementPose.SwimDive) => [["swim", "down"], ["swim", "dive"], ["dive"], ["swim"]],
            nameof(MovementPose.Glide) when gliderFamily.Length > 0 =>
                [[gliderFamily, "idle"], ["gliding", "idle"], ["glider", "idle"], ["fly", "idle"]],
            nameof(MovementPose.Glide) => [["gliding", "idle"], ["gliding", "wing", "idle"], ["glider", "idle"], ["fly", "idle"]],
            nameof(MovementPose.Mounted) => [["rider", "horse", "idle"]],
            nameof(MovementPose.MountedRun) => [["rider", "horse", "run"]],
            _ => [],
        };
        var transitionTokens = pose is nameof(MovementPose.Mounted) or nameof(MovementPose.MountedRun)
            ? new[] { "idletorun", "runtoidle", "start", "end" }
            : [];
        foreach (var tokens in tokenSets)
            foreach (var (alias, path) in cal.Animations.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
                if (tokens.All(token => alias.Contains(token, StringComparison.OrdinalIgnoreCase)) &&
                    !transitionTokens.Any(token => alias.Contains(token, StringComparison.OrdinalIgnoreCase)))
                {
                    if (path.EndsWith(".caf", StringComparison.OrdinalIgnoreCase))
                        return path;
                    if (path.EndsWith(".lmg", StringComparison.OrdinalIgnoreCase) && PakFiles.Read(path) is { } lmg)
                    {
                        var forward = LmgReader.Parse(lmg).ForwardClip;
                        if (forward != null && cal.Find(forward) is { } clip && clip.EndsWith(".caf", StringComparison.OrdinalIgnoreCase))
                            return clip;
                    }
                }
        return null;
    }

    /// <summary>
    /// Samples a clip in place (root motion removed) at 30 frames per second into a looping Godot animation with a
    /// rotation track per bone and position tracks for the bones that move.
    /// </summary>
    private static Animation Bake(ChrSkeleton master, CafAnimation clip, bool loop)
    {
        const float fps = 30f;
        var sampler = new AnimationSampler(master, clip);
        var duration = Math.Max(clip.Duration, 1f / fps);
        var frames = Math.Max(2, (int)MathF.Round(duration * fps) + 1);
        var positions = new Vector3[frames, master.Count];
        var rotations = new Quaternion[frames, master.Count];
        for (var f = 0; f < frames; f++)
        {
            var local = sampler.SampleLocal(Math.Min(f / fps, duration));
            for (var b = 0; b < master.Count; b++)
            {
                var t = CryAxes.FromRowVector(local[b], NVector3.Zero);
                positions[f, b] = t.Origin;
                rotations[f, b] = t.Basis.GetRotationQuaternion();
            }
        }

        var animation = new Animation { Length = duration,
            LoopMode = loop ? Animation.LoopModeEnum.Linear : Animation.LoopModeEnum.None };
        for (var b = 0; b < master.Count; b++)
        {
            var rest = CryAxes.FromRowVector(master.Bones[b].BindLocal, NVector3.Zero).Origin;
            var path = new NodePath($"Skeleton:{master.Bones[b].Name}");
            var rotation = animation.AddTrack(Animation.TrackType.Rotation3D);
            animation.TrackSetPath(rotation, path);
            for (var f = 0; f < frames; f++)
                animation.RotationTrackInsertKey(rotation, Math.Min(f / fps, duration), rotations[f, b]);

            // Without a position track the skeleton uses the rest (bind) position, so only skip bones that stay there.
            var leavesRest = false;
            for (var f = 0; f < frames && !leavesRest; f++)
                leavesRest = positions[f, b].DistanceSquaredTo(rest) > 1e-6f;
            if (!leavesRest)
                continue;
            var position = animation.AddTrack(Animation.TrackType.Position3D);
            animation.TrackSetPath(position, path);
            for (var f = 0; f < frames; f++)
                animation.PositionTrackInsertKey(position, Math.Min(f / fps, duration), positions[f, b]);
        }
        return animation;
    }
}
