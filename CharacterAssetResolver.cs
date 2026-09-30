using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Xml;

namespace AAEmu.GodotViewer;

/// <summary>One mesh to draw for a character.</summary>
public sealed class CharacterPart
{
    /// <summary>
    /// CDF attachment name the part fills (body, face, hair, horns, tail, beard, head, chest, waist, legs, hands,
    /// feet, arms, back, backpack, cosplay, ..., mainhand, offhand, ranged, musical), or "base" for the actor's own mesh.
    /// </summary>
    public string Slot = "";

    /// <summary>Pak path of the mesh: a .chr (skinned) or a .cgf (rigid, follows <see cref="BoneName"/>).</summary>
    public string ModelPath = "";

    /// <summary>Pak path of the .mtl to use, or "" for the model's default (<see cref="MtlReader.ResolveModelMaterial"/>).</summary>
    public string MaterialPath = "";

    /// <summary>True for .chr parts: skin them with the master skeleton (map bones by name, <see cref="ChrSkeleton.MapTo"/>).</summary>
    public bool IsSkinned;

    /// <summary>Rigid parts: bone the mesh follows while wielded / worn (e.g. item_hand_r, item_back_c).</summary>
    public string BoneName = "";

    /// <summary>Weapons: bone while sheathed (item_side_r/l, item_back_c, item_staff_l, item_bow_l, ...), "" when unknown.</summary>
    public string SheathBoneName = "";

    /// <summary>
    /// Rigid parts: mesh-to-bone transform (row-vector convention, CryEngine axes, metres). Composed from the CDF
    /// attachment placement (made bone-relative) and the item asset's attachment offset. Identity for skinned parts.
    /// </summary>
    public Matrix4x4 BoneOffset = Matrix4x4.Identity;

    /// <summary>Uniform scale (CDF attachment scale times item_weapons.drawn_scale for weapons).</summary>
    public float Scale = 1;

    /// <summary>Equipment slot (enum_equip_slot id) the part came from, -1 for authored CDF parts.</summary>
    public int EquipSlot = -1;

    public long ItemId;
    public long AssetId;

    /// <summary>Human-readable origin, for diagnostics ("item 536 body", "cdf nu_m_base.cdf").</summary>
    public string Source = "";

    public override string ToString() =>
        $"{Slot}: {ModelPath}{(MaterialPath.Length > 0 ? " + " + MaterialPath : "")}{(BoneName.Length > 0 ? " @" + BoneName : "")} [{Source}]";
}

/// <summary>Skin colour row (skin_colors): replaces the HumanSkin material parameters of body, face and armour "skin" sub-materials.</summary>
public sealed class SkinColorInfo
{
    public long Id;

    /// <summary>Material Diffuse (0..1). Matches the .mtl "Diffuse" attribute for the default row.</summary>
    public Vector3 Diffuse;

    public Vector3 Specular;

    /// <summary>PublicParams MSkinColor / BSkinColor (0..1).</summary>
    public Vector3 MiddleSkinColor, BrightSkinColor;

    public float Glossiness, SpecularLevel;

    /// <summary>Ferre fur variants: material suffix ("blan01" -> fe_m_nude_blan01.mtl); empty otherwise.</summary>
    public string CustomPostfix = "";
}

public sealed class FaceDecal
{
    /// <summary>scar (movable), tattoo, makeup, eyebrow, deco, pupil_left, pupil_right.</summary>
    public string Kind = "";

    public string TexturePath = "";
    public float Weight = 1;

    /// <summary>
    /// Colour from the appearance (pupil/eyebrow/deco), 0 when none: 0xAABBGGRR (red in the low byte; checked against
    /// the original client's GetCustomEyebrowColor for total_character_customs 298).
    /// </summary>
    public uint Color;

    /// <summary>
    /// Top-left corner of the decal in the face diffuse texture, in that texture's pixels (face_decal_assets
    /// defaultX/defaultY; pupils: odd_eye_info left/right).
    /// </summary>
    public int X, Y;
}

/// <summary>Appearance values (the UnitCustomModelParams fields that change what is drawn).</summary>
public sealed class UnitAppearance
{
    /// <summary>customizing_item_asset_colors.id: selects a complete replacement hair material (&lt;hair&gt;_hcNN.mtl).</summary>
    public long HairColorId;

    /// <summary>skin_colors.id.</summary>
    public long SkinColorId;

    public long FaceNormalMapId, BodyNormalMapId;
    public float FaceNormalMapWeight = 1, BodyNormalMapWeight = 1;

    /// <summary>face_decal_assets ids: [0] movable (scar), [1..6] fixed 0..5 (tattoo, makeup, eyebrow, deco, left pupil, right pupil).</summary>
    /// <summary>Palette hair colour (total_character_customs.default_hair_color, ABGR), used by hairs with use_pallet.</summary>
    public uint DefaultHairColor;

    /// <summary>Two-tone hair (total_character_customs.two_tone_*): the second colour (ABGR) and the two mask widths.</summary>
    public uint TwoToneHairColor;
    public float TwoToneFirstWidth, TwoToneSecondWidth;

    public long[] DecalIds = new long[7];

    public float[] DecalWeights = [1, 1, 1, 1, 1, 1, 1];
    public uint LipColor, LeftPupilColor, RightPupilColor, EyebrowColor, DecoColor;

    /// <summary>128 signed bytes; byte i drives Target Idx i of the race's *_targets.xml (percent, within Min..Max).</summary>
    public byte[] Modifier = [];

    /// <summary>Item ids of body parts carried by a total_character_customs row (0 = none).</summary>
    public long HairItemId, FaceItemId, BodyItemId, HornItemId, TailItemId;
}

/// <summary>Everything a renderer needs to draw one unit.</summary>
public sealed class CharacterAssets
{
    public long ModelId;

    /// <summary>"character" (a playable race model: composed from body-part and equipment items), "actor" (its own .cdf/.chr), "prefab", "vehicle", "ship", "missing".</summary>
    public string Kind = "";

    /// <summary>actor_models.model_file as a pak path (.cdf or .chr).</summary>
    public string ModelFile = "";

    /// <summary>The .chr whose skeleton drives every part (the CDF's Model File).</summary>
    public string SkeletonPath = "";

    /// <summary>Animation list next to the skeleton (&lt;skeleton&gt;.cal), "" when missing.</summary>
    public string AnimationListPath = "";

    /// <summary>CAL action families selected by the equipped glider's item_backpacks row.</summary>
    public string GliderAnimation = "";
    public string GliderFastAnimation = "";
    public string GliderSlowAnimation = "";
    public string GliderSlidingAnimation = "";

    public List<CharacterPart> Parts = [];

    /// <summary>Underwear: textures laid over the nude body diffuse (undershirt/underpants items), not meshes.</summary>
    public List<string> BodyTextureOverlays = [];

    public SkinColorInfo SkinColor;

    /// <summary>Hair scalp texture (item_body_parts.hair_base), drawn on the head; "" when none.</summary>
    public string HairBaseTexture = "";

