#nullable enable
using System.Globalization;
using System.Xml;
using Microsoft.Data.Sqlite;

namespace AAEmu.GodotViewer.Ui.X2.Scripting;

/// <summary>One selectable customization entry: the id the appearance stores plus what the selection grid shows.</summary>
internal sealed record LoginCustomizingItem(long Id, string IconPath, bool IsNew = false, bool TwoTone = false, bool UsePallet = false);

/// <summary>
/// One face slider of a race/gender's *_targets.xml. <see cref="Index"/> is the Target Idx, i.e. the byte of the 128-byte
/// appearance modifier it reads (signed percent, see <see cref="CharacterAssetResolver.FaceMorphWeights"/>);
/// <see cref="Part"/> is the customizer part (1 eyes, 2 nose, 3 mouth, 4 face shape incl. ears and contour, 0 hidden).
/// </summary>
internal sealed record LoginFaceTarget(int Index, string Name, int Part, double Min, double Max);

/// <summary>A custom_face_presets row: the modifier bytes it writes and its icon.</summary>
internal sealed record LoginFacePreset(long Id, byte[] Modifier, string IconPath);

/// <summary>A hair colour choice (customizing_item_asset_colors) of one hair item.</summary>
internal sealed record LoginHairColor(long Id, uint BaseColor);

/// <summary>Read-only customization choices from compact.sqlite3, keyed by playable model id.</summary>
internal sealed class LoginCustomizationCatalog
{
    /// <summary>Customizer parts (X2Customizer PRESET_* constants of customizing_new/common.lua).</summary>
    public const int PartEye = 1, PartNose = 2, PartMouth = 3, PartShape = 4, PartTotal = 5;

    private readonly string? _path;
    private readonly Dictionary<(long, string), long[]> _cache = [];
    private readonly Dictionary<(long, string), LoginCustomizingItem[]> _items = [];
    private readonly Dictionary<(long, long), LoginCustomizingItem[]> _hornColors = [];
    private readonly Dictionary<(long, long), LoginHairColor[]> _hairColors = [];
    private readonly Dictionary<(long, int), LoginFacePreset[]> _facePresets = [];
    private readonly Dictionary<(long, int), int[]> _presetIndices = [];
    private readonly Dictionary<long, LoginFaceTarget[]> _targets = [];
    private readonly Dictionary<long, LoginCustomizingItem[]> _totalPresets = [];
    private readonly Dictionary<long, LoginCustomizingItem[]> _previewCloths = [];
    private readonly Dictionary<int, string> _skillIconCache = [];
    public LoginCustomizationCatalog(string? path) => _path = File.Exists(path) ? path : null;

    public long Model(int race, int gender) => Scalar("SELECT model_id FROM characters WHERE char_race_id=@a AND char_gender_id=@b ORDER BY id LIMIT 1", race, gender);
    public uint StartingZone(int race, int gender) => checked((uint)Math.Max(0,
        Scalar("SELECT starting_zone_id FROM characters WHERE char_race_id=@a AND char_gender_id=@b ORDER BY id LIMIT 1", race, gender)));
    public long[] Values(long model, string kind) => _cache.TryGetValue((model, kind), out var v) ? v : _cache[(model, kind)] = Load(model, kind);
    public long Pick(long model, string kind, int oneBased) { var v = Values(model, kind); return oneBased >= 1 && oneBased <= v.Length ? v[oneBased - 1] : 0; }

    /// <summary>1-based position of <paramref name="id"/> in a list, 0 when it is not there (nothing selected).</summary>
    public int IndexOf(long model, string kind, long id) => id <= 0 ? 0 : Array.IndexOf(Values(model, kind), id) + 1;

