using System.Collections.Concurrent;
using System.Diagnostics;
using AAEmu.GodotViewer.Client;
using AAEmu.GodotViewer.Client.Targeting;
using AAEmu.GodotViewer.Net;
using AAEmu.GodotViewer.Settings;
using AAEmu.GodotViewer.Ui;
using Godot;
using NVector3 = System.Numerics.Vector3;

namespace AAEmu.GodotViewer;

/// <summary>
/// ArcheAge world viewer and client. Streams the world cells around the camera (fly mode) or the player (walk mode)
/// straight from the client's game_pak: terrain with its cover texture, every placed model with its materials, decals,
/// roads, water, voxel caves, distance clouds and lights. F toggles fly and walk mode. With --online it logs into the
/// AAEmu stack and shows the server's world: the player's own character, NPCs, other players and doodads.
///
/// Command line (after "--"): --pak=&lt;path to game_pak&gt; --db=&lt;client database&gt; --world=main_world --cell=9,14 --radius=1 --far=3 --step=2 --walk
/// --cam=x,y,z,yaw,pitch (Cry coordinates, degrees; z "+40" = 40 m above the terrain) --models=0 --normals=0
/// --player=10 (race model id) --hour=12 --time-scale=0 --orbit=7 --autorun=seconds --screenshot=path.png
/// --render-parity[=0|1] --material-parity[=0|1] --ssao[=0|1] --ssr[=0|1] --glow[=0|1]
/// --glow-bicubic[=0|1] --vegetation-distance=scale --vegetation-shadows[=0|1]
/// --sky-hdr[=0|1] (default 1; 0 preserves the static zone cube/procedural sky path)
/// --water-enhanced[=0|1] --water-waves[=0|1] --water-reflections[=0|1] --water-depth-effects[=0|1]
/// --ocean-vertex-waves[=0|1] (default 0) --water-underwater-fog[=0|1]
/// Water defaults: enhanced/waves/reflections/depth-effects/underwater-fog = 1; ocean-vertex-waves = 0.
/// --shadow-distance=metres --shadow-split1=0..1 --shadow-blur=0..10 --shadow-bias=0..2
/// --shadow-normal-bias=0..10 --world-lights[=0|1] --fov=degrees
/// --perf-report[=interval-seconds] (default interval: 2 seconds)
/// --perf-terrain-lod[=0|1] --perf-far-blocks[=0|1] --perf-brush-batching[=0|1] --perf-road-merge[=0|1]
/// --perf-multimesh-aabb[=0|1] --perf-far-lod-bias[=0|1] --perf-visibility-fades[=0|1]
/// --perf-near-vegetation-shadows[=0|1] --perf-vegetation-distance[=0|1] --perf-world-lights[=0|1]
/// --perf-nameplate-throttle[=0|1] --perf-overhead-throttle[=0|1]
/// --perf-tree-shadow-distance=metres
/// --perf-focus-cache[=0|1] --perf-hud-throttle[=0|1] --perf-streaming-budget[=0|1]
/// --perf-shadow-range[=0|1] --perf-shadow-filter[=0|1]
/// --online (login screen) or --autologin=CharacterName (launcher account, first available world) --login=host:port
/// pak, db, launcher, host and port can also come from GODOTAGE_* environment variables or godotage.cfg: see <see cref="ClientPaths"/>.
/// </summary>
public partial class WorldViewer : Node3D
{
    [Export] public string GamePakPath { get; set; } = ClientPaths.Pak;
    [Export] public string WorldName { get; set; } = "main_world";
    [Export] public int CenterCellX { get; set; } = 9;
    [Export] public int CenterCellY { get; set; } = 14;
    [Export(PropertyHint.Range, "0,4")] public int CellRadius { get; set; } = 1;
    /// <summary>Cells out to this ring load a cheap far version (coarse terrain, big objects only) for view distance.</summary>
    [Export(PropertyHint.Range, "0,8")] public int FarRadius { get; set; } = 3;
    /// <summary>Heightmap samples per terrain vertex: 1 = every 2 m sample, 2 = 4 m, 4 = 8 m. Must divide 512.</summary>
    [Export(PropertyHint.Range, "1,8")] public int TerrainStep { get; set; } = 2;
    /// <summary>cover.ctc quadtree level for the terrain colour: 4 = 2048 px per cell (0.5 m), 3 = 1024 px, 0 = 128 px.</summary>
    [Export(PropertyHint.Range, "0,4")] public int TerrainTextureLevel { get; set; } = 4;
    /// <summary>Draw everything placed in the cells (false = terrain only).</summary>
    [Export] public bool ShowModels { get; set; } = true;
    [Export] public bool UseNormalMaps { get; set; } = true;
    /// <summary>Opt-in Cry material/environment translation. False preserves the current viewer rendering.</summary>
    [Export] public bool RenderParity { get; set; }
    [Export] public bool RenderMaterialParity { get; set; } = true;
    /// <summary>Screen-space effects are independently switchable because their GPU cost depends on resolution.</summary>
    [Export] public bool RenderSsao { get; set; }
    [Export] public bool RenderSsr { get; set; }
    [Export] public bool RenderGlow { get; set; } = true;
    [Export] public bool RenderGlowBicubic { get; set; }
    [Export(PropertyHint.Range, "0.25,2.0,0.05")] public float VegetationDistanceScale { get; set; } = 1f;
    [Export] public bool VegetationShadows { get; set; } = true;
    [Export(PropertyHint.Range, "50,2000,25")] public float RenderShadowDistance { get; set; } = 250f;
    /// <summary>Near split as a fraction of directional shadow distance; lower values reserve more map for nearby detail.</summary>
    [Export(PropertyHint.Range, "0,1,0.01")] public float RenderShadowSplit1 { get; set; } = 0.1f;
    [Export(PropertyHint.Range, "0,10,0.1")] public float RenderShadowBlur { get; set; } = 1f;
    [Export(PropertyHint.Range, "0,2,0.01")] public float RenderShadowBias { get; set; } = 0.1f;
    [Export(PropertyHint.Range, "0,10,0.1")] public float RenderShadowNormalBias { get; set; } = 2f;
    [Export] public bool RenderWorldLights { get; set; } = true;
    [Export] public bool RenderSkyHdr { get; set; } = true;
    [Export] public bool RenderWaterEnhanced { get; set; } = true;
    [Export] public bool RenderWaterWaves { get; set; } = true;
    [Export] public bool RenderWaterReflections { get; set; } = true;
    [Export] public bool RenderWaterDepthEffects { get; set; } = true;
    [Export] public bool RenderOceanVertexWaves { get; set; } = false;
    [Export] public bool RenderUnderwaterFog { get; set; } = true;
    [Export] public bool StartInWalkMode { get; set; }
    /// <summary>Offline race model of the walking player (models.id: 10 nuian male, 11 nuian female, 14 dwarf male, ...); 0 = capsule.</summary>
    [Export] public long PlayerModelId { get; set; } = 10;
    /// <summary>Client database; empty = <see cref="ClientPaths.Database"/>, resolved once the pak is open (it may be extracted from it).</summary>
    [Export] public string GameDbPath { get; set; } = "";
    /// <summary>Hour of the day (0-24) for the zone's time-of-day lighting.</summary>
    [Export(PropertyHint.Range, "0,24")] public float Hour { get; set; } = 12f;
    /// <summary>Offline clock multiplier. Zero preserves the fixed <see cref="Hour"/> behavior; online uses server speed.</summary>
    [Export(PropertyHint.Range, "0,100,0.1")] public float TimeScale { get; set; }
    /// <summary>Camera vertical field of view, exposed so before/after captures can match the client exactly.</summary>
    [Export(PropertyHint.Range, "10,120,1")] public float RenderFieldOfView { get; set; } = 75f;
    /// <summary>Log into the AAEmu stack instead of just viewing.</summary>
    [Export] public bool Online { get; set; }