    /// <summary>Colour (ABGR, 0 = none) the hair scalp texture is painted with: the hair colour row's hair_base_color, or a palette hair's colour.</summary>
    public uint HairBaseColor;

    /// <summary>
    /// True when the hair is a palette hair (customizing_item_assets.use_pallet): its material has a _mask texture and
    /// the colour comes from <see cref="HairColor"/> instead of an _hcNN material.
    /// </summary>
    public bool HairUsesPalette;

    /// <summary>Palette hair colour (ABGR as stored, byte order inferred), 0 when none.</summary>
    public uint HairColor;

    /// <summary>Two-tone palette hair: second colour (ABGR, 0 = none) and the two blend widths (Hair shader HairColorWidth/2).</summary>
    public uint HairSecondColor;
    public float HairWidth1, HairWidth2;

    public List<FaceDecal> FaceDecals = [];

    /// <summary>Lip colour (0xAABBGGRR), laid over the lips of the face texture (face mask green channel); 0 when none.</summary>
    public uint LipColor;
    public string FaceNormalMap = "";
    public float FaceNormalMapWeight;
    public string BodyNormalMap = "";

    /// <summary>
    /// Face morph target name (as in the face .chr, without '#') to weight: the race's Apply="always" morph (this is what
    /// moves a borrowed nuian face onto a dwarf/elf/hariharan head; apply it, then skin with the master's inverse bind)
    /// plus the appearance modifier sliders. Apply with <see cref="ChrModel.ApplyMorphTarget"/> before building the mesh.
    /// </summary>
    public Dictionary<string, float> FaceMorphs = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The race/gender *_targets.xml used for <see cref="FaceMorphs"/>.</summary>
    public string FaceTargetsPath = "";

    /// <summary>The appearance modifier <see cref="FaceMorphs"/> was computed from.</summary>
    public byte[] FaceModifier = [];

    /// <summary>npcs.scale (uniform), 1 for players.</summary>
    public float Scale = 1;

    public List<string> Warnings = [];