    /// <summary>The selection grid entries of a list kind (same order as <see cref="Values"/>).</summary>
    public LoginCustomizingItem[] Items(long model, string kind)
    {
        if (_items.TryGetValue((model, kind), out var cached)) return cached;
        if (_path == null || model == 0) return _items[(model, kind)] = [];
        var sql = kind switch
        {
            // hair, horn and tail styles: customizing_item_assets (category 1 hair, 2 horn, 3 tail) in display order; the icon is the item's
            "hair" or "horn" or "tail" => $"""
                SELECT a.item_id, COALESCE(i.filename,''), a.is_new, a.two_tone, a.use_pallet FROM customizing_item_assets a
                LEFT JOIN items x ON x.id=a.item_id LEFT JOIN icons i ON i.id=x.icon_id
                WHERE a.model_id=$m AND a.category_id={(kind == "hair" ? 1 : kind == "horn" ? 2 : 3)} AND a.item_id>0 ORDER BY a.display_order,a.id
                """,
            "skin" => "SELECT a.id, COALESCE(i.filename,''), 'f','f','f' FROM skin_colors a LEFT JOIN icons i ON i.id=a.icon_id WHERE a.model_id=$m AND a.npc_only='f' ORDER BY a.display_order,a.id",
            "face_normal" or "body_normal" => $"SELECT a.id, COALESCE(i.filename,''), a.is_new,'f','f' FROM {kind}_maps a LEFT JOIN icons i ON i.id=a.icon_id WHERE a.model_id=$m AND a.npc_only='f' ORDER BY a.display_order,a.id",
            "scar" => DecalSql(1), "tattoo" => DecalSql(2), "makeup" => DecalSql(3), "eyebrow" => DecalSql(4), "deco" => DecalSql(5), "pupil" => DecalSql(6),
            _ => null,
        };
        var list = new List<LoginCustomizingItem>();
        if (sql != null)
        {
            using var db = Open(); using var q = db.CreateCommand(); q.CommandText = sql; q.Parameters.AddWithValue("$m", model);
            using var r = q.ExecuteReader();
            while (r.Read()) list.Add(new LoginCustomizingItem(r.GetInt64(0), IconPath(r.GetString(1)), Flag(r, 2), Flag(r, 3), Flag(r, 4)));
        }
        return _items[(model, kind)] = list.ToArray();
    }

    public LoginCustomizingItem? Item(long model, string kind, int oneBased)
    {
        var items = Items(model, kind);
        return oneBased >= 1 && oneBased <= items.Length ? items[oneBased - 1] : null;
    }

    /// <summary>Hair colours of one hair item (customizing_item_asset_colors category 1, hc01..hcNN in display order).</summary>
    public LoginHairColor[] HairColors(long model, long hairItemId)
    {
        if (_hairColors.TryGetValue((model, hairItemId), out var cached)) return cached;
        var list = new List<LoginHairColor>();
        if (_path != null && hairItemId > 0)
        {
            using var db = Open(); using var q = db.CreateCommand();
            q.CommandText = """
                SELECT id, COALESCE(hair_base_color_r,0), COALESCE(hair_base_color_g,0), COALESCE(hair_base_color_b,0)
                FROM customizing_item_asset_colors WHERE model_id=$m AND category_id=1 AND item_id=$i ORDER BY display_order,id
                """;
            q.Parameters.AddWithValue("$m", model); q.Parameters.AddWithValue("$i", hairItemId);
            using var r = q.ExecuteReader();
            while (r.Read())
                list.Add(new LoginHairColor(r.GetInt64(0), Abgr(I(r, 1), I(r, 2), I(r, 3))));
        }
        return _hairColors[(model, hairItemId)] = list.ToArray();
    }

    /// <summary>The base colour of a hair colour row (any model), 0 when unknown.</summary>
    public uint HairColorBase(long colorId)
    {
        if (_path == null || colorId <= 0) return 0;
        using var db = Open(); using var q = db.CreateCommand();
        q.CommandText = "SELECT hair_base_color_r, hair_base_color_g, hair_base_color_b FROM customizing_item_asset_colors WHERE id=$i";
        q.Parameters.AddWithValue("$i", colorId);
        using var r = q.ExecuteReader();
        return r.Read() && !r.IsDBNull(0) ? Abgr(I(r, 0), I(r, 1), I(r, 2)) : 0;
    }

