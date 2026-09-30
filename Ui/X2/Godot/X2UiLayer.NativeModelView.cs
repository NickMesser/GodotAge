#nullable enable
using System.Collections.Concurrent;
using System.Globalization;
using AAEmu.GodotViewer.Data;
using AAEmu.GodotViewer.Net;
using Microsoft.Data.Sqlite;
using Godot;

namespace AAEmu.GodotViewer.Ui.X2;

// Character model views. CharacterBuilder does all pak/database resolution; this class only owns the isolated
// preview worlds and translates the native widget state into camera and character build requests.
public partial class X2UiLayer
{
    private sealed class ModelViewScene
    {
        public required SubViewport Viewport;
        public required Node3D Pivot;
        public required Camera3D Camera;
        public CharacterNode? Character;
        public long ModelVersion = -1;
        public long StateVersion = -1;
        public long Generation;
        public string? Animation;
        public int LastFrame;
        public long AnimationVersion = -1;
        public int PresentationStage = -1;
        public int AbilityId;
        public long SkillActionSerial;
        public bool CutsceneActive;
        public double CutsceneStartedAt;
        /// <summary>The cutscene's cinema (a parsed 04_ability_* sequence) and the character whose class clip drives it.</summary>
        public X2UiLayer.LoginStageScene.CinemaDataHandle? Cinema;
        public CharacterNode? CutsceneCharacter;
        public double CutsceneClipAt;
        public bool CutsceneNoClip;
        public SkillPreviewRun? PreviewRun;
        /// <summary>The preview skills are due (a pick was just made, not merely returning to the page): survives the outfit rebuild a pick causes.</summary>
        public bool PreviewWanted;
        public ClipStream? IdleStream;
    }

    private sealed record PreviewBuild(long ModelId, Dictionary<int, long> Equipment, UnitAppearance? Appearance,
        bool UseUnitEquipment = false);

    private readonly Dictionary<ModelViewWidget, ModelViewScene> _modelViewScenes = [];
    private readonly ConcurrentQueue<Action> _modelViewPosts = new();
    private ModelLibrary? _modelViewModels;
    private CharacterBuilder? _modelViewCharacters;
    private GameData? _previewGameData;
    private LoginStageAnimationCatalog? _loginAnimations;
    private ModelViewScene? _loginModelView;
    private bool _loginSkipRequested;
    private LoginStageScene? _loginStage;
    private bool _loginStageAttempted;
    private int _modelViewFrame;
    private int _loginPreviewSignature;
    private string? _savedBackdrop;

    private void InitializeModelViews()
    {
        if (_modelViewCharacters != null || !File.Exists(_gameDatabase)) return;
        _modelViewModels = new ModelLibrary(true);
        _modelViewModels.CreateDefaults();
        _modelViewCharacters = new CharacterBuilder(_modelViewModels, action => _modelViewPosts.Enqueue(action), _gameDatabase);
        _previewGameData = new GameData(_gameDatabase);
        _loginAnimations = new LoginStageAnimationCatalog(_gameDatabase);
    }

    private void UpdateModelViews()
    {
        InitializeModelViews();
        while (_modelViewPosts.TryDequeue(out var action))
        {
            try { action(); }
            catch (Exception e) { Emit($"[x2] model view: {e.Message}"); }
        }
        _modelViewFrame++;
        foreach (var scene in _modelViewScenes.Values)
            scene.Viewport.RenderTargetUpdateMode = scene.LastFrame >= _modelViewFrame - 1
                ? SubViewport.UpdateMode.Always : SubViewport.UpdateMode.Disabled;

        var preview = EnginePreview();
        var showLogin = _modelViewCharacters != null && IsPreviewStage();
        if (showLogin)
        {
            _savedBackdrop ??= Backdrop;
            Backdrop = "";
            _loginModelView ??= CreateModelViewScene(new Vector2I(1280, 768), false);
            if (!_loginStageAttempted)
            {
                _loginStageAttempted = true;
                _loginStage = LoginStageScene.TryCreate(_loginModelView, _modelViewModels!,
                    action => _modelViewPosts.Enqueue(action), Emit);
            }
            ResizeLoginViewport(_loginModelView);
            var signature = preview == null ? 0 : PreviewSignature(preview);
            if (signature != _loginPreviewSignature)
            {
                _loginPreviewSignature = signature;
                if (preview != null) RequestBuild(_loginModelView, preview);
                else
                {
                    _loginModelView.Generation++;
                    _loginModelView.Character?.QueueFree();
                    _loginModelView.Character = null;
                }
            }
            // Face sliders and presets only change the modifier: it is left out of the signature and shown on the built face
            // in place (a rebuild per slider step would lag and restart the idle animation).
            if (preview?.Appearance is { } look && _loginModelView.Character?.FaceMorph is { } faceMorph &&
                faceMorph.Show(look.Modifier) && System.Environment.GetEnvironmentVariable("X2_TRACE_PREVIEW") == "1")
                Emit($"[x2] preview face modifier {Convert.ToHexString(look.Modifier)}");
            if (_loginStage != null)
            {
                _loginStage.Update(_session.Engine);
                _loginStage.TickFx(Time.GetTicksMsec() / 1000.0);
            }
            else
                ConfigureLoginCamera(_loginModelView);
            ApplyLoginCharacterAnimation(_loginModelView, _session.Engine);
            _loginModelView.Viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Always;
        }
        else
        {
            if (_savedBackdrop != null) { Backdrop = _savedBackdrop; _savedBackdrop = null; }
            if (_loginModelView != null) _loginModelView.Viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;
            if (_session.Engine.Stage == Scripting.X2Engine.StageWorld && _loginModelView != null)
            {
                _loginStage?.Dispose();
                _loginStage = null;
                _loginModelView.Generation++;
                _loginModelView.Viewport.QueueFree();
                _loginModelView = null;
                _loginStageAttempted = false;
                _loginPreviewSignature = 0;
            }
        }

        foreach (var stale in _modelViewScenes.Where(p => p.Value.LastFrame < _modelViewFrame - 2).Select(p => p.Key).ToArray())
        {
            var scene = _modelViewScenes[stale];
            scene.Generation++;
            scene.Viewport.QueueFree();
            _modelViewScenes.Remove(stale);
        }
    }

