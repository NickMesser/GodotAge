#nullable enable
using AAEmu.GodotViewer.Lua;

namespace AAEmu.GodotViewer.Ui.X2.Scripting;

/// <summary>
/// Script object returned by X2LoginCharacter:GetCustomizingUnit during stages 9-11. Every setter edits
/// <see cref="Appearance"/>, the one object both the 3D preview and CSCreateCharacter read, so what is shown is what is sent.
/// Getters answer from the appearance (1-based list positions, nil when nothing is selected), as the customizing_new scripts
/// expect. Colours cross the script boundary as 0..255 channels (palette.lua) and are stored as 0xAABBGGRR.
/// </summary>
internal sealed class LoginCustomizingUnit : Widget
{
    private const int PrLeft = 1, PrRight = 2;
    private readonly LoginCustomizationCatalog _catalog;
    private long _model;
    private readonly Action _changed;
    private readonly Dictionary<int, int> _presets = [];
    private int _previewCloth;
    private bool _smile;
    public LoginCharacterAppearance Appearance { get; private set; }
    public int Race { get; private set; }
    public int Gender { get; private set; }
    public long Model => _model;

    /// <summary>The preview cloth pack being tried on (character_preview_cloths.equip_pack_cloth_id), 0 for the race default.</summary>
    public long PreviewClothPack
    {
        get { var cloths = _catalog.PreviewCloths(_model); return _previewCloth >= 1 && _previewCloth <= cloths.Length ? cloths[_previewCloth - 1].Id : 0; }
    }

    /// <summary>A new unit for another stage; <paramref name="previous"/> (same race/gender) carries the look across stages.</summary>
    public LoginCustomizingUnit(UiRoot root, LoginCustomizationCatalog catalog, long model, int race, int gender, Action changed,
        LoginCustomizingUnit? previous = null)
        : base(root, "modelview", "__loginCustomizingUnit", null)
    {
        _catalog = catalog; _model = model; _changed = changed; Race = race; Gender = gender;
        if (previous != null && previous._model == model)
        {
            Appearance = previous.Appearance;
            foreach (var (k, v) in previous._presets) _presets[k] = v;
            _previewCloth = previous._previewCloth; _smile = previous._smile;
        }
        else
        {
            Appearance = catalog.Defaults(model);
            InitializeSelection();
        }
    }

    /// <summary>Another race/gender starts from its default look; re-selecting the same one (back to the race stage) keeps the edits.</summary>
    public void SetRaceGender(int race, int gender)
    {
        var model = _catalog.Model(race, gender);
        if (race == Race && gender == Gender && model == _model) { _changed(); return; }
        Race = race; Gender = gender; _model = model;
        Appearance = _catalog.Defaults(_model); InitializeSelection(); _changed();
    }
    public void InitCustomizerControl(bool unused = true) { }
    public void ApplyCustomizerParamToUnit() => _changed();

    // ------------------------------------------------------------------ hair, horn, tail

    public void SetCustomizingHair(int value)
    {
        var item = _catalog.Pick(_model, "hair", value);
        if (item == 0 || item == Appearance.HairItemId) return;
        // keep the colour number (hcNN) when the new hair has its own colour rows
        var oldColors = _catalog.HairColors(_model, Appearance.HairItemId);
        var newColors = _catalog.HairColors(_model, item);
        var position = Array.FindIndex(oldColors, c => c.Id == Appearance.HairColorId);
        if (newColors.Length > 0) Appearance.HairColorId = newColors[Math.Clamp(position, 0, newColors.Length - 1)].Id;
        Appearance.HairItemId = item;
        _changed();
    }
    public object? GetCustomHair() => Position("hair", Appearance.HairItemId);

    public void SetCustomizingHorn(int value)
    {
        var item = _catalog.Pick(_model, "horn", value);
        if (item == Appearance.HornItemId) return;
        var oldColors = _catalog.HornColors(_model, Appearance.HornItemId);
        var newColors = _catalog.HornColors(_model, item);
        var position = Array.FindIndex(oldColors, c => c.Id == Appearance.HornColorId);
        Appearance.HornColorId = newColors.Length > 0 ? newColors[Math.Clamp(position, 0, newColors.Length - 1)].Id : 0;
        Appearance.HornItemId = item;
        _changed();
    }
    public object? GetCustomHorn() => Position("horn", Appearance.HornItemId);
    public void SetCustomizingHornColor(int value)
    {
        var colors = _catalog.HornColors(_model, Appearance.HornItemId);
        if (value < 1 || value > colors.Length) return;
        Appearance.HornColorId = colors[value - 1].Id; _changed();
    }
    public object? GetCustomHornColor()
    {
        var i = Array.FindIndex(_catalog.HornColors(_model, Appearance.HornItemId), c => c.Id == Appearance.HornColorId);
        return i < 0 ? null : (double)(i + 1);
    }