    /// <summary>Horn colours of one horn item (customizing_item_asset_colors category 2, with icons).</summary>
    public LoginCustomizingItem[] HornColors(long model, long hornItemId)
    {
        if (_hornColors.TryGetValue((model, hornItemId), out var cached)) return cached;
        var list = new List<LoginCustomizingItem>();
        if (_path != null && hornItemId > 0)
        {
            using var db = Open(); using var q = db.CreateCommand();
            q.CommandText = """
                SELECT a.id, COALESCE(i.filename,'') FROM customizing_item_asset_colors a LEFT JOIN icons i ON i.id=a.icon_id
                WHERE a.model_id=$m AND a.category_id=2 AND a.item_id=$i ORDER BY a.display_order,a.id
                """;
            q.Parameters.AddWithValue("$m", model); q.Parameters.AddWithValue("$i", hornItemId);
            using var r = q.ExecuteReader();
            while (r.Read()) list.Add(new LoginCustomizingItem(r.GetInt64(0), IconPath(r.GetString(1))));
        }
        return _hornColors[(model, hornItemId)] = list.ToArray();
    }

    // ------------------------------------------------------------------ face presets and sliders

    /// <summary>
    /// The race/gender's face sliders from its *_targets.xml (the same file the renderer morphs the face with).
    /// Category eyes/nose/mouth map to parts 1-3; shape, ear and contour are part 4 (customizing_new/modifier.lua lists the
    /// ear and FS_Type sliders under the face-shape part, and custom_face_presets of type 4 write those bytes); "none"
    /// (race morphs and retired sliders) is 0 and never shown.
    /// </summary>
    public LoginFaceTarget[] FaceTargets(long model)
    {
        if (_targets.TryGetValue(model, out var cached)) return cached;
        var list = new List<LoginFaceTarget>();
        var xml = FaceTargetsXml(model);
        if (xml != null)
        {
            var doc = new XmlDocument();
            using (var ms = new MemoryStream(xml)) doc.Load(ms);
            var seen = new HashSet<int>();
            foreach (XmlElement t in doc.GetElementsByTagName("Target"))
            {
                if (!int.TryParse(t.GetAttribute("Idx"), out var index) || index is < 0 or >= 128) continue;
                if (t.GetAttribute("Apply").Equals("always", StringComparison.OrdinalIgnoreCase) || !seen.Add(index)) continue;
                var part = t.GetAttribute("Category").Trim().ToLowerInvariant() switch
                {
                    "eyes" => PartEye, "nose" => PartNose, "mouth" => PartMouth, "shape" or "ear" or "contour" => PartShape, _ => 0,
                };
                list.Add(new LoginFaceTarget(index, t.GetAttribute("Name").Trim(), part, Number(t.GetAttribute("Min"), -100), Number(t.GetAttribute("Max"), 100)));
            }
        }
        return _targets[model] = list.ToArray();
    }

    public LoginFaceTarget[] FaceTargets(long model, int part) => part == 0 ? [] : FaceTargets(model).Where(t => t.Part == part).ToArray();
    public LoginFaceTarget? FaceTarget(long model, int index) => FaceTargets(model).FirstOrDefault(t => t.Index == index);