    private void DisposeModelViews()
    {
        _loginStage?.Dispose();
        _loginStage = null;
        _modelViewCharacters?.Dispose();
        _modelViewCharacters = null;
        _previewGameData?.Dispose();
        _previewGameData = null;
        _loginAnimations?.Dispose();
        _loginAnimations = null;
        foreach (var scene in _modelViewScenes.Values) scene.Viewport.QueueFree();
        _modelViewScenes.Clear();
        _loginModelView?.Viewport.QueueFree();
        _loginModelView = null;
    }

    private void DrawLoginModelView()
    {
        if (_loginModelView == null || !IsPreviewStage()) return;
        var texture = _loginModelView.Viewport.GetTexture();
        if (texture != null)
            DrawTextureRect(texture, new Rect2(Vector2.Zero, Size / Math.Max(0.1f, UiScale)), false);
    }

    private void DrawNativeModelView(ModelViewWidget widget, float alpha)
    {
        var rect = widget.ScreenRect;
        if (rect.Width <= 1 || rect.Height <= 1 || widget.ModelRef == null && !widget.BeautyShop) return;
        InitializeModelViews();
        if (_modelViewCharacters == null) return;

        var size = PreviewSize(widget, rect);
        if (!_modelViewScenes.TryGetValue(widget, out var scene))
        {
            scene = CreateModelViewScene(size, true);
            _modelViewScenes[widget] = scene;
        }
        scene.LastFrame = _modelViewFrame;
        if (scene.Viewport.Size != size) scene.Viewport.Size = size;
        scene.Viewport.RenderTargetUpdateMode = widget.Frozen ? SubViewport.UpdateMode.Once : SubViewport.UpdateMode.Always;

        if (scene.ModelVersion != widget.ModelVersion)
        {
            scene.ModelVersion = widget.ModelVersion;
            var build = WidgetPreview(widget);
            if (build != null) RequestBuild(scene, build);
        }
        ConfigureWidgetScene(scene, widget);

        if (scene.Character == null) return;
        var texture = scene.Viewport.GetTexture();
        if (texture == null) return;
        var src = widget.ViewCoords.Width > 0 && widget.ViewCoords.Height > 0
            ? widget.ViewCoords : new UiRect(0, 0, size.X, size.Y);
        DrawRegion(texture, rect, src, new Color(1, 1, 1, alpha), false, false);
    }