    /// <summary>Space Bunny Alpha stands in for the local runner model until character assets are ready (--avatar=native disables).</summary>
    [Export] public bool BunnyAvatar { get; set; } = SpaceBunnyRunner.Available;
    [Export] public string LoginHost { get; set; } = ClientPaths.LoginHost;
    [Export] public int LoginPort { get; set; } = ClientPaths.LoginPort;

    private const int CellSize = WorldStreamer.CellSize;

    private FlyCamera _flyCamera;
    private OrbitCamera _orbitCamera;
    private PlayerController _player;
    private CanvasLayer _hudLayer;
    private Label _hud;
    private LoadingScreenOverlay _loadingScreen;
    private WorldHoverPresentation _hoverPresentation;
    private bool _initialLoadPending;
    private NVector3? _teleportLoadPosition;
    private bool _debugTextOnline;
    private WorldStreamer _world;
    private ModelLibrary _models;
    private DetailLayerBank _detailBank;
    private NearbyCollision _collision;
    private CharacterBuilder _characters;
    private OnlineSession _session;
    private DirectionalLight3D _sun;
    private GpuParticles3D _snow;
    private Godot.Environment _environment;
    private Color _airFogColor = new(0.70f, 0.78f, 0.88f);
    private float _airFogDensity = 0.00005f;
    private Godot.Environment.FogModeEnum _airFogMode = Godot.Environment.FogModeEnum.Exponential;
    private float _airFogDepthBegin = 10f, _airFogDepthEnd = 100f, _airFogDepthCurve = 1f;
    private Color _waterFogColor = new(0.08f, 0.28f, 0.42f);
    private float _waterFogDensity = 0.02f;
    private ProceduralSkyMaterial _sky;
    private ShaderMaterial _dynamicSky;
    private readonly Dictionary<int, EnvironmentReader> _zoneEnvironments = [];
    private int _environmentZone = int.MinValue;
    private float _environmentHour = float.NaN;
    private bool _hourFromCommandLine;
    private int _audioZone = int.MinValue;
    private volatile AudioDirector _pendingAudio;
    private uint _audioSubZone = uint.MaxValue;
    private AAEmu.GodotViewer.Settings.OptionStore? _audioOptions;
    private readonly AAEmu.GodotViewer.Settings.AudioApplier _audioApplier = new();
    private double _nextEnvironmentCheck;
    private double _lastEnvironmentUpdate;
    private readonly ConcurrentQueue<Action> _mainThreadWork = new();
    private long _lastFrameStamp;
    private volatile bool _pakOpen;
    private volatile string _loginStatus = "";
    private bool _walking;
    private bool _placedCamera;
    private string _cameraArg;
    private string _autoLoginCharacter;
    private float _startX, _startY, _startYaw, _startPitch = -18f;
    private string _screenshotPath;
    private double _autoRunSeconds;
    private float _orbitDistance = 7f;
    private bool _autoRunStarted;
    private int _framesSinceLoaded;
    private double _onlineSince = -1;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private bool _perfReportEnabled;
    private double _perfReportIntervalSeconds = 2.0;
    private PerfReport _perfReport;
    private bool _perfTerrainLod = false; // off until its fade transition is fixed (near terrain turned see-through)
    private bool _perfFarBlocks = true;
    private bool _perfBrushBatching = true;
    private bool _perfRoadMerge = true;
    private bool _perfMultiMeshAabb = true;
    private bool _perfFarLodBias = true;
    private bool _perfVisibilityFades = true;
    private bool _perfNearVegetationShadows = true;
    private bool _perfVegetationDistance = true;
    private bool _perfWorldLights = true;
    private bool _perfNameplateThrottle = true;
    private bool _perfOverheadThrottle = true;
    private bool _perfFocusCache = true;
    private bool _perfHudThrottle = true;
    private bool _perfStreamingBudget = true;
    private bool _perfShadowRange = true;
    private bool _perfShadowFilter = true;
    private float _perfVegetationShadowDistance = 350f;
    private double _lastHudUpdate = double.NegativeInfinity;
    private CameraAttributes? _questCinemaPreviousAttributes;
    private bool _questCinemaActive;
    private bool _questCinemaPreviousPlayerInput;
    private float _questCinemaPreviousFov;

    public override void _Ready()
    {
        RenderBudget.SetMainThread();
        ReadCommandLine();
        BuildScene();
        if (_perfReportEnabled)
            _perfReport = new PerfReport(GetViewport(), _perfReportIntervalSeconds);
        if (!Online)
        {
            CreateWorld(CenterCellX, CenterCellY);
            _initialLoadPending = true;
            _player.InputEnabled = false;
            _loadingScreen.BeginInitialLoad();
        }

        Task.Run(async () =>
        {
            if (!PakFiles.Open(GamePakPath))
            {
                _loginStatus = $"Could not open {GamePakPath}";
                return;
            }
            _pakOpen = true;
            if (GameDbPath.Length == 0)
                GameDbPath = ClientPaths.Database;
            Post(() =>
            {
                _loadingScreen.LoadContent(GameDbPath, WorldName);
                _hoverPresentation?.LoadCursors();
                var keyError = new KeyBindings().Load();
                if (keyError != Error.Ok)
                    GD.PrintErr($"Could not load x2 key bindings: {keyError}");
            });
            if (File.Exists(GameDbPath))
            {
                // The sound database import takes seconds: keep it off the world-loading path.
                _ = Task.Run(() =>
                {
                    try
                    {
                        var director = new AudioDirector();
                        director.Configure(GameDbPath, PakFiles.Read);
                        _pendingAudio = director;
                        Post(() => AttachAudio(director));
                    }
                    catch (Exception e)
                    {
                        GD.PrintErr($"Audio setup failed: {e.Message}");
                    }
                });
            }
            if (File.Exists(GameDbPath))
                _characters = new CharacterBuilder(_models, Post, GameDbPath);

            if (!Online)
            {
                _world.Start();
                if (BunnyAvatar && SpaceBunnyRunner.Available)
                    // The placeholder runner is built on the main thread: this runs on the loading task.
                    Post(() =>
                    {
                        if (SpaceBunnyRunner.Build() is { } bunny)
                            _player.SetModel(bunny);
                        else if (PlayerModelId > 0)
                            _characters?.BuildPlayerDefault(PlayerModelId, node => _player.SetModel(node));
                    });
                else if (PlayerModelId > 0)
                    _characters?.BuildPlayerDefault(PlayerModelId, node => _player.SetModel(node));
            }
            else if (_autoLoginCharacter != null)
                await AutoLogin(_autoLoginCharacter);
            else
                Post(ShowLoginScreen);
        });
    }