    public void SetCustomizingTail(int value) { Appearance.TailItemId = _catalog.Pick(_model, "tail", value); _changed(); }
    public object? GetCustomTail() => Position("tail", Appearance.TailItemId);

    /// <summary>Colour choice: a table with index (colour grid), defaultR/G/B (palette) or twoToneR/G/B + widths (two-tone).</summary>
    public void SetCustomizingHairDefaultColor(object? value)
    {
        if (value is double n) { SelectHairColor((int)n); return; }
        if (value is not LuaTable t) return;
        if (t.GetValueOrDefault("index") is double index) SelectHairColor((int)index);
        if (Channel(t, "defaultR") is { } r && Channel(t, "defaultG") is { } g && Channel(t, "defaultB") is { } b)
            Appearance.DefaultHairColor = Recolor(PaletteHairColor(), Appearance.DefaultHairColor, r, g, b);
        if (Channel(t, "twoToneR") is { } tr && Channel(t, "twoToneG") is { } tg && Channel(t, "twoToneB") is { } tb)
            Appearance.TwoToneHairColor = Recolor(Appearance.TwoToneHairColor, Appearance.TwoToneHairColor, tr, tg, tb);
        if (t.GetValueOrDefault("firstWidth") is double first) Appearance.TwoToneFirstWidth = (float)Math.Clamp(first, 0, 1);
        if (t.GetValueOrDefault("secondWidth") is double second) Appearance.TwoToneSecondWidth = (float)Math.Clamp(second, 0, 1);
        _changed();
    }
    public void SetCustomizingHairColor(object? value) => SetCustomizingHairDefaultColor(value);
    public void SetCustomizingHairTwoToneColor(object? value) => SetCustomizingHairDefaultColor(value);

    /// <summary>A palette hair answers its colours as a table; a colour-grid hair answers the grid position.</summary>
    public object? GetCustomHairColor()
    {
        var hair = HairItem();
        if (hair?.UsePallet == true)
        {
            var color = PaletteHairColor();
            var t = new LuaTable { ["defaultR"] = R(color), ["defaultG"] = G(color), ["defaultB"] = B(color) };
            if (hair.TwoTone)
            {
                t["twoToneR"] = R(Appearance.TwoToneHairColor); t["twoToneG"] = G(Appearance.TwoToneHairColor); t["twoToneB"] = B(Appearance.TwoToneHairColor);
                t["firstWidth"] = (double)Appearance.TwoToneFirstWidth; t["secondWidth"] = (double)Appearance.TwoToneSecondWidth;
            }
            return t;
        }
        var i = Array.FindIndex(_catalog.HairColors(_model, Appearance.HairItemId), c => c.Id == Appearance.HairColorId);
        return i < 0 ? null : (double)(i + 1);
    }
    public object GetTwoToneHairStatus() => new LuaMulti(HairItem()?.TwoTone == true, (double)R(Appearance.TwoToneHairColor),
        (double)G(Appearance.TwoToneHairColor), (double)B(Appearance.TwoToneHairColor));

    // ------------------------------------------------------------------ skin and face textures

    public void SetCustomizingFaceDiffuse(int value) { Appearance.FaceItemId = _catalog.Pick(_model, "face", value); _changed(); }
    public object? GetCustomFaceDiffuse() => Position("face", Appearance.FaceItemId);
    public void SetCustomizingFaceNormal(int value, double weight = 1)
    {
        Appearance.FaceNormalMapId = _catalog.Pick(_model, "face_normal", value);
        Appearance.FaceNormalMapWeight = (float)Math.Clamp(weight, 0, 1); _changed();
    }
    public object GetCustomFaceNormal() => new LuaMulti(Position("face_normal", Appearance.FaceNormalMapId), (double)Appearance.FaceNormalMapWeight);
    public void SetCustomizingSkinColor(int value) { var id = _catalog.Pick(_model, "skin", value); if (id > 0) { Appearance.SkinColorId = id; _changed(); } }
    public object? GetCustomSkinColor() => Position("skin", Appearance.SkinColorId);
    public void SetCustomizingBodyNormal(int value, double weight = 1)
    {
        Appearance.BodyNormalMapId = _catalog.Pick(_model, "body_normal", value);
        Appearance.BodyNormalMapWeight = (float)Math.Clamp(weight, 0, 1); _changed();
    }
    public object GetCustomBodyNormal() => new LuaMulti(Position("body_normal", Appearance.BodyNormalMapId), (double)Appearance.BodyNormalMapWeight);