    private ModelViewScene CreateModelViewScene(Vector2I size, bool transparent)
    {
        var viewport = new SubViewport
        {
            Name = "X2ModelView",
            Size = size,
            OwnWorld3D = true,
            TransparentBg = transparent,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
        };
        AddChild(viewport);
        var world = new WorldEnvironment
        {
            Environment = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color,
                BackgroundColor = transparent ? new Color(0, 0, 0, 0) : new Color(0.035f, 0.055f, 0.085f),
                AmbientLightSource = Godot.Environment.AmbientSource.Color,
                AmbientLightColor = new Color(0.55f, 0.62f, 0.72f),
                AmbientLightEnergy = 0.8f,
            },
        };
        viewport.AddChild(world);
        viewport.AddChild(new DirectionalLight3D
        {
            RotationDegrees = new Vector3(-35, -35, 0),
            LightColor = new Color(1f, 0.91f, 0.82f),
            LightEnergy = 1.35f,
            ShadowEnabled = true,
        });
        var fill = new DirectionalLight3D
        {
            RotationDegrees = new Vector3(-15, 145, 0),
            LightColor = new Color(0.55f, 0.68f, 1f),
            LightEnergy = 0.55f,
            ShadowEnabled = false,
        };
        viewport.AddChild(fill);
        var pivot = new Node3D { Name = "CharacterPivot" };
        viewport.AddChild(pivot);
        var camera = new Camera3D { Name = "Camera", Current = true, Fov = 30 };
        viewport.AddChild(camera);
        return new ModelViewScene { Viewport = viewport, Pivot = pivot, Camera = camera };
    }

    private static Vector2I PreviewSize(ModelViewWidget widget, UiRect rect)
    {
        var w = widget.TextureSize.Width > 0 ? widget.TextureSize.Width : rect.Width;
        var h = widget.TextureSize.Height > 0 ? widget.TextureSize.Height : rect.Height;
        return new Vector2I(Math.Clamp((int)MathF.Ceiling(w), 32, 1024), Math.Clamp((int)MathF.Ceiling(h), 32, 1024));
    }

    private void ResizeLoginViewport(ModelViewScene scene)
    {
        var size = new Vector2I(Math.Max(64, (int)Size.X), Math.Max(64, (int)Size.Y));
        if (scene.Viewport.Size != size) scene.Viewport.Size = size;
    }

    private static void ConfigureLoginCamera(ModelViewScene scene)
    {
        scene.Pivot.Position = new Vector3(1.15f, 0, 0);
        scene.Pivot.Rotation = Vector3.Zero;
        scene.Camera.Fov = 27;
        scene.Camera.Position = new Vector3(0, 1.05f, -4.1f);
        scene.Camera.LookAt(new Vector3(0, 1.05f, 0), Vector3.Up);
    }

    private static double Number(object? value, double fallback = 0)
    {
        try { return value == null ? fallback : Convert.ToDouble(value, CultureInfo.InvariantCulture); }
        catch { return fallback; }
    }

    private void ConfigureWidgetScene(ModelViewScene scene, ModelViewWidget widget)
    {
        var center = 0d;
        var cameraZoom = 0d;
        var height = 0d;
        if (widget.Appearance.TryGetValue("camera", out var cameraArgs))
        {
            center = cameraArgs.Length > 0 ? Number(cameraArgs[0]) : 0;
            cameraZoom = cameraArgs.Length > 1 ? Number(cameraArgs[1]) : 0;
            height = cameraArgs.Length > 2 ? Number(cameraArgs[2]) : 0;
        }
        else if (widget.Appearance.TryGetValue("camera_to_model", out var modelArgs))
        {
            center = modelArgs.Length > 0 ? Number(modelArgs[0]) : 0;
            height = modelArgs.Length > 1 ? Number(modelArgs[1]) : 0;
        }
        scene.Pivot.Position = new Vector3((float)widget.ModelPosX, (float)widget.ModelPosZ, 0);
        scene.Pivot.Rotation = new Vector3(Mathf.DegToRad((float)widget.RotationX),
            Mathf.DegToRad((float)widget.Rotation), 0);
        scene.Camera.Fov = (float)widget.Fov;
        var targetY = 1.05f + (float)(center + height);
        var distance = Math.Clamp(3.6f + (float)cameraZoom + (float)widget.Zoom * 0.25f, 1.25f, 8f);
        scene.Camera.Position = new Vector3(0, targetY, -distance);
        scene.Camera.LookAt(new Vector3(0, targetY, 0), Vector3.Up);
        if (scene.Character != null)
            scene.Character.ProcessMode = widget.Frozen ? ProcessModeEnum.Disabled : ProcessModeEnum.Inherit;
        if (scene.Character != null && scene.StateVersion != widget.StateVersion)
        {
            scene.StateVersion = widget.StateVersion;
            if (scene.Animation != widget.Animation)
            {
                scene.Character.StopAction();
                if (!string.IsNullOrWhiteSpace(widget.Animation))
                    scene.Character.PlayAction(widget.Animation, 0.15f, widget.AnimationLoop);
                scene.Animation = widget.Animation;
            }
        }
    }

    private PreviewBuild? WidgetPreview(ModelViewWidget widget)
    {
        PreviewBuild? build = null;
        var text = Convert.ToString(widget.ModelRef, CultureInfo.InvariantCulture) ?? "";
        if (text.Equals("player", StringComparison.OrdinalIgnoreCase) && LiveSession?.Entered.Self is { } self)
            build = FromSnapshot(self);
        else if (text.Equals("target", StringComparison.OrdinalIgnoreCase) && Bridge?.Get(Bridge.TargetId) is { ModelId: > 0 } target)
            build = new PreviewBuild(target.ModelId, [], null);
        else if (uint.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unitId)
                 && LiveSession?.Entered.Self is { } own && own.UnitId == unitId)
            build = FromSnapshot(own);
        else if (uint.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out unitId)
                 && Bridge?.Get(unitId) is { ModelId: > 0 } unit)
            build = new PreviewBuild(unit.ModelId, [], null);
        else if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var modelId) && modelId > 0)
            build = new PreviewBuild(modelId, [], null);
        else if (widget.Race != null)
            build = new PreviewBuild(PlayableModel(widget.Race, widget.Gender), [], null);
        else if (LiveSession?.Entered.Self is { } fallback)
            build = FromSnapshot(fallback);

        if (build == null || build.ModelId <= 0) return null;
        var equipment = new Dictionary<int, long>(build.Equipment);
        if (!widget.CosplayEquipped) equipment.Remove(CharacterAssetResolver.SlotCosplay);
        foreach (var key in new[] { "equipment", "equip_item", "equip_costume", "show_item", "dyeing_item" })
        {
            if (!widget.CosplayEquipped && key == "equip_costume") continue;
            if (!widget.Appearance.TryGetValue(key, out var args) || args.Length == 0) continue;
            var itemId = (long)Number(args[0]);
            if (itemId <= 0) continue;
            var slot = key == "equip_costume" ? CharacterAssetResolver.SlotCosplay : ItemSlot(itemId);
            if (slot >= 0) equipment[slot] = itemId;
        }
        return build with { Equipment = equipment };
    }

    private bool IsPreviewStage() => _session.Engine.Stage is Scripting.X2Engine.StageSelect
        or Scripting.X2Engine.StageCreate or Scripting.X2Engine.StageCustomize or Scripting.X2Engine.StageAbility;

    private PreviewBuild? EnginePreview()
    {
        var preview = _session.Engine.CharacterPreview;
        if (preview == null) return null;
        var model = preview.ModelId > 0 ? preview.ModelId : PlayableModel(preview.Race, preview.Gender);
        if (model <= 0) return null;
        // Lobby equipment and customization have the same wire shape as a world unit. Creation has only
        // editor changes and needs the race's preview cloth pack and body defaults filled in first.
        // A lobby character with no appearance and no equipment (the mock backend's) has no unit parts to draw: it is shown like a
        // creation preview, the race's default body.
        var bare = !preview.IsCreation && _session.Engine.ShownCharacter?.Appearance == null && preview.Equipment.Count == 0;
        var appearance = preview.IsCreation || bare || _session.Engine.ShownCharacter?.Appearance != null
            ? Appearance(preview.Appearance) : null;
        var equipment = new Dictionary<int, long>(preview.Equipment);
        if (preview.IsCreation && _session.Engine.Stage == Scripting.X2Engine.StageAbility && _loginAnimations != null)
            foreach (var pair in _loginAnimations.AbilityEquipment(model, _session.Engine.LoginAnimationAbility))
                equipment[pair.Key] = pair.Value;
        return new PreviewBuild(model, equipment, appearance,
            UseUnitEquipment: !preview.IsCreation && !bare);
    }

    private static int PreviewSignature(PreviewBuild preview)
    {
        var hash = new HashCode();
        hash.Add(preview.ModelId);
        hash.Add(preview.UseUnitEquipment);
        foreach (var pair in preview.Equipment.OrderBy(p => p.Key)) { hash.Add(pair.Key); hash.Add(pair.Value); }
        if (preview.Appearance is { } a)
        {
            hash.Add(a.HairColorId); hash.Add(a.SkinColorId); hash.Add(a.FaceNormalMapId); hash.Add(a.BodyNormalMapId);
            hash.Add(a.FaceNormalMapWeight); hash.Add(a.BodyNormalMapWeight); hash.Add(a.DefaultHairColor);
            hash.Add(a.TwoToneHairColor); hash.Add(a.TwoToneFirstWidth); hash.Add(a.TwoToneSecondWidth);
            hash.Add(a.LipColor); hash.Add(a.LeftPupilColor); hash.Add(a.RightPupilColor); hash.Add(a.EyebrowColor); hash.Add(a.DecoColor);
            foreach (var value in a.DecalIds) hash.Add(value);
            foreach (var value in a.DecalWeights) hash.Add(value);
            // Creation previews show a.Modifier in place (CharacterNode.FaceMorph, UpdateModelViews); a build takes the current one.
            if (preview.UseUnitEquipment)
                foreach (var value in a.Modifier) hash.Add(value);
            hash.Add(a.HairItemId); hash.Add(a.FaceItemId); hash.Add(a.BodyItemId); hash.Add(a.HornItemId); hash.Add(a.TailItemId);
        }
        return hash.ToHashCode();
    }

    private void RequestBuild(ModelViewScene scene, PreviewBuild preview)
    {
        if (_modelViewCharacters == null || preview.ModelId <= 0) return;
        var generation = ++scene.Generation;
        void Ready(CharacterNode character)
        {
            if (scene.Generation != generation || !GodotObject.IsInstanceValid(scene.Viewport))
            {
                character.QueueFree();
                return;
            }
            var old = scene.Character;
            if (character.AnimationGraph == null) character.Visible = true;
            else character.HideUntilPresentationReady();
            scene.Pivot.AddChild(character);
            scene.Character = character;
            scene.Animation = null;
            if (old != null && GodotObject.IsInstanceValid(old)) old.QueueFree();
            scene.AnimationVersion = -1;
        }
        if (System.Environment.GetEnvironmentVariable("X2_TRACE_PREVIEW") == "1")
        {
            var a = preview.Appearance;
            Emit($"[x2] preview model {preview.ModelId} items [{string.Join(' ', preview.Equipment.Where(p => p.Key >= 15).OrderBy(p => p.Key).Select(p => $"{p.Key}:{p.Value}"))}]" +
                 (a == null ? " no appearance" : $" skin {a.SkinColorId} hairColor {a.HairColorId} faceNormal {a.FaceNormalMapId} decals [{string.Join(' ', a.DecalIds)}] " +
                  $"items face {a.FaceItemId} hair {a.HairItemId} body {a.BodyItemId} modifier {a.Modifier.Length}b"));
        }
        if (preview.UseUnitEquipment)
            _modelViewCharacters.BuildUnit(preview.ModelId, preview.Equipment, preview.Appearance!, Ready);
        else
            _modelViewCharacters.BuildPlayerPreview(preview.ModelId, preview.Equipment, preview.Appearance!, Ready);
    }

    private void ApplyLoginCharacterAnimation(ModelViewScene scene, Scripting.X2Engine engine)
    {
        if (scene.Character is not { } character) return;
        character.SetFrozen(engine.PreviewFrozen);
        UpdateSkillActionCutscene(scene, engine, character);
        if (scene.PresentationStage != engine.Stage || scene.AnimationVersion != engine.LoginAnimationVersion)
        {
            scene.PresentationStage = engine.Stage;
            scene.AnimationVersion = engine.LoginAnimationVersion;
            if (engine.Stage == Scripting.X2Engine.StageSelect || engine.LoginAnimation == "select")
            {
                character.EnableStageFidgets("fist_ac_stage_idle", "fist_ac_stage_rand_1");
                character.PlayPresentationLoop("fist_ac_stage_idle", "fist_ac_stage_idle",
                    started: character.RevealWhenPresentationReady);
                scene.AbilityId = 0;
            }
            else if (engine.Stage == Scripting.X2Engine.StageCreate || engine.LoginAnimation == "race")
            {
                character.DisableStageFidgets();
                character.PlayPresentationLoop("loginstage_tribe_select", "fist_ac_stage_idle",
                    started: character.RevealWhenPresentationReady);
                scene.AbilityId = 0;
            }
            else if (engine.Stage == Scripting.X2Engine.StageAbility && _loginAnimations?.Get(engine.LoginAnimationAbility) is { } ability)
            {
                character.DisableStageFidgets();
                var abilityId = engine.LoginAnimationAbility;
                var trace = System.Environment.GetEnvironmentVariable("X2_TRACE_PREVIEW") == "1";
                AnimEventDatabase.Prepare(character.AnimationListPath);
                scene.AbilityId = abilityId;
                // The class idle is the base pose the skills return to.
                character.SetCombatIdle(ability.IdleSignal);
                if (scene.CutsceneActive)
                {
                    // The class cutscene: the class clip alone, seen through the cinema camera; when it ends the page comes back.
                    if (trace) Emit($"[x2] class cutscene ability {abilityId}: clip {ability.StartSignal}");
                    character.PlayPresentationSequence([ability.StartSignal], loopLast: false,
                        started: () =>
                        {
                            character.RevealWhenPresentationReady();
                            if (scene.Character != character || !scene.CutsceneActive) return;
                            scene.CutsceneCharacter = character;
                            scene.CutsceneClipAt = Time.GetTicksMsec() / 1000.0;
                            scene.CutsceneNoClip = false;
                            var length = character.AnimationGraph?.LastActionLength ?? 0f;
                            _loginStage?.StartClipEvents(character, ability.StartSignal, scene.CutsceneClipAt, length, false);
                            if (trace) Emit($"[x2] class cutscene clip {ability.StartSignal} started, {length:F2} s");
                        },
                        reachedLoop: () => { if (scene.Character == character && scene.CutsceneActive) scene.CutsceneNoClip = true; });
                }
                else
                {
                    // No cutscene (the page's own first pick, or "Disable skill preview"): the class idle at once, then the preview skills.
                    if (trace) Emit($"[x2] skillset page ability {abilityId}: idle {ability.IdleSignal}, weapon {ability.ActiveWeaponId} pack {ability.StartEquipPackId}");
                    character.PlayPresentationSequence([ability.IdleSignal], loopLast: true, started: character.RevealWhenPresentationReady);
                    StartIdleEffects(scene, ability, character);
                    if (scene.PreviewWanted) SchedulePreviewSkills(scene, ability, character, 1.2);
                }
            }
            else if (engine.Stage == Scripting.X2Engine.StageCustomize)
            {
                // The appearance page uses the same stage idle as the beauty shop (beautyshop.lua: "fist_ac_stage_idle"); the closeup
                // variant named in anim_actions has no .caf in the pak. No fidget: the camera is on the face.
                character.DisableStageFidgets();
                character.PlayPresentationLoop("fist_ac_stage_idle", "fist_ac_stage_idle", started: character.RevealWhenPresentationReady);
                scene.AbilityId = 0;
            }
            else
            {
                character.DisableStageFidgets();
                character.PlayPresentationAction("idle", loop: true, started: character.RevealWhenPresentationReady);
            }
        }
    }

    /// <summary>
    /// The skillset page's class cutscene. A skillset the PLAYER picks plays the class clip through the 04_ability_* cinema (camera on
    /// the actor's camera bone, timed effects) with the page hidden and "Skip (ESC key)" shown; the client tells the script through
    /// SHOW_CHARACTER_ABILITY_WINDOW(show, true): false when it starts, true when it ends or is skipped (Next unlocks then). The page's
    /// own first pick and "Disable skill preview" play no cutscene: the page stays and Next unlocks at once. After either, the actor
    /// performs the three preview skills with their particle effects.
    /// </summary>
    private void UpdateSkillActionCutscene(ModelViewScene scene, Scripting.X2Engine engine, CharacterNode character)
    {
        var now = Time.GetTicksMsec() / 1000.0;
        if (engine.Stage != Scripting.X2Engine.StageAbility)
        {
            scene.SkillActionSerial = engine.SkillActionSerial;
            EndSkillCutscene(scene); // left the page mid-video: put the page state back for the next visit
            _loginSkipRequested = false;
            CancelPreviewSkills(scene);
            _loginStage?.EndCinema(now);
            _loginStage?.StopAllFx(now, immediate: true);
            scene.IdleStream = null;
            scene.PreviewWanted = false;
            return;
        }
        if (scene.SkillActionSerial != engine.SkillActionSerial)
        {
            scene.SkillActionSerial = engine.SkillActionSerial;
            CancelPreviewSkills(scene);
            _loginStage?.EndCinema(now);
            _loginStage?.StopAllFx(now);
            scene.IdleStream = null;
            scene.CutsceneCharacter = null;
            scene.CutsceneNoClip = false;
            scene.PreviewWanted = !engine.SkipSkillAction;
            var ability = _loginAnimations?.Get(engine.LoginAnimationAbility);
            var cinema = !engine.SkillActionInitial && !engine.SkipSkillAction && ability != null && _loginStage != null
                ? _loginStage.LoadCinema(ability.StartSignal, _loginStage.PageRaceCode) : null;
            if (cinema != null)
            {
                scene.Cinema = cinema;
                scene.CutsceneStartedAt = now;
                if (!scene.CutsceneActive)
                {
                    scene.CutsceneActive = true;
                    _session.Root?.DispatchEvent("SHOW_CHARACTER_ABILITY_WINDOW", false, true);
                }
            }
            else
            {
                scene.Cinema = null;
                // No cutscene: the page keeps its widgets. Choosing a skillset locks Next until the video ends, so it is unlocked here
                // unless "Disable skill preview" already did.
                var wasActive = scene.CutsceneActive;
                scene.CutsceneActive = false;
                if (wasActive || engine.SkillActionInitial && !engine.SkipSkillAction)
                    _session.Root?.DispatchEvent("SHOW_CHARACTER_ABILITY_WINDOW", true, true);
                if (System.Environment.GetEnvironmentVariable("X2_TRACE_PREVIEW") == "1")
                    Emit($"[x2] skill action without cutscene ({(engine.SkillActionInitial ? "the page's first pick" : engine.SkipSkillAction ? "preview disabled" : "no cinema")})");
            }
        }
        if (!scene.CutsceneActive)
        {
            _loginSkipRequested = false;
            TickPreviewSkills(scene, engine, character, now);
            return;
        }
        var skip = _loginSkipRequested;
        _loginSkipRequested = false;
        var stage = _loginStage;
        if (stage == null || skip || scene.CutsceneNoClip || now - scene.CutsceneStartedAt > 25)
        {
            FinishSkillCutscene(scene, engine, character, now, skip ? "skipped" : "ended");
            return;
        }
        if (scene.CutsceneCharacter != character)
        {
            // The character is being rebuilt with the new skillset's outfit: the page camera stays until the new one's clip starts.
            if (stage.CinemaActive) stage.EndCinema(now);
            return;
        }
        if (!stage.CinemaActive && scene.Cinema != null) stage.BeginCinema(scene.Cinema, character, now);
        var t = now - scene.CutsceneClipAt;
        stage.TickCinema(t, now);
        if (stage.CinemaActive && t >= stage.CinemaDuration) FinishSkillCutscene(scene, engine, character, now, "ended");
    }

    /// <summary>The cutscene is over (its time ran out, or ESC): the page's camera and widgets return and the preview skills follow.</summary>
    private void FinishSkillCutscene(ModelViewScene scene, Scripting.X2Engine engine, CharacterNode character, double now, string how)
    {
        _loginStage?.EndCinema(now);
        if (System.Environment.GetEnvironmentVariable("X2_TRACE_PREVIEW") == "1")
            Emit($"[x2] class cutscene {how} after {now - scene.CutsceneStartedAt:F1} s");
        EndSkillCutscene(scene);
        scene.Cinema = null;
        scene.CutsceneCharacter = null;
        if (scene.Character != character || _loginAnimations?.Get(engine.LoginAnimationAbility) is not { } ability) return;
        character.StopAction(0.3f);
        StartIdleEffects(scene, ability, character);
        SchedulePreviewSkills(scene, ability, character, 1.0);
    }

    /// <summary>Effects the class idle clip carries (an aura, a floating orb) start with the idle and stay while it plays.</summary>
    private void StartIdleEffects(ModelViewScene scene, LoginStageAnimationCatalog.AbilityVisual ability, CharacterNode character)
    {
        if (_loginStage == null) return;
        _loginStage.StopStream(scene.IdleStream, Time.GetTicksMsec() / 1000.0, killEffects: true);
        scene.IdleStream = _loginStage.StartClipEvents(character, ability.IdleSignal, Time.GetTicksMsec() / 1000.0, 0f, false, persistent: true);
    }

    private void EndSkillCutscene(ModelViewScene scene)
    {
        if (!scene.CutsceneActive) return;
        scene.CutsceneActive = false;
        if (System.Environment.GetEnvironmentVariable("X2_TRACE_PREVIEW") == "1")
            Emit($"[x2] skill action cutscene ended after {Time.GetTicksMsec() / 1000.0 - scene.CutsceneStartedAt:F1} s");
        _session.Root?.DispatchEvent("SHOW_CHARACTER_ABILITY_WINDOW", true, true);
    }

    private int ItemSlot(long itemId)
    {
        try
        {
            using var db = new SqliteConnection($"Data Source={_gameDatabase};Mode=ReadOnly");
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = @"SELECT slot_type_id FROM item_armors WHERE item_id=@id
UNION ALL SELECT h.slot_type_id FROM item_weapons w LEFT JOIN holdables h ON h.id=w.holdable_id WHERE w.item_id=@id
UNION ALL SELECT slot_type_id FROM item_accessories WHERE item_id=@id LIMIT 1";
            command.Parameters.AddWithValue("@id", itemId);
            var value = command.ExecuteScalar();
            return value == null || value is DBNull ? -1 : Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }
        catch { return -1; }
    }

    private static PreviewBuild FromSnapshot(UnitSnapshot unit)
    {
        var equipment = new Dictionary<int, long>();
        foreach (var entry in unit.Equipment)
        {
            var item = entry.ImageTemplateId != 0 ? entry.ImageTemplateId : entry.TemplateId;
            if (item != 0) equipment[entry.Slot] = item;
        }
        return new PreviewBuild(unit.ModelId, equipment, Appearance(unit.Appearance), UseUnitEquipment: true);
    }

    private static UnitAppearance? Appearance(AppearanceParams? source)
    {
        if (source == null || source.Tier == AppearanceTier.None) return null;
        var result = new UnitAppearance
        {
            HairColorId = source.HairColor, SkinColorId = source.SkinColor,
            DefaultHairColor = source.DefaultHairColor, BodyNormalMapId = source.BodyNormalMap,
            TwoToneHairColor = source.TwoToneHairColor, TwoToneFirstWidth = source.TwoToneFirstWidth,
            TwoToneSecondWidth = source.TwoToneSecondWidth,
        };
        if (source.Face is not { } face) return result;
        result.FaceNormalMapId = face.NormalMapId;
        result.FaceNormalMapWeight = face.NormalMapWeight;
        result.DecalIds[0] = face.MovableDecalAssetId;
        result.DecalWeights[0] = face.MovableDecalWeight;
        for (var i = 0; i < face.FixedDecalAssetIds.Length && i + 1 < result.DecalIds.Length; i++)
        {
            result.DecalIds[i + 1] = face.FixedDecalAssetIds[i];
            if (i < face.FixedDecalWeights.Length) result.DecalWeights[i + 1] = face.FixedDecalWeights[i];
        }
        result.LipColor = face.LipColor; result.LeftPupilColor = face.LeftPupilColor;
        result.RightPupilColor = face.RightPupilColor; result.EyebrowColor = face.EyebrowColor;
        result.DecoColor = face.DecoColor; result.Modifier = face.Modifier;
        return result;
    }

    private static UnitAppearance Appearance(LoginCharacterAppearance source) => new()
    {
        HairColorId = source.HairColorId, SkinColorId = source.SkinColorId,
        FaceNormalMapId = source.FaceNormalMapId, BodyNormalMapId = source.BodyNormalMapId,
        FaceNormalMapWeight = source.FaceNormalMapWeight, BodyNormalMapWeight = source.BodyNormalMapWeight,
        DefaultHairColor = source.DefaultHairColor, LipColor = source.LipColor,
        TwoToneHairColor = source.TwoToneHairColor, TwoToneFirstWidth = source.TwoToneFirstWidth,
        TwoToneSecondWidth = source.TwoToneSecondWidth, LeftPupilColor = source.LeftPupilColor, RightPupilColor = source.RightPupilColor,
        EyebrowColor = source.EyebrowColor, DecoColor = source.DecoColor,
        DecalIds = (long[])source.DecalIds.Clone(), DecalWeights = (float[])source.DecalWeights.Clone(),
        Modifier = (byte[])source.Modifier.Clone(), HairItemId = source.HairItemId, FaceItemId = source.FaceItemId,
        BodyItemId = source.BodyItemId, HornItemId = source.HornItemId, TailItemId = source.TailItemId,
    };

    private long PlayableModel(object? raceValue, object? genderValue)
    {
        var race = raceValue switch
        {
            null => 1,
            double d => (int)d,
            int i => i,
            _ => (Convert.ToString(raceValue, CultureInfo.InvariantCulture) ?? "").ToLowerInvariant() switch
            {
                "nuian" => 1, "fairy" => 2, "dwarf" => 3, "elf" => 4, "hariharan" => 5,
                "ferre" => 6, "returned" => 7, "warborn" => 8, "daru" => 9, _ => 1,
            },
        };
        var gender = Convert.ToString(genderValue, CultureInfo.InvariantCulture);
        var female = genderValue is double genderNumber ? (int)genderNumber == 2 : genderValue is int genderInt ? genderInt == 2
            : gender?.Equals("female", StringComparison.OrdinalIgnoreCase) == true;
        if (!File.Exists(_gameDatabase)) return 0;
        using var db = new SqliteConnection($"Data Source={_gameDatabase};Mode=ReadOnly");
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT model_id FROM characters WHERE char_race_id=@race AND char_gender_id=@gender ORDER BY id LIMIT 1";
        command.Parameters.AddWithValue("@race", race);
        command.Parameters.AddWithValue("@gender", female ? 2 : 1);
        return Convert.ToInt64(command.ExecuteScalar() ?? 0, CultureInfo.InvariantCulture);
    }
}