    /// <summary>custom_face_presets of one part (1 eyes, 2 nose, 3 mouth, 4 shape) in display order.</summary>
    public LoginFacePreset[] FacePresets(long model, int part)
    {
        if (_facePresets.TryGetValue((model, part), out var cached)) return cached;
        var list = new List<LoginFacePreset>();
        if (_path != null && model > 0 && part is >= PartEye and <= PartShape)
        {
            using var db = Open(); using var q = db.CreateCommand();
            q.CommandText = """
                SELECT p.id, p.modifier, COALESCE(i.filename,'') FROM custom_face_presets p LEFT JOIN icons i ON i.id=p.icon_id
                WHERE p.model_id=$m AND p.face_morph_type_id=$t ORDER BY p.display_order,p.id
                """;
            q.Parameters.AddWithValue("$m", model); q.Parameters.AddWithValue("$t", part);
            using var r = q.ExecuteReader();
            while (r.Read())
                list.Add(new LoginFacePreset(r.GetInt64(0), r.IsDBNull(1) ? [] : (byte[])r.GetValue(1), IconPath(r.GetString(2))));
        }
        return _facePresets[(model, part)] = list.ToArray();
    }

    /// <summary>
    /// The modifier bytes a face preset of this part owns: its sliders plus every byte any preset of the part sets. Applying
    /// a preset copies exactly these bytes, so the part is replaced as a whole and the other parts keep their values.
    /// </summary>
    public int[] FacePresetIndices(long model, int part)
    {
        if (_presetIndices.TryGetValue((model, part), out var cached)) return cached;
        var set = new SortedSet<int>(FaceTargets(model, part).Select(t => t.Index));
        foreach (var p in FacePresets(model, part))
            for (var i = 0; i < p.Modifier.Length && i < 128; i++)
                if (p.Modifier[i] != 0) set.Add(i);
        return _presetIndices[(model, part)] = set.ToArray();
    }

    /// <summary>Copies face preset <paramref name="oneBased"/>'s bytes of its part into a 128-byte modifier.</summary>
    public bool ApplyFacePreset(long model, int part, int oneBased, byte[] modifier)
    {
        var presets = FacePresets(model, part);
        if (oneBased < 1 || oneBased > presets.Length || modifier.Length < 128) return false;
        var source = presets[oneBased - 1].Modifier;
        foreach (var i in FacePresetIndices(model, part))
            modifier[i] = i < source.Length ? source[i] : (byte)0;
        return true;
    }

    /// <summary>The face preset whose part bytes equal the modifier's (1-based), 0 when none does.</summary>
    public int MatchingFacePreset(long model, int part, byte[] modifier)
    {
        var presets = FacePresets(model, part);
        var indices = FacePresetIndices(model, part);
        for (var p = 0; p < presets.Length; p++)
        {
            var source = presets[p].Modifier;
            if (indices.All(i => (i < modifier.Length ? modifier[i] : 0) == (i < source.Length ? source[i] : 0))) return p + 1;
        }
        return 0;
    }

    /// <summary>Whole-look presets (total_character_customs owned by pc, with an icon) shown under Features.</summary>
    public LoginCustomizingItem[] TotalPresets(long model)
    {
        if (_totalPresets.TryGetValue(model, out var cached)) return cached;
        var list = new List<LoginCustomizingItem>();
        if (_path != null && model > 0)
        {
            using var db = Open(); using var q = db.CreateCommand();
            q.CommandText = """
                SELECT t.id, COALESCE(i.filename,'') FROM total_character_customs t JOIN icons i ON i.id=t.icon_id
                WHERE t.model_id=$m AND t.owner_type_id=1 AND t.npcOnly='f' AND t.display_order>0 ORDER BY t.display_order,t.id
                """;
            q.Parameters.AddWithValue("$m", model);
            using var r = q.ExecuteReader();
            while (r.Read()) list.Add(new LoginCustomizingItem(r.GetInt64(0), IconPath(r.GetString(1))));
        }
        return _totalPresets[model] = list.ToArray();
    }

    public int DefaultTotalPreset(long model)
    {
        var id = Scalar("SELECT default_custom_id FROM characters WHERE model_id=@a ORDER BY id LIMIT 1", model, 0);
        return Array.FindIndex(TotalPresets(model), p => p.Id == id) + 1;
    }

