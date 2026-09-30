#nullable enable
using System.Globalization;
using System.Numerics;
using System.Xml.Linq;
using AAEmu.GodotViewer.Effects;
using AAEmu.GodotViewer.Ui.X2.Scripting;
using Godot;
using GVector3 = Godot.Vector3;
using NVector3 = System.Numerics.Vector3;

namespace AAEmu.GodotViewer.Ui.X2;

public partial class X2UiLayer
{
    /// <summary>The client's login2 world, shared by character selection and all creation pages.</summary>
    private sealed partial class LoginStageScene : IDisposable
    {
        private const string WorldRoot = "game/worlds/login2";
        private const string EntityPath = WorldRoot + "/cells/000_001/client/entities.xml";
        private readonly ModelViewScene _scene;
        private readonly ModelLibrary _models;
        private readonly WorldStreamer _world;
        private readonly WorldEnvironment _worldEnvironment;
        private readonly DirectionalLight3D _sun;
        private readonly LobbyBackdrop _backdrop = new();
        private readonly Dictionary<string, (string Effect, Transform3D Transform)> _levelFx = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, (string Effect, Node3D Anchor)> _fxNodes = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _fxLibraries = new(StringComparer.OrdinalIgnoreCase);
        private Node3D? _stageRoot;
        private EffectPlayer? _fx;
        // The race page's effect libraries (4 MB of XML each) are parsed on a worker while the select page is up.
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Task<ParticleLibrary?>> _fxParsing = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Transform3D> _transforms = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, float> _fovs = new(StringComparer.OrdinalIgnoreCase);
        private readonly Node3D _portraitLights = new() { Name = "LoginPortraitLights" };
        private readonly Action<string> _log;
        private EnvironmentReader? _environment;
        private int _zone = -1;
        private int _stage = -1;
        private string _code = "";
        private float _hour = float.NaN;
        private bool _customizingLight = true;
        private bool _disposed;
        // The page's own camera and actor (what Update chose for the current stage); a cinema overrides them and hands them back.
        private string? _pageCamera, _pageActor;
        private int _pageStage = -1;

        public static LoginStageScene? TryCreate(ModelViewScene scene, ModelLibrary models, Action<Action> post, Action<string> log)
        {
            if (!PakFiles.Exists(WorldRoot + "/world.xml") || !PakFiles.Exists(EntityPath))
            {
                log("[x2] login2 world or camera entities absent from pak; using model-view backdrop");
                return null;
            }
            try { return new LoginStageScene(scene, models, post, log); }
            catch (Exception e)
            {
                log($"[x2] login2 stage: {e.Message}");
                return null;
            }
        }

        private LoginStageScene(ModelViewScene scene, ModelLibrary models, Action<Action> post, Action<string> log)
        {
            _scene = scene;
            _models = models;
            _log = log;
            // The original's login cameras clip at 0.3 m (its debug overlay shows ZN=0.3): nothing right at the lens, e.g. an effect around
            // the actor's camera bone during a class cutscene, washes the whole picture.
            scene.Camera.Near = 0.3f;
            ReadEntities();
            var root = new Node3D { Name = "Login2Stage" };
            _stageRoot = root;
            scene.Viewport.AddChild(root);
            scene.Viewport.AddChild(_portraitLights);
            var bank = new DetailLayerBank(post);
            bank.Create();
            // The selection platform and its cinematic markers live in cell 000_001. One ring
            // includes every cell in this 2x2 world, and the stage is never rebuilt on page changes.
            _world = new WorldStreamer(root, action => post(() => { if (!_disposed && GodotObject.IsInstanceValid(root)) action(); }),
                models, bank, "login2", 0, 1, 1, 1)
            {
                TerrainStep = 4,
                TerrainTextureLevel = 2,
                ShowObjects = true,
            };
            _world.SetFocus(106.6f, 2030.8f);
            _world.Start();
            foreach (var library in new[] { "x2_effect_o", "x2_effects_k" })
            {
                var path = $"game/libs/particles/{library}.xml";
                _fxParsing[path] = Task.Run<ParticleLibrary?>(() => ParticleLibraryReader.TryLoad(path, out var parsed) ? parsed : null);
            }
            AddLobbyOcean(root, _backdrop);

            foreach (var light in scene.Viewport.GetChildren().OfType<DirectionalLight3D>())
                light.QueueFree();
            _worldEnvironment = scene.Viewport.GetChildren().OfType<WorldEnvironment>().First();
            _worldEnvironment.Environment = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Sky,
                Sky = _backdrop.CreateSky(),
                AmbientLightSource = Godot.Environment.AmbientSource.Color,
                // The measured backdrop colours are display values, so the lobby is not tone mapped (the original's film curve
                // at 06:30 is near linear: shoulder 0.95, midtones 1.0, toe 0.92).
                TonemapMode = Godot.Environment.ToneMapper.Linear,
                FogEnabled = false,
            };
            _sun = new DirectionalLight3D { ShadowEnabled = true, DirectionalShadowMaxDistance = 100f };
            scene.Viewport.AddChild(_sun);
            log("[x2] loading original login2 world and camera entities");
        }