    public override void _ExitTree()
    {
        if (_hudLayer != null)
            foreach (var ui in _hudLayer.GetChildren().OfType<AAEmu.GodotViewer.Ui.X2.X2UiLayer>())
                ui.QuestDirectingModeChanged -= SetQuestDirectingCamera;
        SetQuestDirectingCamera(false);
        _perfReport?.Finish();
        if (_audioOptions != null)
            _audioOptions.Changed -= OnAudioOptionChanged;
        // A director configured but never attached (quit during startup) must be freed here, or its finalizer
        // touches the engine after shutdown.
        _pendingAudio?.Free();
        _pendingAudio = null;
        // Release first: a loader waiting for a frame budget must be able to finish its cell before we wait for it.
        RenderBudget.Release();
        _world?.StopAndWait(TimeSpan.FromSeconds(3));
        _characters?.Dispose();
    }

    public override void _Process(double delta)
    {
        // Real frame time (delta is capped): paces how fast the loaders create GPU resources.
        var now = Stopwatch.GetTimestamp();
        if (_lastFrameStamp != 0)
            RenderBudget.NewFrame(Stopwatch.GetElapsedTime(_lastFrameStamp, now).TotalMilliseconds);
        _lastFrameStamp = now;

        // Scene changes queued by the worker threads, a few milliseconds' worth per frame, in order.
        var budget = Stopwatch.StartNew();
        var workBudgetMilliseconds = _perfStreamingBudget ? 4.0 : 12.0;
        while (budget.Elapsed.TotalMilliseconds < workBudgetMilliseconds && _mainThreadWork.TryDequeue(out var work))
            work();

        if (_world == null)
        {
            if (_hud.Visible && ShouldUpdateHud())
                _hud.Text = _pakOpen ? _loginStatus : $"Opening {GamePakPath} ...";
            _perfReport?.Tick(settled: false);
            return;
        }

        if (_walking)
        {
            _player.CameraYaw = _orbitCamera.Yaw;
            _collision.Update(_player.Position, _clock.Elapsed.TotalSeconds);
        }
        var focus = _walking ? _player.CryPosition : _world.ToCry(_flyCamera.Position);
        _world.SetFocus(focus.X, focus.Y);
        _world.UpdateVegetationShadows(_world.ToGodot(focus.X, focus.Y, focus.Z));
        if (GetViewport().GetCamera3D() is { } oceanCamera)
        {
            var oceanEye = _world.ToCry(oceanCamera.GlobalPosition);
            _world.UpdateOceanCamera(oceanEye.X, oceanEye.Y, oceanEye.Z);
        }
        UpdateEnvironment(focus);
        UpdateUnderwaterFog();
        UpdateSnowPosition();

        // Once the start cell's heights exist, put the camera (and player) there.
        if (!_placedCamera && _world.HasHeightsAt(_startX, _startY))
        {
            _placedCamera = true;
            PlaceStart();
        }

        if (_world.Status.StartsWith("World loading failed", StringComparison.Ordinal))
        {
            if (_initialLoadPending || _teleportLoadPosition.HasValue)
                _loadingScreen.ShowFailure(_world.Status);
        }
        else if (_initialLoadPending)
        {
            var startHeights = _world.HasHeightsAt(_startX, _startY);
            _loadingScreen.SetProgress(startHeights ? (_world.InitialLoadDone ? 0.96f : 0.62f) : 0.12f);
            if (startHeights && _world.InitialLoadDone && _mainThreadWork.IsEmpty)
            {
                _initialLoadPending = false;
                _loadingScreen.SetProgress(1f);
                _loadingScreen.Finish();
                _player.InputEnabled = true;
            }
        }
        else if (_teleportLoadPosition is { } destination)
        {
            var destinationHeights = _world.HasHeightsAt(destination.X, destination.Y);
            var nearbyReady = destinationHeights && _world.AreNearbyCellsReadyAt(destination.X, destination.Y, 1);
            _loadingScreen.SetProgress(destinationHeights ? (nearbyReady ? 0.96f : 0.38f) : 0.16f);
            if (nearbyReady && _mainThreadWork.IsEmpty)
            {
                _teleportLoadPosition = null;
                _loadingScreen.SetProgress(1f);
                _loadingScreen.Finish();
                _player.InputEnabled = true;
            }
        }

        // the game UI owns the screen online; F3 shows the viewer's debug text
        _hud.Visible = _session == null || _debugTextOnline;
        if (_hud.Visible && ShouldUpdateHud())
            _hud.Text = $"{(_session != null ? _session.Status + "\n" : "")}{_world.Status}\n" +
                        $"Cry x {focus.X:F1}  y {focus.Y:F1}  z {focus.Z:F1}   cell {Mathf.FloorToInt(focus.X / CellSize)},{Mathf.FloorToInt(focus.Y / CellSize)}   " +
                        $"zone {_environmentZone}   {Engine.GetFramesPerSecond()} fps   {(_walking ? "walk" : "fly")} mode (F toggles)\n" +
                        (_walking
                            ? "WASD move, Shift walk, Space jump, hold RMB to turn, hold LMB to orbit, both to run, wheel zoom"
                            : $"Hold RMB to look, WASD move, Q/E down/up, Shift fast, wheel speed ({_flyCamera.Speed:F0} m/s)") +
                        $"\nMovement {_player.MovementState}  pose {_player.MovementPose}  anim {(_player.GetChildren().OfType<CharacterNode>().FirstOrDefault()?.CurrentAnimation ?? "none")}  floor {_player.FloorHeight:F2}  player {_player.CryPosition.Z:F2}  water {(_player.WaterHeight?.ToString("F2") ?? "none")}";

        // Scripted test: once everything is in, run forward for the requested time before the screenshot.
        var settled = !_initialLoadPending && !_teleportLoadPosition.HasValue && _world.InitialLoadDone &&
                      _mainThreadWork.IsEmpty && (_session == null || _clock.Elapsed.TotalSeconds - _onlineSince > 12);
        _perfReport?.Tick(settled);
        if (settled && _autoRunSeconds > 0 && !_autoRunStarted)
        {
            _autoRunStarted = true;
            _player.AutoRunSeconds = _autoRunSeconds;
            GD.Print($"Auto-run {_autoRunSeconds:F1} s from {_player.CryPosition}");
        }
        var autoRunDone = _autoRunSeconds <= 0 || (_autoRunStarted && _player.AutoRunSeconds <= 0);
        if (_screenshotPath != null && settled && autoRunDone && ++_framesSinceLoaded == 60)
        {
            GetViewport().GetTexture().GetImage().SavePng(_screenshotPath);
            GD.Print($"Saved {_screenshotPath} after {_clock.Elapsed.TotalSeconds:F1} s; player at {_player.CryPosition}, {_collision.ActiveBodies} collision bodies; {_session?.Status}");
            GetTree().Quit();
        }
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (e is InputEventKey { Pressed: true, Echo: false, Keycode: Key.F3 } && _session != null) _debugTextOnline = !_debugTextOnline;
        if (e is InputEventKey { Pressed: true, Echo: false, Keycode: Key.F } && _placedCamera && _session == null)
            SetWalking(!_walking, carryPosition: true);
    }

