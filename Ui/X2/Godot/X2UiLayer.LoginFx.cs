#nullable enable
using System.Globalization;
using System.Numerics;
using System.Xml.Linq;
using AAEmu.GodotViewer.Data;
using AAEmu.GodotViewer.Effects;
using Godot;
using GVector3 = Godot.Vector3;
using NVector3 = System.Numerics.Vector3;

namespace AAEmu.GodotViewer.Ui.X2;

// Login-stage effects: particle effects started by animation events of the lobby character's clips (class clips, skill clips), the
// skill fx groups the "!skill_effect" events select, and the class cutscene (the 04_ability_* cinema: camera on the actor's camera
// bone, timed particle entities, the travelling light). All of it plays in the login viewport through the stage's EffectPlayer.
public partial class X2UiLayer
{
    /// <summary>The live effect of one spawn: freed when it expires or is stopped.</summary>
    private sealed class FxHolder
    {
        public required Node Owner;
        public EffectPlayer? Player;
        public double ExpireAt = double.MaxValue;
        public bool Continuous;
        public string Tag = "";
        public bool Stopped;
    }

    private enum FxState { Pending, Ready, Missing }

    /// <summary>Plays the fx items of a skill's fx group as the skill's animation events name the phases (form, throw, launch, hit, end).</summary>
    private sealed class SkillFxRun
    {
        public required IReadOnlyList<FxGroupItemData> Items;
        public required CharacterNode Character;
        public readonly List<(FxGroupItemData Item, FxHolder Holder)> Active = [];
        public readonly HashSet<int> Fired = [];
        public bool AnyEventDriven;
    }

    /// <summary>Animation events of one playing clip, fired in time (a looping clip fires them again each cycle).</summary>
    private sealed class ClipStream
    {
        public required CharacterNode Character;
        public required string Alias;
        public required IReadOnlyList<AnimEvent> Events;
        public double StartAt;
        public float Length;
        public bool Loop;
        public int Next;
        public SkillFxRun? Skill;
        /// <summary>Effects that never end by themselves (an idle's aura) live until the stream is stopped instead of being cut after a while.</summary>
        public bool Persistent;
        public readonly List<FxHolder> Holders = [];
    }