/// <summary>Read-only login-stage animation and equipment lookups from the configured game database.</summary>
internal sealed class LoginStageAnimationCatalog : IDisposable
{
    internal sealed record AbilityVisual(int Id, string StartSignal, string EndSignal, string IdleSignal,
        int ActiveWeaponId, int StartEquipPackId, long[] PreviewSkillIds);

    private readonly SqliteConnection _db;
    private readonly Dictionary<int, AbilityVisual?> _abilities = [];

    internal LoginStageAnimationCatalog(string path)
    {
        _db = new SqliteConnection($"Data Source={path};Mode=ReadOnly");
        _db.Open();
    }

    internal AbilityVisual? Get(int abilityId)
    {
        if (_abilities.TryGetValue(abilityId, out var cached)) return cached;
        try
        {
            using var command = _db.CreateCommand();
            command.CommandText = """
SELECT start_anim_id,stop_anim_id,start_signal,end_signal,active_weapon_id,start_equip_pack_id,
       preview_skill_01_id,preview_skill_02_id,preview_skill_03_id
FROM login_stage_abilities WHERE ability_id=@id LIMIT 1
""";
            command.Parameters.AddWithValue("@id", abilityId);
            using var reader = command.ExecuteReader();
            if (!reader.Read()) return _abilities[abilityId] = null;
            var idleId = reader.GetInt32(0);
            var start = reader.IsDBNull(2) ? "" : reader.GetString(2);
            var end = reader.IsDBNull(3) ? "" : reader.GetString(3);
            var visual = new AbilityVisual(abilityId, start, end, "",
                reader.IsDBNull(4) ? 0 : reader.GetInt32(4), reader.IsDBNull(5) ? 0 : reader.GetInt32(5),
                [reader.IsDBNull(6) ? 0 : reader.GetInt64(6), reader.IsDBNull(7) ? 0 : reader.GetInt64(7),
                 reader.IsDBNull(8) ? 0 : reader.GetInt64(8)]);
            reader.Close();
            visual = visual with { IdleSignal = AnimationAction(idleId) };
            return _abilities[abilityId] = visual;
        }
        catch { return _abilities[abilityId] = null; }
    }