    // ------------------------------------------------------------------ face decals (DecalIds: 0 scar, 1 tattoo, 2 makeup, 3 eyebrow, 4 deco, 5/6 pupils)

    public void SetCustomizingEyebrow(int value) { Appearance.DecalIds[3] = _catalog.Pick(_model, "eyebrow", value); _changed(); }
    public object? GetCustomEyebrow() => Position("eyebrow", Appearance.DecalIds[3]);
    public void SetCustomizingPupil(int value, double range = 3)
    {
        var id = _catalog.Pick(_model, "pupil", value);
        if ((int)range != PrRight) Appearance.DecalIds[5] = id;
        if ((int)range != PrLeft) Appearance.DecalIds[6] = id;
        _changed();
    }
    public object? GetCustomPupil(double range = 3) => Position("pupil", Appearance.DecalIds[(int)range == PrRight ? 6 : 5]);
    public void SetCustomizingScar(int value, double x = 0, double y = 0, double scale = 0, double rotate = 0, double weight = 1)
    {
        Appearance.DecalIds[0] = _catalog.Pick(_model, "scar", value);
        Appearance.DecalWeights[0] = Appearance.MovableDecalWeight = (float)Math.Clamp(weight, 0, 1);
        Appearance.MovableDecalMoveX = (short)Math.Clamp(Math.Round(x), short.MinValue, short.MaxValue);
        Appearance.MovableDecalMoveY = (short)Math.Clamp(Math.Round(y), short.MinValue, short.MaxValue);
        Appearance.MovableDecalScale = (float)scale; Appearance.MovableDecalRotate = (float)rotate;
        _changed();
    }
    public object? GetCustomScar() => Position("scar", Appearance.DecalIds[0]);
    public LuaTable GetScarStatus() => new()
    {
        ["weight"] = (double)Appearance.MovableDecalWeight, ["x"] = (double)Appearance.MovableDecalMoveX,
        ["y"] = (double)Appearance.MovableDecalMoveY, ["scale"] = (double)Appearance.MovableDecalScale,
        ["rotate"] = (double)Appearance.MovableDecalRotate,
    };
    public void SetCustomizingTattoo(int value, double weight = 1) => SetDecal(1, "tattoo", value, weight);
    public object GetCustomTattoo() => Decal(1, "tattoo");
    public void SetCustomizingMakeUp(int value, double weight = 1) => SetDecal(2, "makeup", value, weight);
    public object GetCustomMakeUp() => Decal(2, "makeup");
    public void SetCustomizingDeco(int value, double weight = 1) => SetDecal(4, "deco", value, weight);
    public object GetCustomDeco() => Decal(4, "deco");

    // ------------------------------------------------------------------ preview clothes

    public void SetCustomizingPreviewCloth(int value)
    {
        if (value == _previewCloth) return;
        _previewCloth = Math.Clamp(value, 0, _catalog.PreviewCloths(_model).Length); _changed();
    }
    public object? GetCustomPreviewCloth() => _previewCloth > 0 ? (double)_previewCloth : null;

    // ------------------------------------------------------------------ face presets and sliders

    /// <summary>
    /// Part 5 replaces the whole look with a total_character_customs preset; parts 1-4 copy a custom_face_presets row's bytes
    /// of that part into the modifier (the other parts' bytes stay).
    /// </summary>
    public void ApplyPresetParam(int part, int value)
    {
        if (part == LoginCustomizationCatalog.PartTotal)
        {
            if (_catalog.TotalPreset(_model, value) is not { } look) return;
            Appearance = look;
            _presets[part] = value;
            for (var p = LoginCustomizationCatalog.PartEye; p <= LoginCustomizationCatalog.PartShape; p++)
                _presets[p] = _catalog.MatchingFacePreset(_model, p, Appearance.Modifier);
        }
        else
        {
            EnsureModifier();
            if (!_catalog.ApplyFacePreset(_model, part, value, Appearance.Modifier)) return;
            _presets[part] = value;
        }
        _changed();
    }
    public object? GetSelectedPresetIndex(int part) => _presets.GetValueOrDefault(part) is var i and > 0 ? (double)i : null;

    /// <summary>A face slider: the target's byte of the modifier holds its value (signed percent).</summary>
    public void ModifyFaceParamValue(int index, double value)
    {
        if (index is < 0 or >= 128) return;
        EnsureModifier();
        var b = unchecked((byte)(sbyte)Math.Clamp(Math.Round(value), sbyte.MinValue, sbyte.MaxValue));
        if (Appearance.Modifier[index] == b) return;
        Appearance.Modifier[index] = b;
        _changed();
    }
    public double GetFaceTargetCurValue(int index) => index >= 0 && index < Appearance.Modifier.Length ? (sbyte)Appearance.Modifier[index] : 0;