    private sealed partial class LoginStageScene
    {
        private readonly HashSet<string> _fxMissing = new(StringComparer.OrdinalIgnoreCase);
        private static readonly string[] FxSkip = (System.Environment.GetEnvironmentVariable("X2_LOBBY_FX_SKIP") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        private static readonly string[] FxOnly = (System.Environment.GetEnvironmentVariable("X2_LOBBY_FX_ONLY") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        private readonly List<FxHolder> _holders = [];
        private readonly List<ClipStream> _streams = [];
        private readonly List<(double Deadline, Func<bool> Retry)> _deferred = [];

        // Effects of the class cutscene, the class clips and the preview skills play on their own player: it hides what this player cannot
        // draw yet (multiplicative cards go black, opacity-mapped meshes go grey) and plays library roots that are switched off in the editor
        // but whose children play in the client. The race page's portal effects keep the stage player (_fx) as tuned.
        private EffectPlayer? _skillFx;
        private readonly HashSet<string> _skillFxLibraries = new(StringComparer.OrdinalIgnoreCase);

        private EffectPlayer EnsureFx()
        {
            if (_skillFx == null)
            {
                _skillFx = new EffectPlayer { Name = "LoginSkillEffects", SkipRefractionMaterials = true, SkipMultiplicativeMaterials = true,
                    SkipGeometry = ["haje_hammer"], IgnoreDisabledRoot = true, SkipStaticTails = true };
                _scene.Viewport.AddChild(_skillFx);
            }
            return _skillFx;
        }

        private static string FxLibraryPath(string effect)
        {
            var dot = effect.IndexOf('.');
            return $"game/libs/particles/{(dot < 0 ? effect : effect[..dot]).ToLowerInvariant()}.xml";
        }

        /// <summary>Starts parsing the library of an effect on a worker (safe to call every frame).</summary>
        public void PreloadFx(string effect)
        {
            if (string.IsNullOrWhiteSpace(effect) || effect.IndexOf('.') < 0) return;
            var path = FxLibraryPath(effect);
            if (_skillFxLibraries.Contains(path) || _fxMissing.Contains(path) || _fxParsing.ContainsKey(path)) return;
            if (!PakFiles.Exists(path)) { _fxMissing.Add(path); return; }
            _fxParsing[path] = Task.Run<ParticleLibrary?>(() => ParticleLibraryReader.TryLoad(path, out var parsed) ? parsed : null);
        }

        private FxState FxStateOf(string effect)
        {
            if (string.IsNullOrWhiteSpace(effect) || effect.IndexOf('.') < 0) return FxState.Missing;
            var path = FxLibraryPath(effect);
            var fx = EnsureFx();
            if (!_skillFxLibraries.Contains(path))
            {
                PreloadFx(effect);
                if (_fxMissing.Contains(path)) return FxState.Missing;
                if (!_fxParsing.TryGetValue(path, out var task)) return FxState.Missing;
                if (!task.IsCompleted) return FxState.Pending;
                if (task.Result is { } library)
                {
                    fx.AddLibrary(library);
                    _skillFxLibraries.Add(path);
                }
                else { _fxMissing.Add(path); return FxState.Missing; }
            }
            return fx.Effects.ContainsKey(effect) ? FxState.Ready : FxState.Missing;
        }

        private static string? BoneOrNull(string? bone) =>
            string.IsNullOrWhiteSpace(bone) || bone.Equals("none", StringComparison.OrdinalIgnoreCase) ? null : bone.Trim();

        /// <summary>A node under the character (on a bone when it has that bone) that carries an effect at a bone-local offset.</summary>
        private static (Node3D Holder, Node Owner) CreateHolder(CharacterNode character, string? bone, GVector3 offset)
        {
            var skeleton = character.Skeleton;
            Node parent = character;
            Node owner;
            if (BoneOrNull(bone) is { } name && skeleton != null && GodotObject.IsInstanceValid(skeleton))
            {
                var index = skeleton.FindBone(name);
                for (var i = 0; index < 0 && i < skeleton.GetBoneCount(); i++)
                    if (skeleton.GetBoneName(i).ToString().Equals(name, StringComparison.OrdinalIgnoreCase)) index = i;
                if (index >= 0)
                {
                    var attachment = new BoneAttachment3D { BoneName = skeleton.GetBoneName(index) };
                    skeleton.AddChild(attachment);
                    parent = attachment;
                }
            }
            var holder = new Node3D { Position = offset };
            parent.AddChild(holder);
            owner = parent == character ? holder : parent;
            return (holder, owner);
        }

        private FxHolder? SpawnFx(string effect, CharacterNode? character, string? bone, GVector3 cryOffset, Transform3D? world, double now, string tag, bool persistent = false)
        {
            if (_stageRoot == null || FxStateOf(effect) != FxState.Ready) return null;
            // dev knobs: X2_LOBBY_FX_SKIP=<substring>,... drops matching effects, X2_LOBBY_FX_ONLY=<substring>,... keeps only those
            if (FxSkip.Length > 0 && FxSkip.Any(x => effect.Contains(x, StringComparison.OrdinalIgnoreCase))) return null;
            if (FxOnly.Length > 0 && !FxOnly.Any(x => effect.Contains(x, StringComparison.OrdinalIgnoreCase))) return null;
            var fx = EnsureFx();
            Node3D holder;
            Node owner;
            if (character != null)
            {
                if (!GodotObject.IsInstanceValid(character)) return null;
                (holder, owner) = CreateHolder(character, bone, CryAxes.Point(cryOffset.X, cryOffset.Y, cryOffset.Z));
            }
            else
            {
                holder = new Node3D { Transform = world ?? Transform3D.Identity };
                _stageRoot.AddChild(holder);
                owner = holder;
            }
            var continuous = fx.IsAuthoredContinuous(effect);
            var duration = fx.AuthoredDuration(effect);
            EffectPlayer? player = null;
            try { player = fx.PlayAuthored(effect, holder, GVector3.Zero, keepAlive: false); }
            catch (Exception e) { _log($"[x2] login effect {effect}: {e.Message}"); }
            var result = new FxHolder { Owner = owner, Player = player, Continuous = continuous, Tag = tag,
                // finite effects are freed a little after their emitters end; ones that run until stopped are cut after 12 s unless persistent
                ExpireAt = duration >= 0 ? now + duration + 1.5 : persistent ? double.MaxValue : now + 12 };
            _holders.Add(result);
            if (System.Environment.GetEnvironmentVariable("X2_TRACE_PREVIEW") == "1")
                _log($"[x2] login fx {tag}: {effect} bone '{bone}' {(continuous ? "continuous" : "one-shot")}");
            return result;
        }

        /// <summary>Stops an effect's emitters and frees it once its particles have faded.</summary>
        private void StopHolder(FxHolder holder, double now, double fade = 2.5)
        {
            if (holder.Stopped) return;
            holder.Stopped = true;
            if (holder.Player != null && GodotObject.IsInstanceValid(holder.Player)) holder.Player.Stop();
            holder.ExpireAt = Math.Min(holder.ExpireAt, now + fade);
        }

        // ------------------------------------------------------------------ animation-event streams

        /// <summary>
        /// Starts firing the animation events of a clip (a class clip, an idle, a skill clip) that begins now.
        /// <paramref name="length"/> is the clip length in seconds; a looping clip fires its events again every cycle.
        /// </summary>
        public ClipStream? StartClipEvents(CharacterNode character, string alias, double now, float length, bool loop, SkillFxRun? skill = null, bool persistent = false)
        {
            var events = AnimEventDatabase.For(character.AnimationListPath, alias);
            foreach (var e in events) if (e.IsEffect) PreloadFx(e.Parameter);
            if (events.Count == 0) return null;
            var stream = new ClipStream { Character = character, Alias = alias, Events = events, StartAt = now, Length = length, Loop = loop, Skill = skill, Persistent = persistent };
            if (skill != null && events.Any(e => e.IsSkillEffect)) skill.AnyEventDriven = true;
            _streams.Add(stream);
            if (System.Environment.GetEnvironmentVariable("X2_TRACE_PREVIEW") == "1")
                _log($"[x2] clip events {alias}: {events.Count} ({string.Join(' ', events.Select(e => $"{e.Name}:{e.Parameter}@{e.Time:F2}"))})");
            return stream;
        }

        public void StopStream(ClipStream? stream, double now, bool killEffects)
        {
            if (stream == null) return;
            _streams.Remove(stream);
            if (killEffects) foreach (var holder in stream.Holders) StopHolder(holder, now, 1.0);
        }

        /// <summary>Fires the fx items a skill phase starts and stops the ones it ends (fx events: 0 start, 1 form, 2 throw, 3 launch, 4 hit, 5 end).</summary>
        public void FireSkillPhase(SkillFxRun run, int phase, string? eventBone, double now)
        {
            run.Fired.Add(phase);
            foreach (var active in run.Active.ToArray())
                if (active.Item.EndEventId == phase && active.Item.StartEventId != phase)
                {
                    StopHolder(active.Holder, now, 1.5);
                    run.Active.Remove(active);
                }
            foreach (var item in run.Items)
            {
                // Items on the target (location 2) need a target: the lobby has none.
                if (item.StartEventId != phase || item.LocationId == 2 || string.IsNullOrWhiteSpace(item.AssetName)) continue;
                var offset = new GVector3(item.OffsetX, item.OffsetY, item.OffsetZ);
                var bone = BoneOrNull(item.Bone) ?? BoneOrNull(eventBone);
                var itemCopy = item;
                bool Spawn()
                {
                    var state = FxStateOf(itemCopy.AssetName);
                    if (state == FxState.Missing) return true;
                    if (state == FxState.Pending) return false;
                    if (!GodotObject.IsInstanceValid(run.Character)) return true;
                    if (SpawnFx(itemCopy.AssetName, run.Character, bone, offset, null, Time.GetTicksMsec() / 1000.0,
                            $"skill fx {itemCopy.Id} phase {phase}") is { } holder)
                        run.Active.Add((itemCopy, holder));
                    return true;
                }
                if (!Spawn()) _deferred.Add((now + 4, Spawn));
            }
        }

        /// <summary>Fires one animation event of a stream.</summary>
        private void DispatchClipEvent(ClipStream stream, AnimEvent e, double now)
        {
            if (e.IsEffect)
            {
                var effect = e.Parameter;
                var offset = new GVector3(e.Offset.X, e.Offset.Y, e.Offset.Z);
                bool Spawn()
                {
                    var state = FxStateOf(effect);
                    if (state == FxState.Missing) return true;
                    if (state == FxState.Pending) return false;
                    if (!GodotObject.IsInstanceValid(stream.Character)) return true;
                    if (SpawnFx(effect, stream.Character, e.Bone, offset, null, Time.GetTicksMsec() / 1000.0, $"clip {stream.Alias}@{e.Time:F2}", stream.Persistent) is { } holder)
                        stream.Holders.Add(holder);
                    return true;
                }
                if (!Spawn()) _deferred.Add((now + 3, Spawn));
            }
            else if (e.IsSkillEffect && stream.Skill != null)
            {
                var phase = e.Parameter.ToLowerInvariant() switch
                {
                    "start" => 0, "form" => 1, "throw" => 2, "launch" => 3, "hit" => 4, "end" => 5, _ => -1,
                };
                if (phase >= 0) FireSkillPhase(stream.Skill, phase, e.Bone, now);
            }
        }

        /// <summary>Per frame: fires due clip events, retries effects whose library was still parsing, frees expired effects.</summary>
        public void TickFx(double now)
        {
            for (var i = _streams.Count - 1; i >= 0; i--)
            {
                var stream = _streams[i];
                if (!GodotObject.IsInstanceValid(stream.Character)) { _streams.RemoveAt(i); continue; }
                var t = (float)(now - stream.StartAt);
                while (stream.Next < stream.Events.Count && stream.Events[stream.Next].Time <= t)
                    DispatchClipEvent(stream, stream.Events[stream.Next++], now);
                if (stream.Loop && stream.Length > 0.05f && stream.Next >= stream.Events.Count && t >= stream.Length)
                {
                    stream.StartAt += stream.Length;
                    stream.Next = 0;
                }
                else if (!stream.Loop && stream.Next >= stream.Events.Count)
                    _streams.RemoveAt(i); // every event fired; its effects live on (StopStream is a no-op afterwards, the holders expire by themselves)
            }
            for (var i = _deferred.Count - 1; i >= 0; i--)
                if (_deferred[i].Deadline < now || _deferred[i].Retry()) _deferred.RemoveAt(i);
            for (var i = _holders.Count - 1; i >= 0; i--)
            {
                var holder = _holders[i];
                var dead = !GodotObject.IsInstanceValid(holder.Owner);
                if (!dead && holder.ExpireAt <= now)
                {
                    holder.Owner.QueueFree();
                    dead = true;
                }
                if (dead) _holders.RemoveAt(i);
            }
        }

        /// <summary>Stops every clip stream and frees every effect the lobby character carries (a new pick, leaving the page).</summary>
        public void StopAllFx(double now, bool immediate = false)
        {
            _streams.Clear();
            _deferred.Clear();
            foreach (var holder in _holders)
            {
                if (immediate && GodotObject.IsInstanceValid(holder.Owner)) { holder.Owner.QueueFree(); holder.ExpireAt = 0; }
                else StopHolder(holder, now, 0.8);
            }
            if (immediate) _holders.Clear();
        }

        // ------------------------------------------------------------------ class cutscene (04_ability_* cinema sequences)

        private sealed class CinemaNodeData
        {
            public string Name = "";
            public readonly List<(float Time, NVector3 Value)> Position = [];
            public readonly List<(float Time, string Event)> Events = [];
            public string? AttachNode, AttachBone;
            public GVector3 AttachOffset;
            public string? Anim;
            public float[]? Rotation;
            public XElement? Entity;
        }

        private sealed class CinemaData
        {
            public string Name = "";
            public float Duration = 5f;
            public float Fov = 45f;
            public string CameraBone = "camera";
            public string ActorName = "";
            public Transform3D Actor = Transform3D.Identity;
            public readonly List<CinemaNodeData> Nodes = [];
        }

        private sealed class CinemaEffectRun
        {
            public required CinemaNodeData Node;
            public required string Effect;
            public int NextEvent;
            public bool Wanted;
            public FxHolder? Holder;
        }

        private sealed class CinemaRun
        {
            public required CinemaData Data;
            public required CharacterNode Character;
            public int CameraBone = -1;
            public readonly List<CinemaEffectRun> Effects = [];
            public OmniLight3D? Light;
            public CinemaNodeData? LightNode;
        }

        private CinemaRun? _cinema;
        public bool CinemaActive => _cinema != null;
        public float CinemaDuration => _cinema?.Data.Duration ?? 0f;
        /// <summary>Race code of the page's character (num, nuf, elm, ...): it names the race's stage cameras and cinemas.</summary>
        public string PageRaceCode => _code;

        private static string? CinemaKey(string startSignal) => (startSignal.StartsWith("loginstage_class_", StringComparison.OrdinalIgnoreCase)
            ? startSignal["loginstage_class_".Length..] : startSignal).ToLowerInvariant() switch
        {
            "melee" => "fight", "sorcerer" => "magic", "ranger" => "wild", "healer" => "love", "abyssal" => "hatred",
            "assassin" => "assassin", "madness" => "madness", "pleasure" => "pleasure", _ => null,
        };

        private static NVector3 CryVector(string? text)
        {
            var v = Floats(text);
            return v.Length >= 3 ? new NVector3(v[0], v[1], v[2]) : NVector3.Zero;
        }

        /// <summary>Reads the cinema sequence of a class for a race (nuian male when the race has none); null when the client has none.</summary>
        public CinemaDataHandle? LoadCinema(string startSignal, string raceCode)
        {
            var key = CinemaKey(startSignal);
            if (key == null) return null;
            foreach (var code in new[] { raceCode, "num" }.Distinct())
            {
                var file = $"04_ability_{key}_{code}.xml";
                var text = PakFiles.ReadText($"{WorldRoot}/cinemas/{file}");
                if (text == null) continue;
                var objects = PakFiles.ReadText($"{WorldRoot}/cinemas/{file[..^4]}_cinema_only_objs.xml");
                try { return new CinemaDataHandle(ParseCinema(file[..^4], text, objects)); }
                catch (Exception e) { _log($"[x2] cinema {file}: {e.Message}"); }
            }
            return null;
        }

        /// <summary>Opaque holder so the layer can keep a parsed cinema without seeing the stage's private types.</summary>
        public sealed class CinemaDataHandle
        {
            internal CinemaDataHandle(object data) => Data = data;
            internal object Data { get; }
        }

        /// <summary>An entity's transform with its Scale (the entity's own times the effect property's, e.g. the fire rain hits are 0.4).</summary>
        private static Transform3D? ScaledEntityTransform(XElement entity)
        {
            if (EntityTransform(entity) is not { } transform) return null;
            var own = Floats((string?)entity.Attribute("Scale"));
            var property = Floats((string?)entity.Element("Properties")?.Attribute("Scale"));
            var scale = (own.Length > 0 ? own[0] : 1f) * (property.Length > 0 ? property[0] : 1f);
            transform.Basis = transform.Basis.Scaled(GVector3.One * scale);
            return transform;
        }

        private static CinemaData ParseCinema(string name, string sequenceXml, string? objectsXml)
        {
            var data = new CinemaData { Name = name };
            var entities = new Dictionary<string, XElement>(StringComparer.OrdinalIgnoreCase);
            if (objectsXml != null)
                foreach (var entity in XDocument.Parse(objectsXml.TrimStart('﻿')).Descendants("Entity"))
                    if ((string?)entity.Attribute("Name") is { Length: > 0 } entityName) entities[entityName] = entity;
            var sequence = XDocument.Parse(sequenceXml.TrimStart('﻿')).Root!;
            if (float.TryParse((string?)sequence.Attribute("EndTime"), NumberStyles.Float, CultureInfo.InvariantCulture, out var end)) data.Duration = end;
            string cameraNode = "";
            var cameraFov = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
            var cameraAttach = new Dictionary<string, (string Node, string Bone)>(StringComparer.OrdinalIgnoreCase);
            foreach (var node in sequence.Descendants("Node"))
            {
                var nodeName = (string?)node.Attribute("Name") ?? "";
                var type = (string?)node.Attribute("Type");
                var item = new CinemaNodeData { Name = nodeName };
                entities.TryGetValue(nodeName, out item.Entity);
                foreach (var track in node.Elements("Track"))
                {
                    var keys = track.Elements("Key").ToArray();
                    switch ((string?)track.Attribute("ParamId"))
                    {
                        case "6" when type == "2" && keys.Length > 0:
                            cameraNode = (string?)keys[0].Attribute("node") ?? "";
                            break;
                        case "0" when type == "3" && keys.Length > 0:
                            if (float.TryParse((string?)keys[0].Attribute("value"), NumberStyles.Float, CultureInfo.InvariantCulture, out var fov)) cameraFov[nodeName] = fov;
                            break;
                        case "1":
                            foreach (var key in keys)
                                item.Position.Add((Floats((string?)key.Attribute("time")) is { Length: > 0 } t ? t[0] : 0f, CryVector((string?)key.Attribute("value"))));
                            break;
                        case "2" when keys.Length > 0 && Floats((string?)keys[0].Attribute("value")) is { Length: >= 4 } rotation:
                            item.Rotation = rotation; // w,x,y,z like entity Rotate
                            break;
                        case "4":
                            foreach (var key in keys)
                                if ((string?)key.Attribute("event") is { Length: > 0 } evt)
                                    item.Events.Add((Floats((string?)key.Attribute("time")) is { Length: > 0 } t ? t[0] : 0f, evt));
                            break;
                        case "29" when keys.Length > 0:
                            item.AttachNode = (string?)keys[0].Attribute("node");
                            item.AttachBone = (string?)keys[0].Attribute("boneName");
                            var offset = CryVector((string?)keys[0].Attribute("offset"));
                            item.AttachOffset = new GVector3(offset.X, offset.Y, offset.Z);
                            if (type == "3") cameraAttach[nodeName] = (item.AttachNode ?? "", item.AttachBone ?? "camera");
                            break;
                        case "10" when keys.Length > 0:
                            item.Anim = (string?)keys[0].Attribute("anim");
                            break;
                    }
                }
                data.Nodes.Add(item);
            }
            if (cameraFov.TryGetValue(cameraNode, out var cameraAngle)) data.Fov = cameraAngle;
            if (cameraAttach.TryGetValue(cameraNode, out var attach)) { data.CameraBone = attach.Bone; data.ActorName = attach.Node; }
            var actor = data.Nodes.FirstOrDefault(n => n.Name == data.ActorName);
            if (actor is { Position.Count: > 0 })
            {
                var p = actor.Position[0].Value;
                var r = actor.Rotation;
                // Cry quaternions are stored w,x,y,z; sequence positions are world coordinates (this cell starts at y = 1024).
                var q = r is { Length: >= 4 }
                    ? System.Numerics.Quaternion.Normalize(new System.Numerics.Quaternion(r[1], r[2], r[3], r[0]))
                    : System.Numerics.Quaternion.Identity;
                var matrix = Matrix4x4.CreateFromQuaternion(q) * Matrix4x4.CreateTranslation(p.X, p.Y - 1024f, p.Z);
                data.Actor = CryAxes.FromRowVector(matrix, NVector3.Zero);
            }
            return data;
        }

        /// <summary>Starts the cutscene on this character: camera on its camera bone, timed effects, the travelling light.</summary>
        public void BeginCinema(CinemaDataHandle handle, CharacterNode character, double now)
        {
            if (_cinema != null) EndCinema(now);
            var data = (CinemaData)handle.Data;
            var run = new CinemaRun { Data = data, Character = character };
            var skeleton = character.Skeleton;
            if (skeleton != null) run.CameraBone = skeleton.FindBone(data.CameraBone);
            _scene.Pivot.Transform = data.Actor;
            foreach (var node in data.Nodes)
            {
                if (node.Entity == null) continue;
                var cls = (string?)node.Entity.Attribute("EntityClass");
                if (string.Equals(cls, "ParticleEffect", StringComparison.OrdinalIgnoreCase))
                {
                    var effect = ((string?)node.Entity.Element("Properties")?.Attribute("ParticleEffect"))?.Trim();
                    if (string.IsNullOrEmpty(effect)) continue;
                    PreloadFx(effect);
                    run.Effects.Add(new CinemaEffectRun { Node = node, Effect = effect });
                }
                else if (string.Equals(cls, "Light", StringComparison.OrdinalIgnoreCase) && run.Light == null && MakeLight(node.Entity) is { } light)
                {
                    run.Light = light;
                    run.LightNode = node;
                    _portraitLights.AddChild(light);
                }
            }
            _cinema = run;
            _log($"[x2] cinema {data.Name} begins: {data.Duration:F1} s, camera bone {data.CameraBone} ({run.CameraBone}), fov {data.Fov}, {run.Effects.Count} effects");
        }

        /// <summary>Moves the camera, effects and light to time <paramref name="t"/> of the running cinema.</summary>
        public void TickCinema(double t, double now)
        {
            if (_cinema is not { } run) return;
            var skeleton = run.Character.Skeleton;
            if (skeleton != null && GodotObject.IsInstanceValid(skeleton) && run.CameraBone >= 0)
            {
                _scene.Camera.GlobalTransform = skeleton.GlobalTransform * skeleton.GetBoneGlobalPose(run.CameraBone);
                _scene.Camera.Fov = run.Data.Fov;
            }
            foreach (var effect in run.Effects)
            {
                var events = effect.Node.Events;
                while (effect.NextEvent < events.Count && events[effect.NextEvent].Time <= t)
                {
                    var kind = events[effect.NextEvent++].Event;
                    if (kind == "Enable") effect.Wanted = true;
                    else if (kind == "Disable")
                    {
                        effect.Wanted = false;
                        if (effect.Holder != null) { StopHolder(effect.Holder, now, 1.5); effect.Holder = null; }
                    }
                    else if (kind == "Restart")
                    {
                        effect.Wanted = true;
                        if (effect.Holder != null) { StopHolder(effect.Holder, now, 1.5); effect.Holder = null; }
                    }
                }
                if (!effect.Wanted || effect.Holder != null) continue;
                var node = effect.Node;
                var attached = node.AttachNode != null && node.AttachNode == run.Data.ActorName;
                effect.Holder = attached
                    ? SpawnFx(effect.Effect, run.Character, node.AttachBone,
                        new GVector3(node.AttachOffset.X, node.AttachOffset.Y, node.AttachOffset.Z), null, now, $"cinema {node.Name}")
                    : SpawnFx(effect.Effect, null, null, GVector3.Zero, ScaledEntityTransform(node.Entity!), now, $"cinema {node.Name}");
            }
            if (run.Light != null && run.LightNode is { Position.Count: > 0 } lightNode)
            {
                var keys = lightNode.Position;
                NVector3 value = keys[0].Value;
                for (var i = 1; i < keys.Count; i++)
                {
                    if (t >= keys[i].Time) { value = keys[i].Value; continue; }
                    var span = Math.Max(1e-4f, keys[i].Time - keys[i - 1].Time);
                    value = NVector3.Lerp(keys[i - 1].Value, keys[i].Value, Math.Clamp((float)(t - keys[i - 1].Time) / span, 0f, 1f));
                    break;
                }
                run.Light.Position = CryAxes.Point(value.X, value.Y - 1024f, value.Z);
            }
        }

        /// <summary>Ends the cutscene: the page's camera and actor position come back, its effects and light go.</summary>
        public void EndCinema(double now)
        {
            if (_cinema is not { } run) return;
            _cinema = null;
            foreach (var effect in run.Effects)
                if (effect.Holder != null) StopHolder(effect.Holder, now, 1.0);
            if (run.Light != null && GodotObject.IsInstanceValid(run.Light)) run.Light.QueueFree();
            ApplyPageView();
        }
    }
}