    internal Dictionary<int, long> AbilityEquipment(long modelId, int abilityId)
    {
        var equipment = new Dictionary<int, long>();
        var ability = Get(abilityId);
        if (ability == null) return equipment;
        try
        {
            using var command = _db.CreateCommand();
            command.CommandText = """
SELECT COALESCE(p.preview_cloth_pack_id,0),COALESCE(p.preview_weapon_pack_id,0)
FROM characters c LEFT JOIN ability_preview_packs p ON p.character_id=c.id AND p.ability_id=@ability
WHERE c.model_id=@model ORDER BY c.id LIMIT 1
""";
            command.Parameters.AddWithValue("@ability", abilityId);
            command.Parameters.AddWithValue("@model", modelId);
            using var reader = command.ExecuteReader();
            var clothId = ability.StartEquipPackId;
            var weaponPackId = 0;
            if (reader.Read())
            {
                if (!reader.IsDBNull(0) && reader.GetInt32(0) > 0) clothId = reader.GetInt32(0);
                if (!reader.IsDBNull(1)) weaponPackId = reader.GetInt32(1);
            }
            reader.Close();
            if (clothId > 0)
                ReadPack("SELECT headgear_id,necklace_id,shirt_id,belt_id,pants_id,glove_id,shoes_id,bracelet_id,back_id,undershirt_id,underpants_id,cosplay_id,backpack_id,stabilizer_id FROM equip_pack_cloths WHERE id=@id", clothId,
                    [0, 1, 2, 3, 4, 5, 6, 7, 8, 13, 14, 27, 26, 28], equipment);
            if (weaponPackId > 0)
                ReadPack("SELECT mainhand_id,offhand_id,ranged_id,musical_id FROM equip_pack_weapons WHERE id=@id", weaponPackId,
                    [15, 16, 17, 18], equipment);
        }
        catch { }
        return equipment;
    }

    private string AnimationAction(int id)
    {
        using var command = _db.CreateCommand();
        command.CommandText = "SELECT name FROM anim_actions WHERE id=@id LIMIT 1";
        command.Parameters.AddWithValue("@id", id);
        return Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture) ?? "";
    }

    private void ReadPack(string sql, int packId, int[] slots, Dictionary<int, long> equipment)
    {
        using var command = _db.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@id", packId);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return;
        for (var i = 0; i < slots.Length; i++)
            equipment[slots[i]] = reader.IsDBNull(i) ? 0 : Math.Max(0, reader.GetInt64(i));
    }

    public void Dispose() => _db.Dispose();
}