    public bool GetCustomizingOddEyeUsable() => false;
    public void SetSmile(bool value) => _smile = value;
    public bool IsSmile() => _smile;
    public void SetStance(params object?[] unused) { }
    public void AddAnimationState(params object?[] unused) { }

    // ------------------------------------------------------------------ colours (0..255 channels)

    public void SetCustomizingEyebrowColor(double r, double g, double b) { Appearance.EyebrowColor = Recolor(Appearance.EyebrowColor, r, g, b); _changed(); }
    public object GetCustomEyebrowColor() => Rgb(Appearance.EyebrowColor);
    public void SetCustomizingPupilColor(double r, double g, double b, double range = 3)
    {
        if ((int)range != PrRight) Appearance.LeftPupilColor = Recolor(Appearance.LeftPupilColor, r, g, b);
        if ((int)range != PrLeft) Appearance.RightPupilColor = Recolor(Appearance.RightPupilColor, r, g, b);
        _changed();
    }
    public object GetCustomPupilColor(double range = 3) => Rgb((int)range == PrRight ? Appearance.RightPupilColor : Appearance.LeftPupilColor);
    public void SetCustomizingLipColor(double r, double g, double b) { Appearance.LipColor = Recolor(Appearance.LipColor, r, g, b); _changed(); }
    public object GetCustomLipColor() => Rgb(Appearance.LipColor);
    public void SetCustomizingDecoColor(double r, double g, double b) { Appearance.DecoColor = Recolor(Appearance.DecoColor, r, g, b); _changed(); }
    public object GetCustomizingDecoColor() => Rgb(Appearance.DecoColor);

    // ------------------------------------------------------------------ helpers

    private void InitializeSelection()
    {
        _presets.Clear();
        _presets[LoginCustomizationCatalog.PartTotal] = _catalog.DefaultTotalPreset(_model);
        for (var p = LoginCustomizationCatalog.PartEye; p <= LoginCustomizationCatalog.PartShape; p++)
            _presets[p] = _catalog.MatchingFacePreset(_model, p, Appearance.Modifier);
        _previewCloth = 0;
        _smile = false;
    }

    private LoginCustomizingItem? HairItem() => _catalog.Items(_model, "hair").FirstOrDefault(i => i.Id == Appearance.HairItemId);
    private void SelectHairColor(int index)
    {
        var colors = _catalog.HairColors(_model, Appearance.HairItemId);
        if (index >= 1 && index <= colors.Length) Appearance.HairColorId = colors[index - 1].Id;
    }
    private void SetDecal(int slot, string kind, int value, double weight)
    {
        Appearance.DecalIds[slot] = _catalog.Pick(_model, kind, value);
        Appearance.DecalWeights[slot] = (float)Math.Clamp(weight, 0, 1);
        _changed();
    }
    private object Decal(int slot, string kind) => new LuaMulti(Position(kind, Appearance.DecalIds[slot]), (double)Appearance.DecalWeights[slot]);
    private object? Position(string kind, long id) => _catalog.IndexOf(_model, kind, id) is var i and > 0 ? (double)i : null;
    private void EnsureModifier() { if (Appearance.Modifier.Length != 128) { var m = Appearance.Modifier; Array.Resize(ref m, 128); Appearance.Modifier = m; } }
    private static int? Channel(LuaTable t, string key) => t.GetValueOrDefault(key) is double d ? (int)Math.Round(d) : null;
    /// <summary>
    /// The palette re-sends the colour it was initialised with every time a panel refreshes (palette.lua L_Init calls
    /// SetColorProc). A colour equal to the one the getter reported keeps the stored value, so opening a panel never turns an
    /// unset colour (0) into an opaque black one.
    /// </summary>
    private static uint Recolor(uint current, double r, double g, double b) => Recolor(current, current, (int)Math.Round(r), (int)Math.Round(g), (int)Math.Round(b));
    private static uint Recolor(uint shown, uint stored, int r, int g, int b) =>
        R(shown) == r && G(shown) == g && B(shown) == b ? stored : LoginCustomizationCatalog.Abgr(r, g, b);
    private uint PaletteHairColor() => Appearance.DefaultHairColor != 0 ? Appearance.DefaultHairColor : _catalog.HairColorBase(Appearance.HairColorId);
    private static int R(uint c) => (int)(c & 255);
    private static int G(uint c) => (int)((c >> 8) & 255);
    private static int B(uint c) => (int)((c >> 16) & 255);
    private static object Rgb(uint c) => new LuaMulti((double)R(c), (double)G(c), (double)B(c));
}