    /// <summary>The whole look of total preset <paramref name="oneBased"/>, null when out of range.</summary>
    public LoginCharacterAppearance? TotalPreset(long model, int oneBased)
    {
        var presets = TotalPresets(model);
        return oneBased >= 1 && oneBased <= presets.Length ? LoadAppearance(model, presets[oneBased - 1].Id) : null;
    }

    // ------------------------------------------------------------------ preview clothes

    /// <summary>Clothes the customization preview can try on (character_preview_cloths of the race/gender).</summary>
    public LoginCustomizingItem[] PreviewCloths(long model)
    {
        if (_previewCloths.TryGetValue(model, out var cached)) return cached;
        var list = new List<LoginCustomizingItem>();
        if (_path != null && model > 0)
        {
            using var db = Open(); using var q = db.CreateCommand();
            q.CommandText = """
                SELECT p.equip_pack_cloth_id, COALESCE(i.filename,'') FROM character_preview_cloths p LEFT JOIN icons i ON i.id=p.icon_id
                WHERE p.character_id=(SELECT id FROM characters WHERE model_id=$m ORDER BY id LIMIT 1) ORDER BY p.ui_order,p.id
                """;
            q.Parameters.AddWithValue("$m", model);
            using var r = q.ExecuteReader();
            while (r.Read()) list.Add(new LoginCustomizingItem(r.GetInt64(0), IconPath(r.GetString(1))));
        }
        return _previewCloths[model] = list.ToArray();
    }

    /// <summary>
    /// Equipment of a preview cloth pack by equip slot, every pack slot present (0 = empty) so the pack replaces the race's
    /// default preview pack instead of adding to it. Preview only: none of it is sent when the character is created.
    /// </summary>
    public Dictionary<int, long> PreviewClothEquipment(long packId)
    {
        var result = new Dictionary<int, long>();
        if (_path == null || packId <= 0) return result;
        using var db = Open(); using var q = db.CreateCommand();
        q.CommandText = """
            SELECT headgear_id, necklace_id, shirt_id, belt_id, pants_id, glove_id, shoes_id, bracelet_id, back_id,
              undershirt_id, underpants_id, cosplay_id, backpack_id, stabilizer_id FROM equip_pack_cloths WHERE id=$p
            """;
        q.Parameters.AddWithValue("$p", packId);
        using var r = q.ExecuteReader();
        if (!r.Read()) return result;
        var slots = CharacterAssetResolver.PreviewPackSlots;
        for (var i = 0; i < slots.Length && i < r.FieldCount; i++) result[slots[i]] = L(r, i);
        return result;
    }

    /// <summary>Login ability preview icon, using the same ui/icon path convention as the in-world skill API.</summary>
    public string SkillIcon(int skillId)
    {
        if (skillId <= 0 || _path == null) return "";
        if (_skillIconCache.TryGetValue(skillId, out var cached)) return cached;
        using var db = Open(); using var q = db.CreateCommand();
        q.CommandText = "SELECT COALESCE(i.filename, '') FROM skills s LEFT JOIN icons i ON i.id=s.icon_id WHERE s.id=$id LIMIT 1";
        q.Parameters.AddWithValue("$id", skillId);
        var filename = Convert.ToString(q.ExecuteScalar()) ?? "";
        return _skillIconCache[skillId] = filename.Length == 0 ? "" : "ui/icon/" + filename;
    }

    public LoginCharacterAppearance Defaults(long model)
    {
        var id = Scalar("SELECT default_custom_id FROM characters WHERE model_id=@a ORDER BY id LIMIT 1", model, 0);
        return LoadAppearance(model, id) ?? new LoginCharacterAppearance();
    }

