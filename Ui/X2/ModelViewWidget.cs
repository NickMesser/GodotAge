#nullable enable
namespace AAEmu.GodotViewer.Ui.X2;

/// <summary>Script-facing state for a native character preview.</summary>
public sealed class ModelViewWidget : Widget
{
    internal ModelViewWidget(UiRoot root, string type, string id, Widget? parent) : base(root, type, id, parent) { }

    public TextureDrawable? BackgroundDrawable { get; private set; }
    public string? BackgroundPath { get; private set; }
    public string? BackgroundKey { get; private set; }
    public UiRect ViewCoords { get; private set; }
    public UiRect ViewExtent { get; private set; }
    public UiRect TextureSize { get; private set; }
    public double Fov { get; private set; } = 30;
    public bool Frozen { get; private set; }
    public bool DisableColorGrading { get; private set; }
    public double Rotation { get; private set; }
    public double RotationX { get; private set; }
    public double ModelPosX { get; private set; }
    public double ModelPosZ { get; private set; }
    public double Zoom { get; private set; }
    public string? Animation { get; private set; }
    public bool AnimationLoop { get; private set; }
    public object? ModelRef { get; private set; }
    public bool BeautyShop { get; private set; }
    public bool CosplayEquipped { get; private set; } = true;
    public void ToggleCosplayEquipped(bool equipped) { CosplayEquipped = equipped; Changed(true); }
    public double GetSelectedPresetIndex(double type) => _appearance.TryGetValue("preset_" + type, out var args) && args.Length > 0 && args[0] is double n ? n : 1;
    public object? Race { get; private set; }
    public object? Gender { get; private set; }
    /// <summary>Changes whenever renderer-visible state changes.</summary>
    public long StateVersion { get; private set; }
    /// <summary>Changes only when the character must be rebuilt.</summary>
    public long ModelVersion { get; private set; }
    public event Action? StateChanged;
    public IReadOnlyDictionary<string, object?[]> Appearance => _appearance;
    private readonly Dictionary<string, object?[]> _appearance = new(StringComparer.Ordinal);

    public void SetModelViewBackground(string path, string? key = null)
    {
        BackgroundPath = path;
        BackgroundKey = key;
        if (BackgroundDrawable == null)
        {
            BackgroundDrawable = key == null ? CreateImageDrawable(path, "background") : CreateDrawable(path, key, "background");
            BackgroundDrawable.AddAnchor("TOPLEFT", this, 0.0, 0.0);
            BackgroundDrawable.AddAnchor("BOTTOMRIGHT", this, 0.0, 0.0);
        }
        else
        {
            BackgroundDrawable.SetTexture(path);
            if (key != null) BackgroundDrawable.SetTextureInfo(key);
        }
        Changed(true);
    }

    public void SetModelViewCoords(double x, double y, double width, double height)
    { ViewCoords = new UiRect((float)x, (float)y, (float)width, (float)height); Changed(); }
    public void SetModelViewExtent(double width, double height)
    { ViewExtent = new UiRect(0, 0, (float)width, (float)height); Changed(); }
    public void SetTextureSize(double width, double height)
    { TextureSize = new UiRect(0, 0, (float)width, (float)height); Changed(); }
    public void SetFov(double fov) { Fov = Math.Clamp(fov, 1, 179); Changed(); }
    public void SetDisableColorGrading(bool disabled) => DisableColorGrading = disabled;
    public void SetFreeze(bool frozen) { Frozen = frozen; Changed(); }
    public bool IsFrozen() => Frozen;
    public void SetRotation(double rotation) { Rotation = rotation; Changed(); }
    public void SetRotationX(double rotation) { RotationX = rotation; Changed(); }
    public void AddRotation(double delta) { Rotation += delta; Changed(); }
    public void AddRotationX(double delta) { RotationX += delta; Changed(); }
    public void AddModelPosX(double delta) { ModelPosX += delta; Changed(); }
    public void AddModelPosZ(double delta) { ModelPosZ += delta; Changed(); }
    public void ResetModelPos() { ModelPosX = 0; ModelPosZ = 0; Changed(); }
    public void ZoomInOut(double delta) { Zoom += delta; Changed(); }
    public void ZoomInOutBeautyShop(double delta) { Zoom += delta; Changed(); }
    public void ResetZoom() { Zoom = 0; Changed(); }
    public void PlayAnimation(string? name, bool loop = false) { Animation = name; AnimationLoop = loop; Changed(); }
    public void StopAnimation() { Animation = null; Changed(); }

    public void Init(object? unitRef, bool copyEquipment = true)
    {
        ModelRef = unitRef;
        BeautyShop = false;
        _appearance["copy_equipment"] = [copyEquipment];
        Changed(true);
    }
    public void InitBeautyShop() { BeautyShop = true; ModelRef = "player"; Animation = null; _appearance.Clear(); Changed(true); }
    public void InitByModelRef(object? modelRef, object? race = null, object? gender = null, bool unused = false)
    {
        ModelRef = modelRef;
        Race = race;
        Gender = gender;
        BeautyShop = false;
        Changed(true);
    }
    public void ClearModel() { ModelRef = null; Animation = null; _appearance.Clear(); Changed(true); }
    public void ResetBeautyShop() { _appearance.Clear(); Rotation = 0; RotationX = 0; Zoom = 0; ModelPosX = ModelPosZ = 0; Changed(true); }
    public void InitCustomizerControl(bool enabled = true) => Remember("customizer_control", enabled);
    public void ApplyCustomizerParamToUnit() => Remember("customizer_applied", true);
    public bool HasDiffWithClientUnit() => _appearance.Count > 0;
    public object? GetRace() => Race;
    public object? GetGender() => Gender;
    public void SetRace(object? race) { Race = race; Changed(true); }
    public void SetGender(object? gender) { Gender = gender; Changed(true); }
    public void AdjustCameraPos(params object?[] args) => Remember("camera", args);
    public void AdjustCameraPosToModel(params object?[] args) => Remember("camera_to_model", args);
    public void SetEquipment(params object?[] args) => Remember("equipment", args);
    public void SetCostume(params object?[] args) => Remember("costume", args);
    public void SetDye(params object?[] args) => Remember("dye", args);
    public void SetCustomizerParam(params object?[] args) => Remember("customizer", args);
    public void EquipItem(params object?[] args) => Remember("equip_item", args);
    public void EquipCostume(params object?[] args) => Remember("equip_costume", args);
    public void ResetEquips() { _appearance.Remove("equip_item"); _appearance.Remove("equip_costume"); _appearance.Remove("show_item"); Changed(true); }
    public LuaMulti ShowItem(params object?[] args) { Remember("show_item", args); return new LuaMulti(true, true); }
    public void ApplyModelByDyeingItem(params object?[] args) => Remember("dyeing_item", args);
    public void UpdateDyeColor(params object?[] args) => Remember("dye_color", args);
    public void SetIngameShopCamMode(params object?[] args) => Remember("ingame_shop_camera", args);
    private void Remember(string key, params object?[] args)
    {
        _appearance[key] = args;
        Changed(key is not ("camera" or "camera_to_model" or "ingame_shop_camera"));
    }
    private void Changed(bool model = false) { StateVersion++; if (model) ModelVersion++; StateChanged?.Invoke(); }
}