    public CharacterPart Find(string slot) => Parts.Find(p => p.Slot.Equals(slot, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Turns a model id (the modelRef of SCUnitState = models.id = npcs.model_id = characters.model_id), equipped item
/// template ids and appearance values into the files to load. Reads the game database through any ADO.NET
/// connection (e.g. Microsoft.Data.Sqlite on compact.sqlite3) and the pak through delegates.
/// <para>Rules (verified against the database and pak unless marked inferred; see the report):</para>
/// <list type="number">
/// <item>models.id -> (sub_type, sub_id); ActorModel -> actor_models.id = sub_id -> model_file (.cdf or .chr).
///   models.id is never equal to actor_models.id.</item>
/// <item>A .cdf gives the skeleton (Model File) and authored attachments; a .chr is skeleton and mesh in one.
///   Playable race CDFs (nu_m.cdf ...) have empty attachments, filled from items.</item>
/// <item>Body parts (equip slots 19-25: face, hair, glasses, horns, tail, body, beard): item_body_parts (item_id,
///   model_id) -> asset_id -> item_assets.path. Face items point at wrapper CDFs (Model File + Material).</item>
/// <item>Armour (slots 0-8, 26-28, 31-33): item_armors.asset_id -> item_armor_assets.armor_asset_id -> asset_id ->
///   item_assets whose model_id equals the unit's model id (no row: no visual). Paths are .chr, .cgf or wrapper .cdf.
///   Underwear (13, 14, 29, 30) resolves to a .dds overlaid on the body texture.</item>
/// <item>Weapons (15-18): item_weapons.asset_id -> item_assets (model 0, race independent) -> .cgf on the CDF bone
///   attachment of the slot (mainhand/ranged/musical item_hand_r, offhand item_hand_l).</item>
/// <item>Helmets/costumes pick the hair variant: item_assets.detail bits 13-15 = category (3 helmet, 7 costume),
///   bits 10-12 = enum_hair_types (0 default, 1 none, 2..5 = item_body_parts.asset_1_id..asset_4_id). Inferred from
///   the data (exact correlation over all rows), not from client code.</item>
/// <item>Appearance: hair colour -> customizing_item_asset_colors.material (full hair material); skin colour ->
///   skin_colors; face maps/decals -> face_normal_maps / face_decal_assets; modifier -> face morph weights.</item>
/// <item>Drawing rules the caller applies: skip sub-materials with Shader="Cut" and remove body/hair triangles inside
///   them (<see cref="ChrCutVolumes"/>); apply <see cref="CharacterAssets.FaceMorphs"/>; skin every .chr part with
///   <see cref="ChrModel.SkinBinds"/> against the skeleton of <see cref="CharacterAssets.SkeletonPath"/>.</item>
/// </list>
/// Not thread-safe (one resolution at a time per instance).
/// </summary>
public sealed class CharacterAssetResolver
{
    private readonly DbConnection _db;
    private readonly Func<string, bool> _exists;
    private readonly Func<string, byte[]> _read;
    private ChrSkeleton _skeleton; // skeleton of the unit being resolved (the resolver is not thread-safe)

    /// <summary>When a costume (cosplay slot 27) has a visual, hide chest/waist/legs/hands/feet/arms armour (inferred, default on).</summary>
    public bool CostumeHidesArmor = true;

    /// <summary>
    /// When a costume has a visual, drop the nude body (inferred, default on): costume .mtl files carry their own
    /// HumanSkin sub-material for every exposed area (swimsuits included), and the body otherwise pokes through.
    /// </summary>
    public bool CostumeHidesBody = true;

    /// <summary>enum_equip_slot names; equal to the CDF attachment names (ear/finger/neck have no attachment).</summary>
    public static readonly string[] EquipSlotNames =
    [
        "head", "neck", "chest", "waist", "legs", "hands", "feet", "arms", "back", "ear_1", "ear_2", "finger_1", "finger_2",
        "undershirt", "underpants", "mainhand", "offhand", "ranged", "musical", "face", "hair", "glasses", "horns", "tail",
        "body", "beard", "backpack", "cosplay", "stabilizer", "undershirt2", "underpants2", "cosplaylooks", "race_cosplay",
        "race_cosplaylooks",
    ];

    /// <summary>Equip slot of each equip_pack_cloths column, in the order headgear, necklace, shirt, belt, pants, glove, shoes,
    /// bracelet, back, undershirt, underpants, cosplay, backpack, stabilizer (the preview cloth packs).</summary>
    public static readonly int[] PreviewPackSlots = [SlotHead, 1, SlotChest, 3, 4, 5, 6, 7, SlotBack,
        SlotUndershirt, SlotUnderpants, SlotCosplay, SlotBackpack, 28];

    public const int SlotHead = 0, SlotChest = 2, SlotBack = 8, SlotUndershirt = 13, SlotUnderpants = 14, SlotMainhand = 15,
        SlotOffhand = 16, SlotRanged = 17, SlotMusical = 18, SlotFace = 19, SlotHair = 20, SlotGlasses = 21, SlotHorns = 22,
        SlotTail = 23, SlotBody = 24, SlotBeard = 25, SlotBackpack = 26, SlotCosplay = 27;

    /// <summary>equip slot -> item_body_parts.slot_type_id (enum_equip_slot_types) for body slots 19-25.</summary>
    private static readonly Dictionary<int, int> BodySlotType = new()
    {
        [SlotFace] = 23, [SlotHair] = 24, [SlotGlasses] = 25, [SlotHorns] = 26, [SlotTail] = 27, [SlotBody] = 28, [SlotBeard] = 29,
    };

    private static readonly string[] SheathBones = ["", "item_side_r", "item_back_r", "item_back_c", "item_staff_l", "item_bow_l", "item_lute_r", "item_book_c"];
    private static readonly string[] EquipPosBones = ["", "item_hand_r", "item_hand_l", "item_arm_l", "item_hand_b"];

    public CharacterAssetResolver(DbConnection db, Func<string, bool> exists, Func<string, byte[]> read)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _exists = exists ?? throw new ArgumentNullException(nameof(exists));
        _read = read ?? throw new ArgumentNullException(nameof(read));
    }

    // ================================================================== public entry points

    /// <summary>Default look of a playable race model (characters row): body, default face, default custom (hair, horns ...).</summary>
    public CharacterAssets ResolvePlayerDefault(long modelId)
    {
        var ch = Row("SELECT face_item_id, default_custom_id FROM characters WHERE model_id=@m ORDER BY id LIMIT 1", ("@m", modelId));
        var equipment = new Dictionary<int, long>();
        UnitAppearance look = null;
        if (ch != null)
        {
            look = LoadTotalCustom(L(ch[1]));
            FillBodyParts(modelId, look, L(ch[0]), equipment);
        }
        return ResolveUnit(modelId, equipment, look);
    }

    /// <summary>
    /// A playable-race creation preview with its authored default body parts and preview cloth pack, overridden by
    /// the supplied equipment and customization. Character creation sends only the fields changed in the editor.
    /// </summary>
    public CharacterAssets ResolvePlayerPreview(long modelId, IReadOnlyDictionary<int, long> overrides, UnitAppearance appearance)
    {
        var ch = Row("SELECT face_item_id, default_custom_id, preview_cloth_pack_id FROM characters WHERE model_id=@m ORDER BY id LIMIT 1", ("@m", modelId));
        if (ch == null)
            return ResolveUnit(modelId, overrides, appearance);
        var look = LoadTotalCustom(L(ch[1])) ?? new UnitAppearance();
        if (appearance != null)
        {
            if (appearance.HairColorId > 0) look.HairColorId = appearance.HairColorId;
            if (appearance.SkinColorId > 0) look.SkinColorId = appearance.SkinColorId;
            if (appearance.FaceNormalMapId > 0) { look.FaceNormalMapId = appearance.FaceNormalMapId; look.FaceNormalMapWeight = appearance.FaceNormalMapWeight; }
            if (appearance.BodyNormalMapId > 0) { look.BodyNormalMapId = appearance.BodyNormalMapId; look.BodyNormalMapWeight = appearance.BodyNormalMapWeight; }
            // The creation appearance is complete (the customizer starts from this same default custom), so colours and
            // decals are taken as they are: a cleared decal or colour (0) must show exactly as it will be sent.
            look.DefaultHairColor = appearance.DefaultHairColor;
            look.TwoToneHairColor = appearance.TwoToneHairColor;
            look.TwoToneFirstWidth = appearance.TwoToneFirstWidth;
            look.TwoToneSecondWidth = appearance.TwoToneSecondWidth;
            look.LipColor = appearance.LipColor;
            look.LeftPupilColor = appearance.LeftPupilColor;
            look.RightPupilColor = appearance.RightPupilColor;
            look.EyebrowColor = appearance.EyebrowColor;
            look.DecoColor = appearance.DecoColor;
            for (var i = 0; i < look.DecalIds.Length && i < appearance.DecalIds.Length; i++)
            {
                look.DecalIds[i] = appearance.DecalIds[i];
                look.DecalWeights[i] = i < appearance.DecalWeights.Length ? appearance.DecalWeights[i] : 1;
            }
            if (appearance.Modifier.Length > 0) look.Modifier = appearance.Modifier;
            if (appearance.HairItemId > 0) look.HairItemId = appearance.HairItemId;
            if (appearance.FaceItemId > 0) look.FaceItemId = appearance.FaceItemId;
            if (appearance.BodyItemId > 0) look.BodyItemId = appearance.BodyItemId;
            if (appearance.HornItemId > 0) look.HornItemId = appearance.HornItemId;
            if (appearance.TailItemId > 0) look.TailItemId = appearance.TailItemId;
        }
        var equipment = new Dictionary<int, long>();
        FillBodyParts(modelId, look, L(ch[0]), equipment);
        var packId = L(ch[2]);
        if (packId > 0)
        {
            var cloth = Row(@"SELECT headgear_id, necklace_id, shirt_id, belt_id, pants_id, glove_id, shoes_id,
                bracelet_id, back_id, undershirt_id, underpants_id, cosplay_id, backpack_id, stabilizer_id
                FROM equip_pack_cloths WHERE id=@p", ("@p", packId));
            if (cloth != null)
            {
                var slots = PreviewPackSlots;
                for (var i = 0; i < slots.Length; i++)
                    if (L(cloth[i]) > 0)
                        equipment[slots[i]] = L(cloth[i]);
            }
        }
        if (overrides != null)
            foreach (var (slot, item) in overrides)
                equipment[slot] = item;
        return ResolveUnit(modelId, equipment, look);
    }

    /// <summary>
    /// An NPC template (npcs.id): model, cloth and weapon packs, total custom (or, when the template has none, the race's
    /// default custom; the server instead picks a random one), body parts and scale.
    /// </summary>
    public CharacterAssets ResolveNpc(long npcId)
    {
        var n = Row("SELECT model_id, equip_cloths_id, equip_weapons_id, total_custom_id, no_apply_total_custom, scale FROM npcs WHERE id=@n", ("@n", npcId));
        if (n == null)
            return new CharacterAssets { Kind = "missing", Warnings = { $"npc {npcId} not found" } };
        var modelId = L(n[0]);
        var equipment = new Dictionary<int, long>();
        UnitAppearance look = null;
        var ch = Row("SELECT face_item_id, default_custom_id FROM characters WHERE model_id=@m ORDER BY id LIMIT 1", ("@m", modelId));
        if (ch != null)
        {
            var customId = S(n[4]) == "t" ? 0 : L(n[3]);
            look = LoadTotalCustom(customId > 0 ? customId : L(ch[1]));
            FillBodyParts(modelId, look, L(ch[0]), equipment);
        }
        var cloths = L(n[1]);
        if (cloths > 0)
        {
            var c = Row("SELECT headgear_id, necklace_id, shirt_id, belt_id, pants_id, glove_id, shoes_id, bracelet_id, back_id, undershirt_id, underpants_id, cosplay_id FROM equip_pack_cloths WHERE id=@c", ("@c", cloths));
            if (c != null)
            {
                int[] slots = [0, 1, 2, 3, 4, 5, 6, 7, 8, SlotUndershirt, SlotUnderpants, SlotCosplay];
                for (var i = 0; i < slots.Length; i++)
                    if (L(c[i]) > 0)
                        equipment[slots[i]] = L(c[i]);
            }
        }
        var weapons = L(n[2]);
        if (weapons > 0)
        {
            var w = Row("SELECT mainhand_id, offhand_id, ranged_id, musical_id FROM equip_pack_weapons WHERE id=@w", ("@w", weapons));
            if (w != null)
                for (var i = 0; i < 4; i++)
                    if (L(w[i]) > 0)
                        equipment[SlotMainhand + i] = L(w[i]);
        }
        var result = ResolveUnit(modelId, equipment, look);
        result.Scale = n[5] is DBNull ? 1 : (float)D(n[5]);
        return result;
    }

    /// <summary>
    /// A unit as the client sees it: model id, equipment/body-image slots (enum_equip_slot id -> item template id,
    /// as in SCUnitState / the character's equipment container) and appearance (null = none).
    /// </summary>
    public CharacterAssets ResolveUnit(long modelId, IReadOnlyDictionary<int, long> equipment, UnitAppearance appearance)
    {
        var a = new CharacterAssets { ModelId = modelId };
        var model = Row("SELECT m.sub_type, m.sub_id, a.model_file FROM models m LEFT JOIN actor_models a ON a.id = m.sub_id AND m.sub_type = 'ActorModel' WHERE m.id=@m", ("@m", modelId));
        if (model == null)
        {
            a.Kind = "missing";
            a.Warnings.Add($"model {modelId} not in models");
            return a;
        }
        var subType = S(model[0]);
        if (subType != "ActorModel")
        {
            a.Kind = subType switch { "PrefabModel" => "prefab", "VehicleModel" => "vehicle", "ShipModel" => "ship", _ => subType };
            a.Warnings.Add($"model {modelId} is a {subType} (sub_id {L(model[1])}); not a skinned character");
            return a;
        }
        a.ModelFile = CdfReader.PakPath(S(model[2]));
        a.Kind = Row("SELECT 1 FROM characters WHERE model_id=@m LIMIT 1", ("@m", modelId)) != null ? "character" : "actor";
        if (a.ModelFile.Length == 0 || !_exists(a.ModelFile))
        {
            a.Warnings.Add($"model file '{S(model[2])}' missing");
            if (a.ModelFile.Length == 0)
                return a;
        }

        CdfFile cdf = null;
        if (a.ModelFile.EndsWith(".cdf", StringComparison.Ordinal))
        {
            var bytes = _read(a.ModelFile);
            if (bytes != null)
                cdf = CdfReader.Parse(bytes);
            if (cdf != null)
            {
                a.SkeletonPath = cdf.ModelPath;
                if (!_exists(cdf.ModelPath))
                    a.Warnings.Add($"skeleton {cdf.ModelPath} missing");
                else if (HasGeometry(cdf.ModelPath))
                    a.Parts.Add(new CharacterPart { Slot = "base", ModelPath = cdf.ModelPath, MaterialPath = MtlPath(cdf.ModelMaterialPath), IsSkinned = true, Source = "cdf model" });
                foreach (var att in cdf.Attachments)
                {
                    if (att.IsHidden || att.BindingPath.Length == 0)
                        continue;
                    AddFile(a, att.Name, att.BindingPath, att.MaterialPath, -1, 0, 0, "cdf attachment", att, null);
                }
            }
        }
        else
        {
            a.SkeletonPath = a.ModelFile;
            a.Parts.Add(new CharacterPart { Slot = "base", ModelPath = a.ModelFile, IsSkinned = true, Source = "actor chr" });
        }
        _skeleton = null;
        if (a.SkeletonPath.Length > 0)
        {
            var skelBytes = _read(a.SkeletonPath);
            if (skelBytes != null)
                _skeleton = ChrModelReader.ReadSkeleton(skelBytes);
            var cal = a.SkeletonPath[..^4] + ".cal";
            a.AnimationListPath = _exists(cal) ? cal : "";
        }

        // Equipment and body parts.
        var hairType = 0; // enum_hair_types chosen by a helmet/costume
        var hairTypeFromCostume = false;
        long hairItem = 0;
        if (equipment != null)
            foreach (var (slot, item) in equipment)
            {
                if (item <= 0 || slot < 0 || slot >= EquipSlotNames.Length)
                    continue;
                if (BodySlotType.ContainsKey(slot))
                {
                    if (slot == SlotHair)
                        hairItem = item; // added after the helmet is known
                    else
                        AddBodyPart(a, slot, item, cdf, 0);
                    continue;
                }
                if (slot is 1 or 9 or 10 or 11 or 12)
                    continue; // neck, ears, fingers: no visual
                if (slot is >= SlotMainhand and <= SlotMusical)
                {
                    AddWeapon(a, slot, item, cdf);
                    continue;
                }
                var t = AddArmor(a, slot, item, cdf);
                if (t >= 0 && (slot == SlotCosplay || (slot == SlotHead && !hairTypeFromCostume)))
                {
                    hairType = t;
                    hairTypeFromCostume = slot == SlotCosplay;
                }
            }
        if (hairItem > 0 && hairType != 1)
            AddBodyPart(a, SlotHair, hairItem, cdf, hairType);

        if (a.Parts.Exists(p => p.EquipSlot == SlotCosplay && p.IsSkinned))
        {
            if (CostumeHidesArmor)
                a.Parts.RemoveAll(p => p.EquipSlot is 2 or 3 or 4 or 5 or 6 or 7);
            if (CostumeHidesBody)
                a.Parts.RemoveAll(p => p.Slot == "body");
        }

        if (appearance != null)
            ApplyAppearance(a, appearance);
        else
            ApplyFaceMorphs(a, []);
        return a;
    }

    /// <summary>Loads a total_character_customs row (NPC customs, playable defaults). Null when missing.</summary>
    public UnitAppearance LoadTotalCustom(long id)
    {
        if (id <= 0)
            return null;
        var r = Row(@"SELECT hair_id, hair_color_id, skin_color_id, face_movable_decal_asset_id, face_fixed_decal_asset_0_id,
            face_fixed_decal_asset_1_id, face_fixed_decal_asset_2_id, face_fixed_decal_asset_3_id, face_fixed_decal_asset_4_id,
            face_fixed_decal_asset_5_id, face_movable_decal_weight, face_fixed_decal_asset_0_weight, face_fixed_decal_asset_1_weight,
            face_fixed_decal_asset_2_weight, face_fixed_decal_asset_3_weight, face_fixed_decal_asset_4_weight,
            face_fixed_decal_asset_5_weight, face_normal_map_id, face_normal_map_weight, body_normal_map_id, body_normal_map_weight,
            lip_color, left_pupil_color, right_pupil_color, eyebrow_color, deco_color, modifier, face_id, body_id, horn_id, tail_id,
            default_hair_color, two_tone_hair_color, two_tone_first_width, two_tone_second_width FROM total_character_customs WHERE id=@id", ("@id", id));
        if (r == null)
            return null;
        var u = new UnitAppearance
        {
            HairItemId = L(r[0]), HairColorId = L(r[1]), SkinColorId = L(r[2]),
            FaceNormalMapId = L(r[17]), FaceNormalMapWeight = (float)D(r[18]),
            BodyNormalMapId = L(r[19]), BodyNormalMapWeight = (float)D(r[20]),
            LipColor = (uint)L(r[21]), LeftPupilColor = (uint)L(r[22]), RightPupilColor = (uint)L(r[23]),
            EyebrowColor = (uint)L(r[24]), DecoColor = (uint)L(r[25]),
            Modifier = r[26] as byte[] ?? [],
            FaceItemId = L(r[27]), BodyItemId = L(r[28]), HornItemId = L(r[29]), TailItemId = L(r[30]),
            DefaultHairColor = (uint)L(r[31]),
            TwoToneHairColor = (uint)L(r[32]), TwoToneFirstWidth = (float)D(r[33]), TwoToneSecondWidth = (float)D(r[34]),
        };
        for (var i = 0; i < 7; i++)
        {
            u.DecalIds[i] = L(r[3 + i]);
            u.DecalWeights[i] = r[10 + i] is DBNull ? 1 : (float)D(r[10 + i]);
        }
        return u;
    }

    // ================================================================== body parts

    /// <summary>
    /// Body-part items of a playable model, as the server builds them for NPCs: face = custom face_id (when it exists
    /// for the model) else characters.face_item_id; body = custom body_id else the first slot-28 row; hair, horns,
    /// tail from the custom; tail and beard fall back to the first row of their slot (ferre tails, dwarf beards).
    /// </summary>
    private void FillBodyParts(long modelId, UnitAppearance look, long defaultFace, Dictionary<int, long> equipment)
    {
        long Pick(long custom, int slotType, bool firstRowFallback)
        {
            if (custom > 0 && Row("SELECT 1 FROM item_body_parts WHERE item_id=@i AND model_id=@m", ("@i", custom), ("@m", modelId)) != null)
                return custom;
            if (!firstRowFallback)
                return 0;
            var r = Row("SELECT item_id FROM item_body_parts WHERE model_id=@m AND slot_type_id=@s ORDER BY id LIMIT 1", ("@m", modelId), ("@s", slotType));
            return r == null ? 0 : L(r[0]);
        }
        var face = Pick(look?.FaceItemId ?? 0, 23, false);
        if (face == 0)
            face = Pick(defaultFace, 23, true);
        void Set(int slot, long item)
        {
            if (item > 0) equipment[slot] = item;
        }
        Set(SlotFace, face);
        Set(SlotBody, Pick(look?.BodyItemId ?? 0, 28, true));
        Set(SlotHair, Pick(look?.HairItemId ?? 0, 24, false));
        Set(SlotHorns, Pick(look?.HornItemId ?? 0, 26, false));
        Set(SlotTail, Pick(look?.TailItemId ?? 0, 27, true));
        Set(SlotBeard, Pick(0, 29, true));
    }

    private void AddBodyPart(CharacterAssets a, int slot, long item, CdfFile cdf, int hairType)
    {
        var r = Row("SELECT asset_id, asset_1_id, asset_2_id, asset_3_id, asset_4_id, hair_base, slot_type_id FROM item_body_parts WHERE item_id=@i AND model_id=@m ORDER BY id LIMIT 1",
            ("@i", item), ("@m", a.ModelId));
        if (r == null)
        {
            a.Warnings.Add($"body part item {item} (slot {EquipSlotNames[slot]}) has no item_body_parts row for model {a.ModelId}");
            return;
        }
        var asset = L(r[0]);
        if (asset == 0)
            return; // rows without an asset (e.g. ferre "beard") draw nothing
        if (slot == SlotHair && hairType >= 2 && hairType <= 5 && L(r[hairType - 1]) > 0)
            asset = L(r[hairType - 1]);
        if (slot == SlotHair && S(r[5]).Length > 0)
            a.HairBaseTexture = MtlReader.TexturePakPath(S(r[5]));
        AddAsset(a, slot, item, asset, cdf, $"item {item} {EquipSlotNames[slot]}");
        if (slot == SlotHair && a.Find("hair") is { MaterialPath.Length: 0 } hairPart && asset != L(r[0]))
        {
            // _hm_typeN helmet variants have no .mtl of their own: use the hair's.
            var m = HairMaterialBase(hairPart.ModelPath) + ".mtl";
            if (_exists(m))
                hairPart.MaterialPath = m;
        }
    }

    // ================================================================== armour and weapons

    /// <summary>Adds an armour visual; returns the hair type its asset asks for (helmets/costumes), -1 otherwise.</summary>
    private int AddArmor(CharacterAssets a, int slot, long item, CdfFile cdf)
    {
        if (slot == SlotBackpack)
        {
            var backpack = Row(@"SELECT b.asset_id, normal.anim_name, fast.anim_name, slow.anim_name, sliding.anim_name
                FROM item_backpacks b
                LEFT JOIN anim_actions normal ON normal.id=b.glider_anim_action_id
                LEFT JOIN anim_actions fast ON fast.id=b.glider_fast_anim_action_id
                LEFT JOIN anim_actions slow ON slow.id=b.glider_slow_anim_action_id
                LEFT JOIN anim_actions sliding ON sliding.id=b.glider_sliding_anim_action_id
                WHERE b.item_id=@i AND b.backpack_type_id=2", ("@i", item));
            if (backpack != null && L(backpack[0]) > 0)
            {
                a.GliderAnimation = S(backpack[1]);
                a.GliderFastAnimation = S(backpack[2]);
                a.GliderSlowAnimation = S(backpack[3]);
                a.GliderSlidingAnimation = S(backpack[4]);
                AddAsset(a, slot, item, L(backpack[0]), cdf, $"glider backpack item {item}");
                return -1;
            }
        }
        var ar = Row("SELECT asset_id, asset2_id, invisible_asset FROM item_armors WHERE item_id=@i", ("@i", item));
        if (ar == null)
        {
            // Backpacks and a few others are not armour; try the weapon chain before giving up.
            if (Row("SELECT 1 FROM item_weapons WHERE item_id=@i", ("@i", item)) != null)
            {
                AddWeapon(a, slot, item, cdf);
                return -1;
            }
            a.Warnings.Add($"item {item} in slot {EquipSlotNames[slot]} is neither armour nor weapon");
            return -1;
        }
        if (S(ar[2]) == "t")
            return -1;
        var variants = Rows(@"SELECT ia.id, ia.path, ia.detail FROM item_armor_assets x JOIN item_assets ia ON ia.id = x.asset_id
            WHERE x.armor_asset_id=@aa AND ia.model_id=@m ORDER BY x.id", ("@aa", L(ar[0])), ("@m", a.ModelId));
        if (variants.Count == 0)
        {
            a.Warnings.Add($"armour item {item} (armor asset {L(ar[0])}) has no variant for model {a.ModelId}: not drawn");
            return -1;
        }
        var hairType = -1;
        foreach (var v in variants)
        {
            var path = CdfReader.PakPath(S(v[1]));
            var detail = L(v[2]);
            var category = (detail >> 13) & 7;
            if (category is 3 or 7)
                hairType = (int)((detail >> 10) & 7);
            if (path.EndsWith(".dds", StringComparison.Ordinal))
            {
                a.BodyTextureOverlays.Add(path);
                continue;
            }
            AddFile(a, EquipSlotNames[slot], path, "", slot, item, L(v[0]), $"item {item} {EquipSlotNames[slot]}", null, null);
        }
        return hairType;
    }

    private void AddWeapon(CharacterAssets a, int slot, long item, CdfFile cdf)
    {
        var w = Row(@"SELECT w.asset_id, w.drawn_scale, ia.path, ia.detail, ia.attachment_offset_pos_x, ia.attachment_offset_pos_y,
            ia.attachment_offset_pos_z, ia.attachment_offset_rot_x, ia.attachment_offset_rot_y, ia.attachment_offset_rot_z
            FROM item_weapons w LEFT JOIN item_assets ia ON ia.id = w.asset_id WHERE w.item_id=@i", ("@i", item));
        if (w == null || w[2] is DBNull)
        {
            a.Warnings.Add($"weapon item {item} has no asset");
            return;
        }
        var detail = L(w[3]);
        var slotName = EquipSlotNames[slot];
        var att = cdf?.Find(slotName);
        var part = new CharacterPart
        {
            Slot = slotName,
            ModelPath = CdfReader.PakPath(S(w[2])),
            EquipSlot = slot,
            ItemId = item,
            AssetId = L(w[0]),
            Source = $"item {item} {slotName}",
            BoneName = att?.BoneName ?? (slot == SlotOffhand ? "item_hand_l" : "item_hand_r"),
            Scale = (att?.Scale ?? 1) * (w[1] is DBNull ? 1 : (float)D(w[1])),
        };
        var equipPos = (int)((detail >> 10) & 7);
        if (equipPos > 0 && equipPos < EquipPosBones.Length)
            part.BoneName = EquipPosBones[equipPos];
        var sheath = (int)((detail >> 1) & 7);
        if (sheath > 0 && sheath < SheathBones.Length)
            part.SheathBoneName = sheath == 1 ? (slot == SlotOffhand ? "item_side_l" : "item_side_r")
                : sheath == 2 ? (slot == SlotOffhand ? "item_back_l" : "item_back_r") : SheathBones[sheath];
        // Item offset: position metres, rotation Euler degrees (X, then Y, then Z; order inferred).
        var rot = Matrix4x4.CreateRotationX(Deg(w[7])) * Matrix4x4.CreateRotationY(Deg(w[8])) * Matrix4x4.CreateRotationZ(Deg(w[9]));
        part.BoneOffset = rot * Matrix4x4.CreateTranslation((float)D(w[4]), (float)D(w[5]), (float)D(w[6])) * AttachmentRelative(att, part.BoneName);
        if (part.ModelPath.EndsWith(".cdf", StringComparison.Ordinal))
            FollowWrapper(a, part);
        if (!_exists(part.ModelPath))
            a.Warnings.Add($"{part.ModelPath} missing");
        a.Parts.Add(part);
    }

    // ================================================================== files

    private void AddAsset(CharacterAssets a, int slot, long item, long assetId, CdfFile cdf, string source)
    {
        var r = Row("SELECT path FROM item_assets WHERE id=@a", ("@a", assetId));
        if (r == null)
        {
            a.Warnings.Add($"item_assets {assetId} missing ({source})");
            return;
        }
        AddFile(a, EquipSlotNames[slot], CdfReader.PakPath(S(r[0])), "", slot, item, assetId, source, null, cdf);
    }

    /// <summary>Adds a .chr/.cgf, following wrapper .cdf files (Model File + Material, plus their own skin attachments).</summary>
    private void AddFile(CharacterAssets a, string slot, string path, string material, int equipSlot, long item, long asset,
        string source, CdfAttachment placement, CdfFile unitCdf)
    {
        if (path.Length == 0)
            return;
        var part = new CharacterPart
        {
            Slot = slot, ModelPath = path, MaterialPath = MtlPath(material), EquipSlot = equipSlot, ItemId = item, AssetId = asset, Source = source,
        };
        if (path.EndsWith(".cdf", StringComparison.Ordinal))
        {
            var extra = FollowWrapper(a, part);
            foreach (var e in extra)
                a.Parts.Add(e);
        }
        part.IsSkinned = part.ModelPath.EndsWith(".chr", StringComparison.Ordinal);
        if (equipSlot >= 0)
            a.Parts.RemoveAll(p => p.EquipSlot < 0 && p.Slot == slot); // an item replaces the CDF's authored piece
        placement ??= unitCdf?.Find(slot);
        if (!part.IsSkinned)
        {
            part.BoneName = placement?.BoneName ?? "";
            part.Scale = placement?.Scale ?? 1;
            if (part.BoneName.Length == 0 && slot is "back" or "backpack")
                part.BoneName = "item_back_c";
            part.BoneOffset = AttachmentRelative(placement, part.BoneName);
        }
        if (!_exists(part.ModelPath))
            a.Warnings.Add($"{part.ModelPath} missing ({source})");
        a.Parts.Add(part);
    }

    /// <summary>Replaces a wrapper .cdf by its Model File/Material; returns parts for its extra skin attachments.</summary>
    private List<CharacterPart> FollowWrapper(CharacterAssets a, CharacterPart part)
    {
        var extra = new List<CharacterPart>();
        var bytes = _read(part.ModelPath);
        if (bytes == null)
        {
            a.Warnings.Add($"{part.ModelPath} missing ({part.Source})");
            return extra;
        }
        var cdf = CdfReader.Parse(bytes);
        var wrapper = part.ModelPath;
        part.ModelPath = cdf.ModelPath;
        if (part.MaterialPath.Length == 0)
            part.MaterialPath = MtlPath(cdf.ModelMaterialPath);
        part.Source += $" via {Path.GetFileName(wrapper)}";
        foreach (var att in cdf.Attachments)
            if (!att.IsHidden && att.IsSkin && att.BindingPath.EndsWith(".chr", StringComparison.Ordinal))
                extra.Add(new CharacterPart
                {
                    Slot = part.Slot, ModelPath = att.BindingPath, MaterialPath = MtlPath(att.MaterialPath), IsSkinned = true,
                    EquipSlot = part.EquipSlot, ItemId = part.ItemId, AssetId = part.AssetId, Source = part.Source + " attachment " + att.Name,
                });
        return extra;
    }

    /// <summary>
    /// Bone-relative placement of a CA_BONE attachment. With AlignBoneAttachment set (every humanoid base CDF writes
    /// "Bone") the mesh takes the bone frame as is; otherwise Rotation/Position are the model-space bind placement and
    /// the relative transform is placement * inverse(bone bind). Identity when unknown.
    /// </summary>
    private Matrix4x4 AttachmentRelative(CdfAttachment att, string boneName)
    {
        if (att == null || !att.HasPlacement || att.AlignBoneAttachment.Length > 0 || _skeleton == null)
            return Matrix4x4.Identity;
        var b = _skeleton.IndexOf(boneName);
        if (b < 0)
            return Matrix4x4.Identity;
        var abs = Matrix4x4.CreateFromQuaternion(att.Rotation) * Matrix4x4.CreateTranslation(att.Position);
        return abs * _skeleton.Bones[b].InverseBindWorld;
    }

    /// <summary>Material references in CDFs sometimes omit ".mtl".</summary>
    private static string MtlPath(string p)
    {
        if (string.IsNullOrEmpty(p))
            return "";
        return p.EndsWith(".mtl", StringComparison.Ordinal) ? p : p + ".mtl";
    }

    /// <summary>Humanoid base skeletons (x_base.chr) carry a 3-vertex placeholder mesh; monsters' base .chr is the body.</summary>
    private bool HasGeometry(string chrPath)
    {
        var bytes = _read(chrPath);
        if (bytes == null)
            return false;
        try
        {
            var chunks = CgfModelReader.ReadChunkTable(bytes, CgfModelReader.U32(bytes, 12), CgfModelReader.I32(bytes, 16));
            foreach (var c in chunks)
                if (c.Type == CgfModelReader.ChunkMesh)
                {
                    var m = CgfModelReader.ReadMesh(bytes, c);
                    if (m != null && m.NumVerts > 4)
                        return true;
                }
        }
        catch (InvalidDataException)
        {
        }
        return false;
    }

    // ================================================================== appearance

    private void ApplyAppearance(CharacterAssets a, UnitAppearance u)
    {
        // Hair colour: a full replacement material for this hair, or a palette colour for use_pallet hairs.
        var hair = a.Find("hair");
        if (hair != null)
        {
            a.HairUsesPalette = S(Row("SELECT use_pallet FROM customizing_item_assets WHERE item_id=@i AND model_id=@m", ("@i", hair.ItemId), ("@m", a.ModelId))?[0]) == "t";
            a.HairColor = u.DefaultHairColor;
            if (a.HairUsesPalette)
                a.HairBaseColor = u.DefaultHairColor;
            a.HairSecondColor = u.TwoToneHairColor;
            a.HairWidth1 = u.TwoToneFirstWidth;
            a.HairWidth2 = u.TwoToneSecondWidth;
        }
        if (u.HairColorId > 0 && hair != null && !a.HairUsesPalette)
        {
            var c = Row("SELECT material, asset_id, item_id, model_id, hair_base_color_r, hair_base_color_g, hair_base_color_b FROM customizing_item_asset_colors WHERE id=@c", ("@c", u.HairColorId));
            if (c != null)
            {
                a.HairBaseColor = 0xFF000000u | ((uint)L(c[6]) << 16) | ((uint)L(c[5]) << 8) | (uint)L(c[4]);
                var mtl = MtlPath(CdfReader.PakPath(S(c[0])));
                var matches = L(c[3]) == a.ModelId && (L(c[2]) == hair.ItemId || L(c[1]) == hair.AssetId || SameFolder(mtl, hair.ModelPath));
                // Rows often name another hair (372) or model (178): the colour is then a palette index, the _hcNN
                // suffix, applied to the worn hair (inferred; the file exists for most hairs).
                var index = System.Text.RegularExpressions.Regex.Match(mtl, @"_hc\d+\.mtl$");
                var own = index.Success ? HairMaterialBase(hair.ModelPath) + index.Value : "";
                if (matches && _exists(mtl))
                    hair.MaterialPath = mtl;
                else if (own.Length > 0 && _exists(own))
                    hair.MaterialPath = own;
                else
                    a.Warnings.Add($"hair colour {u.HairColorId} ({mtl}) has no variant for {hair.ModelPath}; default hair material kept");
            }
        }

        // Skin colour.
        if (u.SkinColorId > 0)
        {
            var s = Row(@"SELECT bright_skin_color_r, bright_skin_color_g, bright_skin_color_b, middle_skin_color_r, middle_skin_color_g,
                middle_skin_color_b, diffuse_color_r, diffuse_color_g, diffuse_color_b, specular_color_r, specular_color_g, specular_color_b,
                glossness, specular_level, custom_postfix FROM skin_colors WHERE id=@s", ("@s", u.SkinColorId));
            if (s != null)
            {
                Vector3 V(int i) => new((float)D(s[i]) / 255f, (float)D(s[i + 1]) / 255f, (float)D(s[i + 2]) / 255f);
                a.SkinColor = new SkinColorInfo
                {
                    Id = u.SkinColorId, BrightSkinColor = V(0), MiddleSkinColor = V(3), Diffuse = V(6), Specular = V(9),
                    Glossiness = (float)D(s[12]), SpecularLevel = (float)D(s[13]), CustomPostfix = S(s[14]),
                };
                if (a.SkinColor.CustomPostfix.Length > 0)
                    foreach (var p in a.Parts)
                    {
                        if (p.Slot is not ("body" or "face" or "tail"))
                            continue;
                        var baseMtl = p.MaterialPath.Length > 0 ? p.MaterialPath : p.ModelPath[..^4] + ".mtl";
                        var variant = baseMtl[..^4] + "_" + a.SkinColor.CustomPostfix.ToLowerInvariant() + ".mtl";
                        if (_exists(variant))
                            p.MaterialPath = variant;
                    }
            }
        }

        // Face maps and decals.
        if (u.FaceNormalMapId > 0)
        {
            var r = Row("SELECT normal FROM face_normal_maps WHERE id=@i", ("@i", u.FaceNormalMapId));
            if (r != null)
            {
                a.FaceNormalMap = MtlReader.TexturePakPath(S(r[0]));
                a.FaceNormalMapWeight = u.FaceNormalMapWeight;
            }
        }
        if (u.BodyNormalMapId > 0)
        {
            var r = Row("SELECT normal FROM body_normal_maps WHERE id=@i", ("@i", u.BodyNormalMapId));
            if (r != null)
                a.BodyNormalMap = MtlReader.TexturePakPath(S(r[0]));
        }
        string[] kinds = ["scar", "tattoo", "makeup", "eyebrow", "deco", "pupil_left", "pupil_right"];
        // Makeup has no colour of its own (the customizer has none); the lip colour tints the lips (LipColor).
        uint[] colors = [0, 0, 0, u.EyebrowColor, u.DecoColor, u.LeftPupilColor, u.RightPupilColor];
        a.LipColor = u.LipColor;
        for (var i = 0; i < 7; i++)
        {
            if (u.DecalIds[i] <= 0)
                continue;
            var r = Row("SELECT asset_path, defaultX, defaultY, odd_eye_info FROM face_decal_assets WHERE id=@i", ("@i", u.DecalIds[i]));
            if (r != null && S(r[0]).Length > 0)
            {
                var decal = new FaceDecal { Kind = kinds[i], TexturePath = MtlReader.TexturePakPath(S(r[0])), Weight = u.DecalWeights[i],
                    Color = colors[i], X = (int)L(r[1]), Y = (int)L(r[2]) };
                // Pupils: {"type":"odd_eye","left":{"x":14,"y":320},"right":{"x":5,"y":247}} (defaultX/Y are 0).
                var side = kinds[i] == "pupil_left" ? "left" : kinds[i] == "pupil_right" ? "right" : null;
                var eye = side == null ? null : System.Text.RegularExpressions.Regex.Match(S(r[3]),
                    "\"" + side + @"""\s*:\s*\{\s*""x""\s*:\s*(-?\d+)\s*,\s*""y""\s*:\s*(-?\d+)");
                if (eye is { Success: true })
                {
                    decal.X = int.Parse(eye.Groups[1].Value, CultureInfo.InvariantCulture);
                    decal.Y = int.Parse(eye.Groups[2].Value, CultureInfo.InvariantCulture);
                }
                a.FaceDecals.Add(decal);
            }
        }

        ApplyFaceMorphs(a, u.Modifier);
    }

    /// <summary>Face morphs: the race's Apply="always" morphs plus the modifier sliders (only for playable-race faces).</summary>
    private void ApplyFaceMorphs(CharacterAssets a, byte[] modifier)
    {
        if (a.Kind != "character" || a.Find("face") == null || a.ModelFile.Length == 0)
            return;
        var targets = FaceTargetsPath(a.ModelFile);
        var bytes = targets.Length > 0 ? _read(targets) : null;
        if (bytes == null)
        {
            a.Warnings.Add($"face targets for {a.ModelFile} not found");
            return;
        }
        a.FaceTargetsPath = targets;
        a.FaceModifier = modifier ?? [];
        foreach (var (name, w) in FaceMorphWeights(bytes, modifier ?? []))
            a.FaceMorphs[name] = w;
    }

    /// <summary>"game/objects/characters/nuian/male/nude/nu_m.cdf" -> "game/objects/characters/nuian/male/face/nu_m_targets.xml".</summary>
    public string FaceTargetsPath(string modelFile)
    {
        var slash = modelFile.LastIndexOf('/');
        var folder = slash > 0 ? modelFile[..slash] : "";
        var gender = folder.LastIndexOf('/');
        if (gender < 0)
            return "";
        var prefix = Path.GetFileNameWithoutExtension(modelFile);
        var p = folder[..gender] + "/face/" + prefix + "_targets.xml";
        return _exists(p) ? p : "";
    }

    /// <summary>
    /// Face morph weights from a *_targets.xml and the 128-byte modifier. Byte i (signed, percent) drives Target Idx i
    /// (verified: every custom_face_presets row of a category only touches that category's indices; 99.4% of values lie
    /// inside the target's Min..Max). Apply="normal": Morph weight v/100*Scale. Apply="minmax": v&lt;0 drives MinMorph
    /// at -v/100*Scale, v&gt;0 drives MaxMorph at v/100*Scale (weight rule inferred). Apply="always": the race morph of
    /// borrowed face meshes (dwarf_Morph, elf_Morph, hariharan_morph, fairy_Morph, ferre Male/Female) at weight Scale,
    /// whatever the modifier says.
    /// </summary>
    public static Dictionary<string, float> FaceMorphWeights(byte[] targetsXml, byte[] modifier)
    {
        var result = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        using var ms = new MemoryStream(targetsXml);
        var doc = new XmlDocument();
        doc.Load(ms);
        foreach (XmlElement t in doc.GetElementsByTagName("Target"))
        {
            var scale = float.TryParse(t.GetAttribute("Scale"), NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? s : 1;
            if (t.GetAttribute("Apply").Equals("always", StringComparison.OrdinalIgnoreCase))
            {
                if (t.GetAttribute("Morph") is { Length: > 0 } always)
                    result[always] = scale;
                continue;
            }
            if (!int.TryParse(t.GetAttribute("Idx"), out var idx) || idx < 0 || idx >= modifier.Length)
                continue;
            var v = (sbyte)modifier[idx];
            if (v == 0)
                continue;
            if (t.GetAttribute("Apply").Equals("minmax", StringComparison.OrdinalIgnoreCase))
            {
                var name = v < 0 ? t.GetAttribute("MinMorph") : t.GetAttribute("MaxMorph");
                if (name.Length > 0)
                    result[name] = Math.Abs(v) / 100f * scale;
            }
            else if (t.GetAttribute("Morph") is { Length: > 0 } morph)
                result[morph] = v / 100f * scale;
        }
        return result;
    }

    // ================================================================== animation helpers

    /// <summary>Basic locomotion clips of a skeleton's .cal (players, humanoid NPCs and monsters use the same names).</summary>
    public static (string Idle, string Walk, string Run) LocomotionClips(CalFile cal, Func<string, byte[]> read)
    {
        string Resolve(params string[] names)
        {
            foreach (var n in names)
            {
                var p = cal.Find(n);
                if (p == null)
                    continue;
                if (!p.EndsWith(".lmg", StringComparison.Ordinal))
                    return p;
                var bytes = read(p);
                var fwd = bytes == null ? null : LmgReader.Parse(bytes).ForwardClip;
                var caf = fwd == null ? null : cal.Find(fwd);
                if (caf != null)
                    return caf;
            }
            return null;
        }
        return (Resolve("fist_ba_relaxed_idle", "fist_ba_combat_idle", "ba_relaxed_idle"),
            Resolve("_FIST_MO_NORMAL_WALK", "_NORMAL_WALK", "fist_mo_normal_walk_f"),
            Resolve("_FIST_MO_NORMAL_RUN", "_NORMAL_RUN", "fist_mo_normal_run_f", "_FIST_MO_COMBAT_RUN", "_COMBAT_RUN"));
    }

    // ================================================================== db helpers

    /// <summary>".../nu_m_hair010/nu_m_hair010_hm_type1.chr" -> ".../nu_m_hair010/nu_m_hair010" (folder + folder name).</summary>
    private static string HairMaterialBase(string hairPath)
    {
        var slash = hairPath.LastIndexOf('/');
        if (slash < 0)
            return hairPath;
        var folder = hairPath[..slash];
        return folder + "/" + folder[(folder.LastIndexOf('/') + 1)..];
    }

    private static bool SameFolder(string a, string b)
    {
        int i = a.LastIndexOf('/'), j = b.LastIndexOf('/');
        return i > 0 && j > 0 && a[..i] == b[..j];
    }

    private static float Deg(object o) => (float)(D(o) * Math.PI / 180);

    private object[] Row(string sql, params (string Name, object Value)[] args)
    {
        var rows = Rows(sql, args);
        return rows.Count > 0 ? rows[0] : null;
    }

    private List<object[]> Rows(string sql, params (string Name, object Value)[] args)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = name;
            p.Value = value ?? DBNull.Value;
            cmd.Parameters.Add(p);
        }
        var list = new List<object[]>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var row = new object[r.FieldCount];
            r.GetValues(row);
            list.Add(row);
        }
        return list;
    }

    private static long L(object o) => o is null or DBNull ? 0 : Convert.ToInt64(o, CultureInfo.InvariantCulture);
    private static double D(object o) => o is null or DBNull ? 0 : Convert.ToDouble(o, CultureInfo.InvariantCulture);
    private static string S(object o) => o is null or DBNull ? "" : Convert.ToString(o, CultureInfo.InvariantCulture) ?? "";
}