    /// <summary>A total_character_customs row as a complete creation appearance (null when missing).</summary>
    private LoginCharacterAppearance? LoadAppearance(long model, long id)
    {
        if (id == 0 || _path == null) return null;
        var a = new LoginCharacterAppearance();
        using var db = Open(); using var q = db.CreateCommand();
        q.CommandText = """
            SELECT hair_id,hair_color_id,skin_color_id,face_normal_map_id,face_normal_map_weight,
              body_normal_map_id,body_normal_map_weight,face_id,body_id,horn_id,tail_id,default_hair_color,
              face_movable_decal_asset_id,face_fixed_decal_asset_0_id,face_fixed_decal_asset_1_id,
              face_fixed_decal_asset_2_id,face_fixed_decal_asset_3_id,face_fixed_decal_asset_4_id,
              face_fixed_decal_asset_5_id,face_movable_decal_weight,face_fixed_decal_asset_0_weight,
              face_fixed_decal_asset_1_weight,face_fixed_decal_asset_2_weight,face_fixed_decal_asset_3_weight,
              face_fixed_decal_asset_4_weight,face_fixed_decal_asset_5_weight,lip_color,left_pupil_color,
              right_pupil_color,eyebrow_color,deco_color,modifier,face_movable_decal_scale,face_movable_decal_rotate,
              face_movable_decal_move_x,face_movable_decal_move_y,horn_color_id,two_tone_hair_color,
              two_tone_first_width,two_tone_second_width
            FROM total_character_customs WHERE id=$id
            """;
        q.Parameters.AddWithValue("$id", id); using var r = q.ExecuteReader(); if (!r.Read()) return null;
        a.HairItemId=L(r,0); a.HairColorId=L(r,1); a.SkinColorId=L(r,2); a.FaceNormalMapId=L(r,3); a.FaceNormalMapWeight=F(r,4);
        a.BodyNormalMapId=L(r,5); a.BodyNormalMapWeight=F(r,6); a.FaceItemId=L(r,7); a.BodyItemId=L(r,8); a.HornItemId=L(r,9); a.TailItemId=L(r,10); a.DefaultHairColor=(uint)L(r,11);
        for (var i = 0; i < 7; i++) { a.DecalIds[i] = L(r,12 + i); a.DecalWeights[i] = F(r,19 + i, 1); }
        a.MovableDecalWeight = a.DecalWeights[0];
        a.LipColor=(uint)L(r,26); a.LeftPupilColor=(uint)L(r,27); a.RightPupilColor=(uint)L(r,28);
        a.EyebrowColor=(uint)L(r,29); a.DecoColor=(uint)L(r,30); a.Modifier=r.IsDBNull(31)?[]:(byte[])r.GetValue(31);
        a.MovableDecalScale=F(r,32); a.MovableDecalRotate=F(r,33);
        a.MovableDecalMoveX=(short)Math.Clamp(L(r,34), short.MinValue, short.MaxValue);
        a.MovableDecalMoveY=(short)Math.Clamp(L(r,35), short.MinValue, short.MaxValue);
        a.HornColorId=L(r,36); a.TwoToneHairColor=(uint)L(r,37); a.TwoToneFirstWidth=F(r,38); a.TwoToneSecondWidth=F(r,39);
        // The default custom leaves face and body at 0; the create packet must still name them. The server fills a 0 slot with
        // the model's last item_body_parts row, which is the character-creation mannequin (e.g. nu_m_mannequin_face/_nude).
        // Use the race's default face (characters.face_item_id) and its first player body, as world units are dressed.
        if (a.FaceItemId == 0) a.FaceItemId = Scalar("SELECT face_item_id FROM characters WHERE model_id=@a ORDER BY id LIMIT 1", model, 0);
        if (a.BodyItemId == 0)
            a.BodyItemId = Scalar("SELECT item_id FROM item_body_parts WHERE model_id=@a AND slot_type_id=28 AND npc_only='f' AND item_id>@b ORDER BY id LIMIT 1", model, 0);
        return a;
    }