    private bool ShouldUpdateHud()
    {
        var now = _clock.Elapsed.TotalSeconds;
        if (_perfHudThrottle && now - _lastHudUpdate < 0.25)
            return false;
        _lastHudUpdate = now;
        return true;
    }

    private void ReadCommandLine()
    {
        foreach (var arg in OS.GetCmdlineUserArgs())
        {
            var kv = arg.TrimStart('-').Split('=', 2);
            var key = kv[0];
            var value = kv.Length > 1 ? kv[1] : "";
            switch (key)
            {
                case "pak": GamePakPath = ClientPaths.Pak; break; // the same setting, with its folder handling
                case "world": WorldName = value; break;
                case "cell":
                    var c = value.Split(',');
                    CenterCellX = int.Parse(c[0]);
                    CenterCellY = int.Parse(c[1]);
                    break;
                case "radius": CellRadius = int.Parse(value); break;
                case "far": FarRadius = int.Parse(value); break;
                case "step": TerrainStep = int.Parse(value); break;
                case "cam": _cameraArg = value; break;
                case "models": ShowModels = value != "0"; break;
                case "normals": UseNormalMaps = value != "0"; break;
                case "render-parity": RenderParity = value != "0"; break;
                case "material-parity": RenderMaterialParity = value != "0"; break;
                case "ssao": RenderSsao = value != "0"; break;
                case "ssr": RenderSsr = value != "0"; break;
                case "glow": RenderGlow = value != "0"; break;
                case "glow-bicubic": RenderGlowBicubic = value != "0"; break;
                case "vegetation-distance": VegetationDistanceScale = float.Parse(value, System.Globalization.CultureInfo.InvariantCulture); break;
                case "vegetation-shadows": VegetationShadows = value != "0"; break;
                case "shadow-distance": RenderShadowDistance = float.Parse(value, System.Globalization.CultureInfo.InvariantCulture); break;
                case "shadow-split1": RenderShadowSplit1 = float.Parse(value, System.Globalization.CultureInfo.InvariantCulture); break;
                case "shadow-blur": RenderShadowBlur = float.Parse(value, System.Globalization.CultureInfo.InvariantCulture); break;
                case "shadow-bias": RenderShadowBias = float.Parse(value, System.Globalization.CultureInfo.InvariantCulture); break;
                case "shadow-normal-bias": RenderShadowNormalBias = float.Parse(value, System.Globalization.CultureInfo.InvariantCulture); break;
                case "world-lights": RenderWorldLights = value != "0"; break;
                case "sky-hdr": RenderSkyHdr = value != "0"; break;
                case "water-enhanced": RenderWaterEnhanced = value != "0"; break;
                case "water-waves": RenderWaterWaves = value != "0"; break;
                case "water-reflections": RenderWaterReflections = value != "0"; break;
                case "water-depth-effects": RenderWaterDepthEffects = value != "0"; break;
                case "ocean-vertex-waves": RenderOceanVertexWaves = value != "0"; break;
                case "water-underwater-fog": RenderUnderwaterFog = value != "0"; break;
                case "walk": StartInWalkMode = value != "0"; break;
                case "player": PlayerModelId = long.Parse(value); break;
                case "avatar": BunnyAvatar = (value.Length == 0 || value.Equals("bunny", StringComparison.OrdinalIgnoreCase)) && SpaceBunnyRunner.Available; break;
                case "db": GameDbPath = value; break;
                case "hour": Hour = float.Parse(value, System.Globalization.CultureInfo.InvariantCulture); _hourFromCommandLine = true; break;
                case "time-scale":
                    TimeScale = float.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "fov": RenderFieldOfView = float.Parse(value, System.Globalization.CultureInfo.InvariantCulture); break;
                case "orbit": _orbitDistance = float.Parse(value, System.Globalization.CultureInfo.InvariantCulture); break;
                case "screenshot": _screenshotPath = value; break;
                case "autorun": _autoRunSeconds = double.Parse(value, System.Globalization.CultureInfo.InvariantCulture); break;
                case "perf-report":
                    if (value == "0")
                    {
                        _perfReportEnabled = false;
                    }
                    else
                    {
                        _perfReportEnabled = true;
                        if (value.Length > 0)
                            _perfReportIntervalSeconds = double.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
                    }
                    break;
                case "perf-terrain-lod": _perfTerrainLod = value != "0"; break;
                case "perf-far-blocks": _perfFarBlocks = value != "0"; break;
                case "perf-brush-batching": _perfBrushBatching = value != "0"; break;
                case "perf-road-merge": _perfRoadMerge = value != "0"; break;
                case "perf-multimesh-aabb": _perfMultiMeshAabb = value != "0"; break;
                case "perf-far-lod-bias": _perfFarLodBias = value != "0"; break;
                case "perf-visibility-fades": _perfVisibilityFades = value != "0"; break;
                case "perf-near-vegetation-shadows": _perfNearVegetationShadows = value != "0"; break;
                case "perf-vegetation-distance": _perfVegetationDistance = value != "0"; break;
                case "perf-world-lights": _perfWorldLights = value != "0"; break;
                case "perf-nameplate-throttle": _perfNameplateThrottle = value != "0"; break;
                case "perf-overhead-throttle": _perfOverheadThrottle = value != "0"; break;
                case "perf-focus-cache": _perfFocusCache = value != "0"; break;
                case "perf-hud-throttle": _perfHudThrottle = value != "0"; break;
                case "perf-streaming-budget": _perfStreamingBudget = value != "0"; break;
                case "perf-shadow-range": _perfShadowRange = value != "0"; break;
                case "perf-shadow-filter": _perfShadowFilter = value != "0"; break;
                case "perf-tree-shadow-distance":
                    _perfVegetationShadowDistance = float.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "online": Online = value != "0"; break;
                case "autologin":
                    Online = true;
                    _autoLoginCharacter = value;
                    break;
                case "login":
                    var hostPort = value.Split(':');
                    LoginHost = hostPort[0];
                    if (hostPort.Length > 1)
                        LoginPort = int.Parse(hostPort[1]);
                    break;
            }
        }
        if (TerrainStep < 1 || 512 % TerrainStep != 0)
            TerrainStep = 2;
        VegetationDistanceScale = Math.Clamp(VegetationDistanceScale, 0.25f, 2f);
        RenderShadowDistance = Math.Clamp(RenderShadowDistance, 50f, 2000f);
        RenderShadowSplit1 = Math.Clamp(RenderShadowSplit1, 0f, 1f);
        RenderShadowBlur = Math.Clamp(RenderShadowBlur, 0f, 10f);
        RenderShadowBias = Math.Clamp(RenderShadowBias, 0f, 2f);
        RenderShadowNormalBias = Math.Clamp(RenderShadowNormalBias, 0f, 10f);
        RenderFieldOfView = Math.Clamp(RenderFieldOfView, 10f, 120f);
        TimeScale = Math.Clamp(TimeScale, 0f, 100f);
        _perfVegetationShadowDistance = Math.Clamp(_perfVegetationShadowDistance, 50f, 2000f);
        _perfReportIntervalSeconds = Math.Max(0.25, _perfReportIntervalSeconds);

        // Start point: --cam, or 1.1 km south of the centre cell's middle looking north.
        (_startX, _startY) = ((CenterCellX + 0.5f) * CellSize, (CenterCellY + 0.5f) * CellSize - 1100f);
        if (_cameraArg != null)
        {
            var v = _cameraArg.Split(',').Select(s => float.Parse(s, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            (_startX, _startY) = (v[0], v[1]);
            _startYaw = v.Length > 3 ? v[3] : 0f;
            _startPitch = v.Length > 4 ? v[4] : -20f;
        }
    }

    private void BuildScene()
    {
        _sky = new ProceduralSkyMaterial();
        _dynamicSky = DynamicSky.Create();
        _environment = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Sky,
            Sky = new Sky { SkyMaterial = _sky },
            AmbientLightSource = Godot.Environment.AmbientSource.Sky,
            TonemapMode = Godot.Environment.ToneMapper.Filmic,
            FogEnabled = true,
            FogDensity = 0.00005f,
            FogLightColor = new Color(0.70f, 0.78f, 0.88f),
            // Godot fogs the sky fully by default, which hides the skybox behind a flat fog colour.
            FogSkyAffect = 0.15f,
        };
        if (RenderSkyHdr)
        {
            _environment.FogSkyAffect = 0f;
            _environment.ReflectedLightSource = Godot.Environment.ReflectionSource.Sky;
        }
        if (RenderParity)
            RenderParityProfile.ConfigureEnvironment(_environment, RenderSsao, RenderSsr, RenderGlow, RenderGlowBicubic);
        AddChild(new WorldEnvironment { Environment = _environment });
        _sun = new DirectionalLight3D
        {
            ShadowEnabled = true,
            // Two splits keep the near shadow map useful; vegetation casters and the range are separately configurable.
            DirectionalShadowMaxDistance = RenderParity || _perfShadowRange ? RenderShadowDistance : 400f,
            DirectionalShadowMode = DirectionalLight3D.ShadowMode.Parallel2Splits,
            RotationDegrees = new Vector3(-50f, 35f, 0f),
        };
        if (RenderParity)
        {
            _sun.DirectionalShadowSplit1 = RenderShadowSplit1;
            _sun.ShadowBias = RenderShadowBias;
            _sun.ShadowNormalBias = RenderShadowNormalBias;
        }
        _sun.ShadowBlur = _perfShadowFilter ? Math.Min(RenderShadowBlur, 0.5f) : RenderShadowBlur;
        AddChild(_sun);

        _models = new ModelLibrary(UseNormalMaps, RenderParity && RenderMaterialParity);
        _models.CreateDefaults();
        _detailBank = new DetailLayerBank(Post);
        _detailBank.Create();

        _flyCamera = new FlyCamera { Near = 0.3f, Far = 30000f, Fov = RenderFieldOfView };
        AddChild(_flyCamera);
        _player = new PlayerController { Name = "Player", Visible = false };
        AddChild(_player);
        _orbitCamera = new OrbitCamera { Near = 0.05f, Far = 30000f, Fov = RenderFieldOfView, Target = _player, Distance = _orbitDistance };
        AddChild(_orbitCamera);
        // Nothing moves until the start position is known (PlaceStart).
        SetWalking(false, carryPosition: false);

        _hudLayer = new CanvasLayer();
        AddChild(_hudLayer);
        _hud = new Label { Position = new Vector2(12f, 8f) };
        _hud.AddThemeColorOverride("font_outline_color", Colors.Black);
        _hud.AddThemeConstantOverride("outline_size", 5);
        _hudLayer.AddChild(_hud);

        _loadingScreen = new LoadingScreenOverlay { Name = "LoadingScreen" };
        AddChild(_loadingScreen);
    }

    /// <summary>Main thread. Creates the world streamer with its origin at a cell and aims the camera at the start point.</summary>
    private void CreateWorld(int originCellX, int originCellY)
    {
        _world = new WorldStreamer(this, Post, _models, _detailBank, WorldName, originCellX, originCellY, CellRadius, FarRadius)
        {
            TerrainStep = TerrainStep,
            TerrainTextureLevel = TerrainTextureLevel,
            TerrainLodEnabled = _perfTerrainLod,
            ShowObjects = ShowModels,
            VegetationDistanceScale = _perfVegetationDistance ? VegetationDistanceScale : 1f,
            VegetationShadows = !RenderParity || VegetationShadows,
            ShowWorldLights = (!RenderParity || RenderWorldLights) && _perfWorldLights,
            LargerFarBlocks = _perfFarBlocks,
            MergeRoads = _perfRoadMerge,
            MergeBrushes = _perfBrushBatching,
            PreciseMultiMeshBounds = _perfMultiMeshAabb,
            CheapFarLod = _perfFarLodBias,
            VisibilityFades = _perfVisibilityFades,
            LimitVegetationShadows = _perfNearVegetationShadows,
            EnhancedWater = RenderWaterEnhanced,
            WaterWaves = RenderWaterWaves,
            WaterReflections = RenderWaterReflections,
            WaterDepthEffects = RenderWaterDepthEffects,
            OceanVertexWaves = RenderOceanVertexWaves,
            VegetationShadowDistance = _perfVegetationShadowDistance,
            SkipFocusScanWithinCell = _perfFocusCache,
        };
        _collision = new NearbyCollision(this, _world, _models);
        _player.World = _world;
        _orbitCamera.World = _world;
        // Stream the start cell first; the camera is placed properly once its heights are in (PlaceStart).
        _flyCamera.LookFrom(_world.ToGodot(_startX, _startY, 800f), _startYaw, _startPitch);
        _world.SetFocus(_startX, _startY);
    }

    // ------------------------------------------------------------------ online

    private void ShowLoginScreen()
    {
        var backend = new NetLoginBackend();
        // The original client's login, server and character screens, running its own x2ui scripts.
        var ui = new AAEmu.GodotViewer.Ui.X2.X2UiLayer(backend, LoginHost, LoginPort,
            launcherAccount: AAEmu.GodotViewer.Net.LauncherConfig.TryRead()?.UserName ?? "", gameDatabase: GameDbPath);
        ui.EnteredWorld += _ => BeginOnline(backend);
        _hudLayer.AddChild(ui);
        _loginStatus = "";
    }

    /// <summary>Worker thread: logs in with the launcher account, picks the first available world and the named character.</summary>
    private async Task AutoLogin(string characterName)
    {
        try
        {
            var backend = new NetLoginBackend();
            _loginStatus = $"Logging in to {LoginHost}:{LoginPort} ...";
            var result = await backend.LoginAsync(LoginHost, LoginPort, "", "", CancellationToken.None);
            if (!result.Ok)
            {
                _loginStatus = $"Login failed: {result.Error}";
                return;
            }
            var world = (await backend.GetWorldsAsync(CancellationToken.None)).FirstOrDefault(w => w.Online);
            if (world == null)
            {
                _loginStatus = "No world server is available";
                return;
            }
            _loginStatus = $"Connecting to {world.Name} ...";
            var characters = await backend.SelectWorldAsync(world.Id, CancellationToken.None);
            var character = characters.FirstOrDefault(c => c.Name.Equals(characterName, StringComparison.OrdinalIgnoreCase)) ?? characters.FirstOrDefault();
            if (character == null)
            {
                _loginStatus = "The account has no characters";
                return;
            }
            _loginStatus = $"Entering the world as {character.Name} ...";
            await backend.EnterWorldAsync(character.Id, CancellationToken.None);
            Post(() => BeginOnline(backend));
        }
        catch (Exception e)
        {
            _loginStatus = $"Login failed: {e.Message}";
            GD.PrintErr(e.ToString());
        }
    }

    /// <summary>Main thread. The character is in the world: stream the cells around it and start the session.</summary>
    private void BeginOnline(NetLoginBackend backend)
    {
        // The original in-game UI (x2ui stage 6); the login screens already provide it when they were used.
        if (!_hudLayer.GetChildren().OfType<AAEmu.GodotViewer.Ui.X2.X2UiLayer>().Any())
            _hudLayer.AddChild(AAEmu.GodotViewer.Ui.X2.X2UiLayer.ForWorld(backend, GameDbPath));
        var entered = backend.Entered;
        (_startX, _startY) = (entered.Position.X, entered.Position.Y);
        _startYaw = Mathf.RadToDeg(entered.Yaw);
        _startPitch = -15f;
        StartInWalkMode = true;
        CreateWorld((int)MathF.Floor(entered.Position.X / CellSize), (int)MathF.Floor(entered.Position.Y / CellSize));
        _world.Start();
        _initialLoadPending = true;
        _player.InputEnabled = false;
        _loadingScreen.LoadContent(GameDbPath, WorldName);
        _loadingScreen.BeginInitialLoad();

        _session = new OnlineSession
        {
            Name = "Session",
            Client = backend.Client,
            Entered = entered,
            World = _world,
            Player = _player,
            Models = _models,
            Characters = _characters,
            GameDatabasePath = GameDbPath,
            Doodads = File.Exists(GameDbPath) ? new DoodadModelResolver(GameDbPath) : null,
            Post = Post,
            BunnyAvatar = BunnyAvatar,
        };
        AddChild(_session);
        if (_session.Overhead is { } overhead)
            overhead.UpdateInterval = _perfOverheadThrottle ? 1f / 30f : 0f;
        _session.SnowingChanged += SetSnowing;
        _session.Teleported += teleported =>
        {
            _initialLoadPending = false;
            _teleportLoadPosition = teleported.Position;
            _player.InputEnabled = false;
            _loadingScreen.BeginTeleport();
            _orbitCamera.ResetForTeleport();
        };
        if (_session.Targeting is { } targeting)
        {
            targeting.NameplateUpdatesPerFrame = _perfNameplateThrottle ? 4 : 8;
            targeting.Camera = _orbitCamera;
            _hoverPresentation = new WorldHoverPresentation
            {
                Name = "WorldHoverPresentation",
                Targeting = targeting,
                Registry = _session.UnitRegistry,
                Camera = _orbitCamera,
                OrbitCamera = _orbitCamera,
                LoadingScreen = _loadingScreen,
            };
            AddChild(_hoverPresentation);
        }
        // the in-game UI reads combat, targets and cooldowns from the session
        foreach (var ui in _hudLayer.GetChildren().OfType<AAEmu.GodotViewer.Ui.X2.X2UiLayer>())
        {
            ui.QuestDirectingModeChanged -= SetQuestDirectingCamera;
            ui.QuestDirectingModeChanged += SetQuestDirectingCamera;
            ui.AttachSession(_session);
        }
        _onlineSince = _clock.Elapsed.TotalSeconds;
    }

    // ------------------------------------------------------------------ environment

    /// <summary>Main thread. Samples the active zone's authored time-of-day curves against the local or server clock.</summary>
    private void UpdateEnvironment(NVector3 focus)
    {
        var now = _clock.Elapsed.TotalSeconds;
        if (!_pakOpen || now < _nextEnvironmentCheck)
            return;
        var elapsed = _lastEnvironmentUpdate > 0 ? now - _lastEnvironmentUpdate : 0;
        _lastEnvironmentUpdate = now;
        _nextEnvironmentCheck = now + 1.0;

        // an explicit --hour (screenshot / parity runs) wins over the server's time of day
        if (_session?.HasTimeOfDay == true && !_hourFromCommandLine)
            Hour = _session.TimeOfDayHour;
        else if (_session == null && TimeScale > 0f)
            Hour = WrapHour(Hour + (float)elapsed * EnvironmentPacketParserFamily.DefaultGameHourSpeed * TimeScale);

        var zone = _world.ZoneAt(focus.X, focus.Y);
        if (zone < 0)
            return;
        if (zone != _environmentZone || Math.Abs(Hour - _environmentHour) > 0.0001f)
        {
            _environmentZone = zone;
            _environmentHour = Hour;
            if (!_zoneEnvironments.TryGetValue(zone, out var reader))
                _zoneEnvironments[zone] = reader = EnvironmentReader.Load(p => PakFiles.Read($"game/worlds/{WorldName}/{p}"), zone);
            if (reader != null)
                ApplyEnvironment(reader.Sample(Hour, RenderParity));
        }

        // ZoneAt returns the world.xml id, which is zones.zone_key; sub_zones.id is selected by its DB rectangle.
        if (AudioDirector.Shared is { Content: { } content } director)
        {
            var subZone = content.FindSubZone((uint)zone, focus.X, focus.Y);
            if (zone != _audioZone || subZone != _audioSubZone)
            {
                director.OnZoneChanged(zone, (int)subZone);
                _audioZone = zone;
                _audioSubZone = subZone;
            }
        }
    }

    private static float WrapHour(float hour)
    {
        var wrapped = hour % 24f;
        return wrapped < 0f ? wrapped + 24f : wrapped;
    }

    private void SetSnowing(bool enabled)
    {
        _snow ??= CreateSnowEmitter();
        if (_snow != null)
            _snow.Emitting = enabled;
    }

    private void UpdateSnowPosition()
    {
        if (_snow?.Emitting != true || GetViewport().GetCamera3D() is not { } camera)
            return;
        // snow.xml's Environment:Rain node authors Level=14.1, Radius=100 and MaxViewDist=100.
        _snow.GlobalPosition = camera.GlobalPosition + Vector3.Up * 14.1f;
    }

    private GpuParticles3D CreateSnowEmitter()
    {
        const string texturePath = "game/textures/defaults/snow_default_df.dds";
        var bytes = PakFiles.Read(texturePath);
        if (bytes == null)
        {
            GD.PrintErr($"Snow texture missing: {texturePath}");
            return null;
        }
        var image = new Image();
        if (image.LoadDdsFromBuffer(CryDds.PrepareForGodot(bytes)) != Error.Ok || image.IsEmpty())
            return null;
        var texture = ImageTexture.CreateFromImage(image);
        var material = new StandardMaterial3D
        {
            AlbedoTexture = texture,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles,
            VertexColorUseAsAlbedo = true,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        };
        var process = new ParticleProcessMaterial
        {
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Box,
            EmissionBoxExtents = new Vector3(100f, 1f, 100f),
            Direction = Vector3.Down,
            Spread = 12f,
            InitialVelocityMin = 0.5f,
            InitialVelocityMax = 0.5f,
            Gravity = Vector3.Down * 0.5f,
            ScaleMin = 0.08f,
            ScaleMax = 0.18f,
        };
        var emitter = new GpuParticles3D
        {
            Name = "Server Snow",
            Amount = 1200,
            Lifetime = 18f,
            LocalCoords = false,
            ProcessMaterial = process,
            DrawPass1 = new QuadMesh { Size = Vector2.One, Material = material },
            VisibilityAabb = new Aabb(new Vector3(-100f, -30f, -100f), new Vector3(200f, 45f, 200f)),
            Emitting = false,
        };
        AddChild(emitter);
        return emitter;
    }

    private void UpdateUnderwaterFog()
    {
        var camera = GetViewport().GetCamera3D();
        if (camera == null || _world == null)
            return;
        var eye = _world.ToCry(camera.GlobalPosition);
        var submerged = RenderUnderwaterFog &&
            _world.TryWaterSurfaceAt(eye.X, eye.Y, out var surface, eye.Z) && eye.Z < surface - 0.1f;
        if (RenderParity)
        {
            // Air follows the authored distance ramp; underwater keeps the existing exponential attenuation.
            _environment.FogMode = submerged ? Godot.Environment.FogModeEnum.Exponential : _airFogMode;
            if (!submerged)
            {
                _environment.FogDepthBegin = _airFogDepthBegin;
                _environment.FogDepthEnd = _airFogDepthEnd;
                _environment.FogDepthCurve = _airFogDepthCurve;
            }
        }
        _environment.FogLightColor = submerged ? _waterFogColor : _airFogColor;
        _environment.FogDensity = submerged ? _waterFogDensity : _airFogDensity;
    }

    private void AttachAudio(AudioDirector director)
    {
        _pendingAudio = null;
        AddChild(director);
        var options = AAEmu.GodotViewer.Ui.X2.X2UiLayer.Options;
        if (options == null)
        {
            options = new AAEmu.GodotViewer.Settings.OptionStore();
            options.Load();
            AAEmu.GodotViewer.Ui.X2.X2UiLayer.Options = options;
        }
        _audioOptions = options;
        _audioOptions.Changed += OnAudioOptionChanged;
        _audioApplier.Apply(_audioOptions);
    }

    private void OnAudioOptionChanged(object? sender, AAEmu.GodotViewer.Settings.OptionChangedEventArgs e)
    {
        if (e.Id.StartsWith("OIT_S_", StringComparison.OrdinalIgnoreCase) || e.Id.StartsWith("VIEWER_AUDIO_", StringComparison.OrdinalIgnoreCase))
            _audioApplier.Apply(_audioOptions!);
    }

    private void ApplyEnvironment(EnvironmentSample e)
    {
        static Color Normalized(System.Numerics.Vector3 c, float scale)
        {
            var max = Math.Max(c.X, Math.Max(c.Y, Math.Max(c.Z, 1e-4f)));
            return new Color(c.X / max * scale, c.Y / max * scale, c.Z / max * scale);
        }
        static Color Clamped(System.Numerics.Vector3 c) => new(Math.Clamp(c.X, 0, 1), Math.Clamp(c.Y, 0, 1), Math.Clamp(c.Z, 0, 1));

        // The files hold the light ray direction (sun towards ground) in Cry axes.
        var ray = CryAxes.Point(e.SunVector);
        if (ray.LengthSquared() > 1e-6f && Math.Abs(ray.Normalized().Dot(Vector3.Up)) < 0.999f)
            _sun.Basis = Basis.LookingAt(ray.Normalized(), Vector3.Up);
        var sunMax = Math.Max(e.SunColor.X, Math.Max(e.SunColor.Y, e.SunColor.Z));
        _sun.LightColor = Normalized(e.SunColor, 1f);
        _sun.LightEnergy = (float)Math.Clamp(e.SunIntensity * sunMax * 0.16, 0.05, 1.5);

        _airFogColor = Clamped(e.FogColor);
        _airFogDensity = (float)Math.Clamp(e.FogDensity * 0.0004, 0.00001, 0.002);
        _waterFogColor = Clamped(e.OceanFogColor);
        var toSun = ray.LengthSquared() > 1e-6f ? -ray.Normalized() : Vector3.Up;
        var hdrSkyColor = RenderParity ? RenderParityProfile.FromSource(e.SkyColor) : Clamped(e.SkyColor);
        var hdrHorizon = RenderParity ? RenderParityProfile.FromSource(e.FogTopColor) : Clamped(e.FogTopColor);
        if (hdrHorizon.Luminance < 0.001f)
            hdrHorizon = RenderParity ? RenderParityProfile.FromSource(e.FogColor) : Clamped(e.FogColor);
        var atmosphericSunColor = RenderParity
            ? RenderParityProfile.FromSource(e.SkySunColor)
            : Clamped(e.SkySunColor);
        Color? skyReflection = RenderSkyHdr
            ? DynamicSky.ReflectionColor(e, toSun, atmosphericSunColor, hdrSkyColor, hdrHorizon)
            : null;
        WaterMaterialFactory.UpdateOceanEnvironment(_waterFogColor, Clamped(e.OceanScatteringColor), ray,
            _sun.LightColor, (float)e.SunSpecularMultiplier, skyReflection);
        _waterFogDensity = (float)Math.Clamp(e.OceanFogDensityUnderWater, 0.001, 0.08);
        _environment.FogLightColor = _airFogColor;
        _environment.FogDensity = _airFogDensity;
        _environment.AmbientLightColor = Clamped(e.AmbientColor);
        _environment.AmbientLightSkyContribution = 0.5f;
        // Carry the zone's authored film white point into the renderer's matching white-reference control.
        _environment.TonemapWhite = (float)Math.Clamp(e.FilmCurveWhitepoint, 1.0, 16.0);
        _sky.SkyTopColor = Normalized(e.SkyColor, 0.55f);
        _sky.SkyHorizonColor = Normalized(e.SkyColor, 0.35f).Lerp(Normalized(e.FogColor, 0.8f), 0.5f);
        var night = (float)Math.Clamp(e.StarIntensity / 3.0, 0.0, 1.0);
        if (night > 0f)
        {
            _sky.SkyTopColor = _sky.SkyTopColor.Lerp(Clamped(e.NightZenithColor), night);
            _sky.SkyHorizonColor = _sky.SkyHorizonColor.Lerp(Clamped(e.NightHorizonColor), night);
        }
        _sky.GroundHorizonColor = _sky.SkyHorizonColor;
        _sky.GroundBottomColor = Clamped(e.AmbientColor) * 0.4f;
        if (RenderParity)
            RenderParityProfile.ApplyEnvironmentSample(_environment, _sun, _sky, e);
        _airFogColor = _environment.FogLightColor;
        _airFogDensity = _environment.FogDensity;
        _airFogMode = _environment.FogMode;
        _airFogDepthBegin = _environment.FogDepthBegin;
        _airFogDepthEnd = _environment.FogDepthEnd;
        _airFogDepthCurve = _environment.FogDepthCurve;

        if (RenderSkyHdr)
        {
            // e.SunVector is the light-ray direction; the sky shader needs the direction from the viewer to the sun.
            DynamicSky.Apply(_dynamicSky, e, toSun, hdrSkyColor, hdrHorizon,
                Clamped(e.AmbientColor) * 0.4f, atmosphericSunColor);
            _environment.Sky.SkyMaterial = _dynamicSky;
            _environment.AmbientLightColor = Colors.White;
            _environment.AmbientLightSkyContribution = 1f;
            return;
        }

        // The zone's own skybox when it has one; darker at night with the sun, fading to the fog colour at the horizon.
        if (ZoneSky.Load(_models, e.SkyboxMaterial) is { } zoneSky)
        {
            zoneSky.SetShaderParameter("angle", Mathf.DegToRad((float)e.SkyboxAngle));
            var authoredSkyMultiplier = ZoneSky.ColorMultiplier(_models, e.SkyboxMaterial);
            zoneSky.SetShaderParameter("energy", RenderParity
                ? RenderParityProfile.SkyboxEnergy(e) * authoredSkyMultiplier
                : (float)Math.Clamp(e.SunIntensity / 6.0, 0.2, 1.1) * authoredSkyMultiplier);
            var fogColor = RenderParity ? RenderParityProfile.FromSource(e.FogColor) : Clamped(e.FogColor);
            // Static Cry Sky materials use their authored cube map. TOD Sky color is an ambient tint, so use
            // the profile's blue fog gradient only as the clear-sky fill behind the cube's cloud alpha.
            var skyColor = RenderParity ? Normalized(e.FogColor, 0.55f) : Normalized(e.SkyColor, 0.6f);
            zoneSky.SetShaderParameter("clear_sky_color", skyColor);
            var skySunColor = RenderParity ? RenderParityProfile.FromSource(e.SunColor) : Normalized(e.SunColor, 1f);
            zoneSky.SetShaderParameter("cloud_sun_color", skySunColor);
            zoneSky.SetShaderParameter("sun_color_multiplier", ZoneSky.SunColorMultiplier(_models, e.SkyboxMaterial));
            var fogTopColor = RenderParity ? RenderParityProfile.FromSource(e.FogTopColor) : fogColor;
            zoneSky.SetShaderParameter("horizon_color", fogColor.Lerp(fogTopColor, 0.35f));
            zoneSky.SetShaderParameter("night_horizon_color", Clamped(e.NightHorizonColor));
            zoneSky.SetShaderParameter("night_zenith_color", Clamped(e.NightZenithColor));
            zoneSky.SetShaderParameter("night_zenith_shift", (float)e.NightZenithShift);
            zoneSky.SetShaderParameter("star_intensity", (float)e.StarIntensity);
            _environment.Sky.SkyMaterial = zoneSky;
        }
        else
            _environment.Sky.SkyMaterial = _sky;
        GD.Print($"Environment: zone {_environmentZone} at {e.Hour:F1} h: sun {e.SunColor} x{e.SunIntensity:F2}, fog {e.FogColor} density {e.FogDensity:F3}");
    }

    // ------------------------------------------------------------------ cameras

    /// <summary>Applies the native quest-directing close-up requested by the original UI scripts.</summary>
    private void SetQuestDirectingCamera(bool enabled)
    {
        if (!enabled)
        {
            if (!_questCinemaActive)
                return;
            _questCinemaActive = false;
            _orbitCamera.EndQuestCinema();
            _orbitCamera.Attributes = _questCinemaPreviousAttributes;
            _orbitCamera.Fov = _questCinemaPreviousFov;
            _questCinemaPreviousAttributes = null;
            _player.InputEnabled = _questCinemaPreviousPlayerInput;
            return;
        }

        if (_questCinemaActive || !_walking || _session?.Targeting?.Target is not { Kind: TargetKind.Npc } target ||
            !GodotObject.IsInstanceValid(target.Node) || !target.Node.IsInsideTree())
            return;

        _questCinemaActive = true;
        _questCinemaPreviousPlayerInput = _player.InputEnabled;
        _player.InputEnabled = false;
        _questCinemaPreviousAttributes = _orbitCamera.Attributes;
        _questCinemaPreviousFov = _orbitCamera.Fov;
        var camera = target is UnitRegistry.TargetableUnit liveTarget
            ? _session.ContentData?.GetQuestCamera(liveTarget.TemplateId)
            : null;
        if (camera is { FieldOfView: > 0f })
            _orbitCamera.Fov = camera.FieldOfView;
        _orbitCamera.Attributes = new CameraAttributesPractical
        {
            DofBlurFarEnabled = camera?.DepthOfField ?? true,
            DofBlurFarDistance = camera is null
                ? Mathf.Clamp(target.Height * 1.55f, 2.5f, 5f)
                : new Vector3(camera.CameraOffsetX, camera.CameraOffsetY, camera.CameraOffsetZ).Length() + 0.25f,
            DofBlurFarTransition = 2.5f,
            DofBlurAmount = camera is null ? 0.22f : Mathf.Clamp(camera.BokehSize / 20f + camera.Intensity, 0.1f, 0.5f),
        };
        if (camera?.HidePlayer == true)
            _player.SetThirdPersonModelVisible(false);
        _orbitCamera.BeginQuestCinema(target.Node, target.Height, _player, camera);
    }

    /// <summary>Main thread. Puts the fly camera at --cam (or the online spawn) and, offline, the player on the ground there.</summary>
    private void PlaceStart()
    {
        var (x, y, yaw, pitch) = (_startX, _startY, _startYaw, _startPitch);
        float z;
        if (_cameraArg != null && _session == null)
        {
            var zArg = _cameraArg.Split(',')[2];
            var zValue = float.Parse(zArg, System.Globalization.CultureInfo.InvariantCulture);
            z = zArg.StartsWith('+') ? _world.TerrainHeightAt(x, y) + zValue : zValue;
        }
        else
            z = _world.TerrainHeightAt(x, y) + (_session != null ? 20f : 350f);
        _flyCamera.LookFrom(_world.ToGodot(x, y, z), yaw, pitch);
        if (_session == null) // online, the server placed the player already
            _player.Teleport(new NVector3(x, y, _world.TerrainHeightAt(x, y)), Mathf.DegToRad(yaw));
        _orbitCamera.Yaw = Mathf.DegToRad(yaw);
        SetWalking(StartInWalkMode, carryPosition: false);
    }

    /// <param name="carryPosition">Switching by key: land the player under the fly camera, or put the fly camera where the orbit camera was.</param>
    private void SetWalking(bool walking, bool carryPosition)
    {
        _walking = walking;
        if (!walking)
        {
            _orbitCamera.CancelMouseCapture();
            _player.SetThirdPersonModelVisible(true);
        }
        _player.Visible = walking;
        _player.ProcessMode = walking ? ProcessModeEnum.Inherit : ProcessModeEnum.Disabled;
        _flyCamera.ProcessMode = walking ? ProcessModeEnum.Disabled : ProcessModeEnum.Inherit;
        _orbitCamera.ProcessMode = walking ? ProcessModeEnum.Inherit : ProcessModeEnum.Disabled;
        if (walking)
        {
            if (carryPosition)
            {
                var cry = _world.ToCry(_flyCamera.Position);
                _player.Teleport(new NVector3(cry.X, cry.Y, _world.TerrainHeightAt(cry.X, cry.Y)), _player.Heading);
            }
            _orbitCamera.MakeCurrent();
        }
        else
        {
            if (carryPosition)
                _flyCamera.Position = _orbitCamera.GlobalPosition;
            _flyCamera.MakeCurrent();
        }
    }

    /// <summary>Queues work that touches the scene tree; runs on the main thread within a per-frame time budget, in order.</summary>
    private void Post(Action work) => _mainThreadWork.Enqueue(work);
}