        /// <summary>
        /// The stage's floor is the level's ocean: the actors stand at height 1.0 and world.xml's oceanLevel is 1, with the
        /// dedicated flat "login_stage_for_lobby" mirror material (env.xml). The world streamer only draws oceans from authored
        /// Ocean volumes or coral-seabed inference, neither of which login2 has, so the stage gets its own plane here (colours from
        /// <see cref="LobbyBackdrop"/>). The world renderer is untouched; this plane only exists in the lobby.
        /// </summary>
        private static void AddLobbyOcean(Node3D root, LobbyBackdrop backdrop)
        {
            var level = 1f;
            var worldXml = PakFiles.ReadText(WorldRoot + "/world.xml");
            if (worldXml != null)
            {
                try
                {
                    var text = (string?)XDocument.Parse(worldXml.TrimStart('\uFEFF')).Root?.Attribute("oceanLevel");
                    if (float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)) level = parsed;
                }
                catch (Exception) { /* keep the default */ }
            }
            root.AddChild(backdrop.CreateFloor(level, new GVector3(512f, 0f, -512f)));
        }

        private void ReadEntities()
        {
            var xml = PakFiles.ReadText(EntityPath) ?? throw new FileNotFoundException(EntityPath);
            var document = XDocument.Parse(xml.TrimStart('\uFEFF'));
            foreach (var entity in document.Descendants("Entity"))
            {
                var name = ((string?)entity.Attribute("Name"))?.Trim();
                // Persistent particle emitters of the level (race page dust, portal landing); a cinema sequence switches them on.
                if (!string.IsNullOrEmpty(name) && string.Equals((string?)entity.Attribute("EntityClass"), "ParticleEffect", StringComparison.OrdinalIgnoreCase)
                    && ((string?)entity.Element("Properties")?.Attribute("ParticleEffect"))?.Trim() is { Length: > 0 } levelEffect
                    && EntityTransform(entity) is { } levelTransform)
                    _levelFx[name] = (levelEffect, levelTransform);
                if (string.IsNullOrEmpty(name) || !name.StartsWith("0", StringComparison.Ordinal)) continue;
                var position = Floats((string?)entity.Attribute("Pos"));
                var rotation = Floats((string?)entity.Attribute("Rotate"));
                if (position.Length < 3 || rotation.Length < 4) continue;
                var q = System.Numerics.Quaternion.Normalize(new System.Numerics.Quaternion(rotation[1], rotation[2], rotation[3], rotation[0]));
                var matrix = Matrix4x4.CreateFromQuaternion(q) * Matrix4x4.CreateTranslation(position[0], position[1], position[2]);
                // Entity positions in this file are relative to cell 000_001; the streamer's origin is that cell.
                _transforms[name] = CryAxes.FromRowVector(matrix, NVector3.Zero);
                var fov = Floats((string?)entity.Attribute("FOV"));
                if (fov.Length > 0) _fovs[name] = Mathf.RadToDeg(fov[0]);
            }
        }

        /// <summary>Cell-local Cry position and rotation of an entity as a viewport transform.</summary>
        private static Transform3D? EntityTransform(XElement entity)
        {
            var position = Floats((string?)entity.Attribute("Pos"));
            if (position.Length < 3) return null;
            var rotation = Floats((string?)entity.Attribute("Rotate"));
            var q = rotation.Length >= 4
                ? System.Numerics.Quaternion.Normalize(new System.Numerics.Quaternion(rotation[1], rotation[2], rotation[3], rotation[0]))
                : System.Numerics.Quaternion.Identity;
            var matrix = Matrix4x4.CreateFromQuaternion(q) * Matrix4x4.CreateTranslation(position[0], position[1], position[2]);
            return CryAxes.FromRowVector(matrix, NVector3.Zero);
        }

        private static float[] Floats(string? text) => (text ?? "").Split(',', StringSplitOptions.TrimEntries)
            .Select(x => float.TryParse(x, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? (float?)n : null)
            .Where(x => x.HasValue).Select(x => x!.Value).ToArray();

        public void Update(X2Engine engine)
        {
            if (_disposed) return;
            var zone = _world.ZoneAt(106.6f, 2030.8f);
            if (zone >= 0 && zone != _zone)
            {
                _zone = zone;
                _environment = EnvironmentReader.Load(path => PakFiles.Read($"{WorldRoot}/{path}"), zone);
                _hour = float.NaN;
                _log($"[x2] login2 environment zone {zone}");
            }
            if (_environment != null && _hour != engine.LoginStageHour)
            {
                _hour = engine.LoginStageHour;
                ApplyEnvironment(_environment.Sample(_hour));
            }

            var preview = engine.CharacterPreview;
            var code = preview == null ? "num" : RaceCode(preview.Race, preview.Gender);
            if (_stage == engine.Stage && _code == code && _customizingLight == engine.CustomizingLight) return;
            _stage = engine.Stage;
            _code = code;
            _customizingLight = engine.CustomizingLight;
            var select = engine.Stage == X2Engine.StageSelect;
            var camera = engine.Stage switch
            {
                X2Engine.StageSelect => First($"02_{code}_cam_df", $"02_{code}_cam_fs", $"02_{code}_cam_ty2", "02_num_cam_df"),
                X2Engine.StageCustomize => First($"05_{code}_cs_cam", $"05_{code}_cu_cam", $"05_{code}_hs_cam", $"05_{code}_fs_cam", "05_num_cs_cam", "03_maincam"),
                X2Engine.StageAbility => First("04_maincam", "03_maincam"),
                _ => First("03_maincam"),
            };
            var actor = engine.Stage switch
            {
                X2Engine.StageSelect => First($"02_{code}_PreviewOnly", "02_num_PreviewOnly"),
                X2Engine.StageCustomize => First($"05_mainchr_{code}", "05_mainchr_num", "03_mainchr"),
                X2Engine.StageAbility => null,
                _ => First("03_mainchr"),
            };
            _pageCamera = camera;
            _pageActor = actor;
            _pageStage = engine.Stage;
            if (_cinema == null) ApplyPageView();
            var lights = engine.Stage switch
            {
                X2Engine.StageSelect => $"02_chr_select_{code}_cinema_only_objs.xml",
                X2Engine.StageCustomize => $"05_light_{code}_cam_cinema_only_objs.xml",
                _ => $"03_tribe_select_{code}_light_cinema_only_objs.xml",
            };
            SetPortraitLights(lights);
            SetStageEffects(engine.Stage, code, lights);
            _log($"[x2] login2 stage {engine.Stage} race {code}: camera {camera ?? "missing"}, actor {actor ?? (engine.Stage == X2Engine.StageAbility ? "retained" : "missing")}");
        }

        private static readonly bool SelectAtPreviewMarker = System.Environment.GetEnvironmentVariable("X2_LOBBY_SELECT_ACTOR") == "preview";

        /// <summary>Puts the camera and the actor where the current page wants them.</summary>
        private void ApplyPageView()
        {
            if (_pageCamera != null)
            {
                _scene.Camera.Transform = _transforms[_pageCamera];
                _scene.Camera.Fov = _fovs.GetValueOrDefault(_pageCamera, 30f);
            }
            if (_pageActor == null) return;
            var placement = _transforms[_pageActor];
            // The select page stands the character on the same spot as the creation pages (03_mainchr), not on its "02_*_PreviewOnly"
            // marker: the marker is 0.27 m closer to the camera and the original's select capture (Xyz, camera 02_num_cam_df) matches
            // the 03_mainchr spot to the pixel (character 12% smaller than at the marker, 45 px further right). Height and facing
            // stay those of the marker, which carry the race models' foot offsets.
            if (_pageStage == X2Engine.StageSelect && !SelectAtPreviewMarker && _transforms.TryGetValue("03_mainchr", out var stand))
                placement.Origin = new GVector3(stand.Origin.X, placement.Origin.Y, stand.Origin.Z);
            _scene.Pivot.Transform = placement;
        }

        /// <summary>Last Enable/Disable event of every node of a cinema sequence: the state the sequence leaves its entities in.</summary>
        private static Dictionary<string, bool> SequenceStates(string file)
        {
            var states = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            var text = PakFiles.ReadText($"{WorldRoot}/cinemas/{file}");
            if (text == null) return states;
            foreach (var node in XDocument.Parse(text.TrimStart('\uFEFF')).Descendants("Node"))
            {
                var name = (string?)node.Attribute("Name");
                if (string.IsNullOrEmpty(name)) continue;
                var events = node.Descendants("Key").Select(k => new { Time = Floats((string?)k.Attribute("time")) is { Length: > 0 } t ? t[0] : 0f,
                    Event = (string?)k.Attribute("event") }).Where(k => k.Event is "Enable" or "Disable").OrderBy(k => k.Time).ToArray();
                if (events.Length > 0) states[name] = events[^1].Event == "Enable";
            }
            return states;
        }

        private static IEnumerable<(string Name, string Effect, Transform3D Transform)> CinemaEffects(string file)
        {
            var text = PakFiles.ReadText($"{WorldRoot}/cinemas/{file}");
            if (text == null) yield break;
            foreach (var entity in XDocument.Parse(text.TrimStart('\uFEFF')).Descendants("Entity"))
            {
                if (!string.Equals((string?)entity.Attribute("EntityClass"), "ParticleEffect", StringComparison.OrdinalIgnoreCase)) continue;
                var properties = entity.Element("Properties");
                if ((string?)properties?.Attribute("bActive") == "0") continue;
                var effect = ((string?)properties?.Attribute("ParticleEffect"))?.Trim();
                var name = (string?)entity.Attribute("Name");
                if (string.IsNullOrEmpty(effect) || string.IsNullOrEmpty(name) || EntityTransform(entity) is not { } transform) continue;
                yield return (name, effect, transform);
            }
        }

        /// <summary>
        /// The stage particle emitters, as the original cinema sequences leave them: the background streaks and stars
        /// (BG_fx01/02, in every page light file), and on the race page the portal and the alliance-coloured dust.
        /// </summary>
        private void SetStageEffects(int stage, string code, string lightsFile)
        {
            var wanted = new Dictionary<string, (string Effect, Transform3D Transform)>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, effect, transform) in CinemaEffects(lightsFile)) wanted[name] = (effect, transform);
            if (stage == X2Engine.StageCreate)
            {
                var states = SequenceStates($"03_tribe_select_{code}_light.xml");
                foreach (var pair in SequenceStates($"03_tribe_select_{code}.xml")) states[pair.Key] = pair.Value;
                foreach (var (name, effect, transform) in CinemaEffects($"03_tribe_select_{code}_cinema_only_objs.xml"))
                    if (states.GetValueOrDefault(name, true)) wanted[name] = (effect, transform);
                foreach (var (name, level) in _levelFx)
                    if (states.GetValueOrDefault(name, false)) wanted[name] = level;
            }
            foreach (var name in _fxNodes.Keys.ToArray())
                if (!wanted.TryGetValue(name, out var keep) || !string.Equals(keep.Effect, _fxNodes[name].Effect, StringComparison.OrdinalIgnoreCase))
                {
                    _fxNodes[name].Anchor.QueueFree();
                    _fxNodes.Remove(name);
                }
            var skipped = (System.Environment.GetEnvironmentVariable("X2_LOBBY_NOFX") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var (name, fx) in wanted)
            {
                if (_fxNodes.ContainsKey(name) || _stageRoot == null || skipped.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                if (_fx == null)
                {
                    _fx = new EffectPlayer { Name = "LoginStageEffects", SkipRefractionMaterials = true, SkipMultiplicativeMaterials = true, SkipStaticTails = true, SkipEmitters = ["login_potal11"] };
                    _fx.Factory.FocusGravityBeams = System.Environment.GetEnvironmentVariable("X2_LOBBY_NOBEAMS") != "1";
                    _fx.Factory.DimByEmissive = System.Environment.GetEnvironmentVariable("X2_LOBBY_NODIM") != "1";
                    _scene.Viewport.AddChild(_fx);
                }
                if (fx.Effect is LobbyBackdrop.StarEffect or LobbyBackdrop.RibbonEffect)
                {
                    // Geometry particles of the lobby background: drawn by LobbyBackdrop, not by the particle system.
                    var layer = fx.Effect == LobbyBackdrop.StarEffect ? _backdrop.CreateStars(fx.Transform) : _backdrop.CreateRibbons(fx.Transform);
                    _stageRoot.AddChild(layer);
                    _fxNodes[name] = (fx.Effect, layer);
                    continue;
                }
                var separator = fx.Effect.IndexOf('.');
                var library = $"game/libs/particles/{(separator < 0 ? fx.Effect : fx.Effect[..separator]).ToLowerInvariant()}.xml";
                if (_fxLibraries.Add(library))
                {
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    var loaded = false;
                    if (_fxParsing.TryGetValue(library, out var parsing) && parsing.Result is { } preparsed)
                    {
                        _fx.AddLibrary(preparsed);
                        loaded = true;
                    }
                    else loaded = _fx.LoadLibrary(library);
                    _log(loaded ? $"[x2] login2 effect library {library} ready in {watch.ElapsedMilliseconds} ms" : $"[x2] login2 effect library missing: {library}");
                }
                if (!_fx.Effects.ContainsKey(fx.Effect)) { _log($"[x2] login2 effect missing: {fx.Effect}"); continue; }
                var anchor = new Node3D { Name = name, Transform = fx.Transform };
                _stageRoot.AddChild(anchor);
                _fx.PlayAuthored(fx.Effect, anchor, GVector3.Zero, keepAlive: true);
                _fxNodes[name] = (fx.Effect, anchor);
                _log($"[x2] login2 effect {name}: {fx.Effect}");
            }
        }

        private void SetPortraitLights(string file)
        {
            foreach (var child in _portraitLights.GetChildren()) child.QueueFree();
            _portraitLights.Visible = _stage != X2Engine.StageCustomize || _customizingLight;
            var text = PakFiles.ReadText($"{WorldRoot}/cinemas/{file}");
            if (text == null) return;
            foreach (var entity in XDocument.Parse(text.TrimStart('﻿')).Descendants("Entity"))
                if (MakeLight(entity) is { } light) _portraitLights.AddChild(light);
        }

        /// <summary>The light of a cinema-only Light entity (positions are cell-local; the viewport's origin is cell 000_001).</summary>
        private OmniLight3D? MakeLight(XElement entity)
        {
            if (!string.Equals((string?)entity.Attribute("EntityClass"), "Light", StringComparison.OrdinalIgnoreCase))
                return null;
            var properties = entity.Element("Properties");
            if ((string?)properties?.Attribute("bActive") == "0") return null;
            var position = Floats((string?)entity.Attribute("Pos"));
            if (position.Length < 3) return null;
            var tint = Floats((string?)properties?.Element("Color")?.Attribute("clrDiffuse"));
            var multiplier = Floats((string?)properties?.Element("Color")?.Attribute("fDiffuseMultiplier"));
            var radius = Floats((string?)properties?.Attribute("Radius"));
            var hdr = Floats((string?)properties?.Element("Color")?.Attribute("fHDRDynamic"));
            var specular = Floats((string?)properties?.Element("Color")?.Attribute("fSpecularMultiplier"));
            return new OmniLight3D
            {
                Name = (string?)entity.Attribute("Name") ?? "PortraitLight",
                Position = CryAxes.Point(position[0], position[1], position[2]),
                LightColor = tint.Length >= 3 ? new Color(tint[0], tint[1], tint[2]) : Colors.White,
                LightEnergy = (multiplier.Length > 0 ? Math.Clamp(multiplier[0], .1f, 8f) : 1f) * LightGain(tint, hdr),
                LightSpecular = Math.Clamp((specular.Length > 0 ? specular[0] : 1f) * LightSpecularGain, 0f, 8f),
                OmniRange = (radius.Length > 0 ? Math.Clamp(radius[0], .5f, 20f) : 4f) * LightRangeScale,
                OmniAttenuation = LightAttenuation,
                ShadowEnabled = (string?)entity.Attribute("CastShadow") == "1"
                    && (string?)properties?.Element("Options")?.Attribute("nCastShadows") != "0",
            };
        }

        // Tuned against the original captures (chest / face / legs means of the race and select pages).
        private static float EnvFloat(string name, float fallback) =>
            float.TryParse(System.Environment.GetEnvironmentVariable(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;
        private static readonly float LightWhiteGain = EnvFloat("X2_LOBBY_LIGHT_WHITE", 0f); // > 0 overrides the per-stage gains
        private static readonly float LightBlueGain = EnvFloat("X2_LOBBY_LIGHT_BLUE", 0.5f);
        private static readonly float LightRangeScale = EnvFloat("X2_LOBBY_LIGHT_RANGE", 1f);
        private static readonly float LightAttenuation = EnvFloat("X2_LOBBY_LIGHT_ATT", 0.6f);
        private static readonly float LightSpecularGain = EnvFloat("X2_LOBBY_LIGHT_SPEC", 0.5f);
        private float LightGain(float[] tint, float[] hdr) =>
            tint.Length >= 3 && tint[2] > tint[0] + .2f ? LightBlueGain
            : LightWhiteGain > 0f ? LightWhiteGain
            : _stage == X2Engine.StageCustomize ? LightGainCustomize : LightGainStage;
        private static readonly float LightGainStage = EnvFloat("X2_LOBBY_LIGHT_STAGE", 3.0f);
        private static readonly float LightGainCustomize = EnvFloat("X2_LOBBY_LIGHT_CUSTOM", 1.6f);

        private string? First(params string[] names) => names.FirstOrDefault(_transforms.ContainsKey);

        private static string RaceCode(string race, string gender)
        {
            var female = gender.Equals("female", StringComparison.OrdinalIgnoreCase);
            return race.ToLowerInvariant() switch
            {
                "nuian" => female ? "nuf" : "num",
                "elf" => female ? "elf" : "elm",
                "hariharan" => female ? "haf" : "ham",
                "ferre" => female ? "fef" : "fem",
                "dwarf" => female ? "dwf" : "dwm",
                "warborn" => female ? "waf" : "wam",
                "fairy" => female ? "faf" : "fam",
                "returned" => female ? "ref" : "rem",
                "daru" => "dam",
                _ => female ? "nuf" : "num",
            };
        }

        private void ApplyEnvironment(EnvironmentSample e)
        {
            static Color Clamp(NVector3 c) => new(Math.Clamp(c.X, 0, 1), Math.Clamp(c.Y, 0, 1), Math.Clamp(c.Z, 0, 1));
            static Color Normalize(NVector3 c, float energy)
            {
                var max = Math.Max(c.X, Math.Max(c.Y, Math.Max(c.Z, 1e-4f)));
                return new Color(c.X / max * energy, c.Y / max * energy, c.Z / max * energy);
            }
            var env = _worldEnvironment.Environment;
            var ray = CryAxes.Point(e.SunVector);
            if (ray.LengthSquared() > 1e-6f && Math.Abs(ray.Normalized().Dot(GVector3.Up)) < .999f)
                _sun.Basis = Basis.LookingAt(ray.Normalized(), GVector3.Up);
            var sunMax = Math.Max(e.SunColor.X, Math.Max(e.SunColor.Y, e.SunColor.Z));
            _sun.LightColor = Normalize(e.SunColor, 1f);
            _sun.LightEnergy = (float)Math.Clamp(e.SunIntensity * sunMax * .16, .05, 1.5);
            env.AmbientLightColor = Clamp(e.AmbientColor);
            // The gradients in LobbyBackdrop are the original's 06:30 look. Other hours (the dev time-of-day slider) scale them by the
            // zone's sky colour relative to 06:30, with a floor so no channel vanishes: at 12:00 that gives the original's red-brown
            // (measured sky (61,31,28), ours (61,35,26)); 00:00 is a rough teal where the original is violet (its extra TOD terms are
            // not modelled).
            var reference = _environment!.Sample(6.5);
            static float Ratio(float now, float then) => then > 1e-4f ? Math.Max(now / then, .4f) : 1f;
            _backdrop.SetTint(new GVector3(Ratio(e.SkyColor.X, reference.SkyColor.X), Ratio(e.SkyColor.Y, reference.SkyColor.Y),
                Ratio(e.SkyColor.Z, reference.SkyColor.Z)));
        }

        public void Dispose()
        {
            _disposed = true;
            _world.Stop();
        }
    }
}