    private long[] Load(long model, string kind)
    {
        if (_path == null || model == 0) return [];
        var sql = kind switch
        {
            "face" => "SELECT DISTINCT item_id FROM item_body_parts WHERE model_id=$m AND slot_type_id=23 AND npc_only='f' AND item_id>0 AND item_id NOT IN (SELECT id FROM items WHERE category_id=202) ORDER BY id",
            "body" => "SELECT DISTINCT item_id FROM item_body_parts WHERE model_id=$m AND slot_type_id=28 AND npc_only='f' AND item_id>0 AND item_id NOT IN (SELECT id FROM items WHERE category_id=202) ORDER BY id",
            _ => null,
        };
        if (sql == null) return Items(model, kind).Select(i => i.Id).ToArray();
        using var db=Open(); using var q=db.CreateCommand(); q.CommandText=sql; q.Parameters.AddWithValue("$m",model);
        using var r=q.ExecuteReader(); var list=new List<long>(); while(r.Read()) list.Add(r.GetInt64(0)); return list.ToArray();
    }

    /// <summary>The model's *_targets.xml, found next to its .cdf the way the renderer finds it.</summary>
    private byte[]? FaceTargetsXml(long model)
    {
        if (_path == null || model <= 0) return null;
        using var db = Open(); using var q = db.CreateCommand();
        q.CommandText = "SELECT a.model_file FROM models m JOIN actor_models a ON a.id=m.sub_id AND m.sub_type='ActorModel' WHERE m.id=$m";
        q.Parameters.AddWithValue("$m", model);
        var file = CdfReader.PakPath(Convert.ToString(q.ExecuteScalar()) ?? "");
        var slash = file.LastIndexOf('/');
        var gender = slash > 0 ? file.LastIndexOf('/', slash - 1) : -1;
        if (gender < 0) return null;
        var path = file[..gender] + "/face/" + Path.GetFileNameWithoutExtension(file) + "_targets.xml";
        try { return PakFiles.Read(path); }
        catch (Exception) { return null; }
    }

    private static string DecalSql(int category) =>
        $"SELECT a.id, COALESCE(i.filename,''), a.is_new,'f','f' FROM face_decal_assets a LEFT JOIN icons i ON i.id=a.icon_id WHERE a.model_id=$m AND a.category_id={category} AND a.npc_only='f' ORDER BY a.display_order,a.id";
    private static string IconPath(string filename) => filename.Length == 0 ? "ui/login_stage/no_image.dds" : "ui/icon/" + filename.Replace('\\', '/');
    private static bool Flag(SqliteDataReader r, int i) => !r.IsDBNull(i) && Convert.ToString(r.GetValue(i), CultureInfo.InvariantCulture) is "t" or "1" or "True";
    private static int I(SqliteDataReader r, int i) => r.IsDBNull(i) ? 0 : (int)Math.Round(Convert.ToDouble(r.GetValue(i), CultureInfo.InvariantCulture));
    private static double Number(string s, double fallback) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    /// <summary>Colours are stored as 0xAABBGGRR (e.g. total_character_customs.lip_color).</summary>
    public static uint Abgr(int r, int g, int b) => 0xff000000u | ((uint)Math.Clamp(b, 0, 255) << 16) | ((uint)Math.Clamp(g, 0, 255) << 8) | (uint)Math.Clamp(r, 0, 255);

    private long Scalar(string sql,long a,long b) { if(_path==null)return 0; using var db=Open();using var q=db.CreateCommand();q.CommandText=sql;q.Parameters.AddWithValue("@a",a);q.Parameters.AddWithValue("@b",b);return Convert.ToInt64(q.ExecuteScalar()??0); }
    private SqliteConnection Open(){var db=new SqliteConnection($"Data Source={_path};Mode=ReadOnly");db.Open();return db;}
    private static long L(SqliteDataReader r,int i)=>r.IsDBNull(i)?0:r.GetInt64(i); private static float F(SqliteDataReader r,int i,float fallback=0)=>r.IsDBNull(i)?fallback:Convert.ToSingle(r.GetValue(i));
}
