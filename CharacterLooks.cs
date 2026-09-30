using System.Collections.Concurrent;
using System.Globalization;
using Godot;

namespace AAEmu.GodotViewer;

/// <summary>
/// The per-character parts of ArcheAge's character materials that a plain .mtl request can't express:
/// <list type="bullet">
/// <item>Skin colour. Every HumanSkin sub-material (body, face, and the "skin" sub-materials of armour) is drawn with
///   <see cref="SkinShaderSource"/>, a port of the diffuse part of the client's HumanSkin shader
///   (game/shaders/cache/d3d11/humanskin.cfxb, SkinfNdotL2 and frag_custom_per_light): the diffuse texture is multiplied
///   by the material Diffuse colour, and each light by a colour that goes from the dark (subsurface) colour on the
///   shadow side through MSkinColor at the terminator to BSkinColor where the light hits straight on. A skin_colors row
///   replaces Diffuse, Specular, glossiness, BSkinColor and MSkinColor; without one the .mtl values are used.</item>
/// <item>Face decals. The customizer's decals (tattoo, deco, makeup, scar, eyebrow, pupils) and the lip colour are
///   painted into a copy of the face diffuse texture (<see cref="ComposeFace"/>), at face_decal_assets
///   defaultX/defaultY (pupils: odd_eye_info), in that texture's pixels. The pupils go into the Eye sub-material's
///   texture (usually the same file: the eyeballs are two discs at its edge), which gives the eyes their irises.</item>
/// <item>Eyes (<see cref="CreateEyeMaterial"/>) and eyelashes (blended, see ModelLibrary.CharacterLooks.cs).</item>
/// </list>
/// Everything here runs on worker threads; decoded source images are shared by every ModelLibrary.
/// </summary>
internal static class CharacterLooks
{
    /// <summary>
    /// HumanSkin Diffuse colours are small (0.086 for the default nuian skin) because the client lights skin with its
    /// HDR light levels; cloth and armour (Illum) are authored around 0.2-0.4 and the viewer draws those at texture
    /// brightness. Scaling skin by 3 (about 1/0.33) keeps the client's skin-to-cloth brightness ratio (calibrated against
    /// the original client's character creation screen, skin colours 1, 21 and 83 of the nuian male).
    /// </summary>
    public const float SkinDiffuseScale = 3f;

    /// <summary>How strongly the lip colour replaces the lip texture (compared with the original client, lip colour black/white/green).</summary>
    private const float LipColorStrength = 0.3f;

    /// <summary>Opacity of the hair scalp texture at its whitest.</summary>
    private static readonly float HairBaseStrength = float.TryParse(System.Environment.GetEnvironmentVariable("GV_HAIRBASE"), NumberStyles.Float, CultureInfo.InvariantCulture, out var hb) ? hb : 1f;

    private static readonly ConcurrentDictionary<string, Lazy<Image>> Decoded = new();
    private static readonly ConcurrentDictionary<string, Lazy<Vector3?>> AverageColors = new();

    public static bool IsHumanSkin(MtlMaterial sub) => sub != null && sub.Shader.Equals("HumanSkin", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// How HumanSkin reads its gloss (Specular slot) map, from the sub-material's GenMask: null = no gloss map
    /// (%GLOSS_MAP 0x2 clear), true = the specular amount is its alpha (%SPLIT_GLOSS_MAP 0x8, %FUZZYNESS 0x1000000000,
    /// %GLOSS_MAP_IGNORE_SPECULAR_COLOR 0x2000000000; red/green then hold gloss and iris masks), false = its colour.
    /// </summary>
    public static bool? GlossFromAlpha(MtlMaterial sub)
    {
        if (!ulong.TryParse(sub.GenMask, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var mask) || (mask & 0x2) == 0)
            return null;
        return (mask & (0x8UL | 0x1000000000UL | 0x2000000000UL)) != 0;
    }

    public static bool IsEye(MtlMaterial sub) => sub != null && sub.Shader.Equals("Eye", StringComparison.OrdinalIgnoreCase);

    /// <summary>The HumanSkin parameters of a sub-material, with a skin_colors row laid over them.</summary>
    public sealed record SkinParameters(Vector3 Tint, Vector3 Bright, Vector3 Mid, Vector3 Dark, float Diffusion, float Subsurface,
        Vector3 Specular, float Shininess)
    {
        public string Key => string.Create(CultureInfo.InvariantCulture,
            $"{Tint.X:F4},{Tint.Y:F4},{Tint.Z:F4}|{Bright.X:F3},{Bright.Y:F3},{Bright.Z:F3}|{Mid.X:F3},{Mid.Y:F3},{Mid.Z:F3}|{Dark.X:F3},{Dark.Y:F3},{Dark.Z:F3}|{Diffusion:F2}|{Subsurface:F2}|{Specular.X:F3},{Specular.Y:F3},{Specular.Z:F3}|{Shininess:F1}");
    }

    public static SkinParameters Skin(MtlMaterial sub, SkinColorInfo skin)
    {
        static Vector3 V(System.Numerics.Vector3 v) => new(v.X, v.Y, v.Z);
        var diffuse = skin != null ? V(skin.Diffuse) : V(sub.DiffuseColor);
        var bright = skin != null ? V(skin.BrightSkinColor) : Param(sub, "BSkinColor", new Vector3(0.87f, 0.91f, 0.96f));
        var mid = skin != null ? V(skin.MiddleSkinColor) : Param(sub, "MSkinColor", new Vector3(1f, 0.71f, 0.32f));
        var specular = skin != null ? V(skin.Specular) * Math.Max(skin.SpecularLevel, 0f) : V(sub.SpecularColor);
        var shininess = skin != null && skin.Glossiness > 0 ? skin.Glossiness : sub.Shininess > 0 ? sub.Shininess : 25f;

        // cDarkColor = lerp(sss.a, sss.rgb, SSSMapSat): the subsurface map is a flat colour on every character checked.
        var saturation = Scalar(sub, "SSSMapSat", 0.5f);
        var sss = sub.Texture("SubSurface") is { } sssMap ? AverageColor(sssMap.PakPath) : null;
        var dark = sss is { } s ? Vector3.One.Lerp(s, saturation) : new Vector3(0.8f, 0.66f, 0.66f);
        // Never brighter than the texture: a few monster/NPC skins have Diffuse up to 0.8 (1% above 0.33).
        var tint = diffuse * SkinDiffuseScale;
        var peak = Math.Max(tint.X, Math.Max(tint.Y, tint.Z));
        if (peak > 1f)
            tint /= peak;
        return new SkinParameters(tint, bright, mid, dark, Scalar(sub, "DiffusionAmount", 1.5f),
            Scalar(sub, "SubsurfaceStrenght", 0f), specular, shininess);
    }

    /// <summary>Shader material for a HumanSkin surface.</summary>
    public static ShaderMaterial CreateSkinMaterial(SkinParameters p, Texture2D albedo, Vector2 uvScale, Vector2 uvOffset,
        Texture2D normal, float normalDepth, Texture2D specularMap, bool specularFromAlpha, bool twoSided, float alphaScissor)
    {
        var material = new ShaderMaterial { Shader = SkinShader(twoSided, alphaScissor > 0) };
        material.SetShaderParameter("albedo_texture", albedo);
        material.SetShaderParameter("uv_scale", uvScale);
        material.SetShaderParameter("uv_offset", uvOffset);
        material.SetShaderParameter("skin_tint", p.Tint);
        material.SetShaderParameter("bright_skin", p.Bright);
        material.SetShaderParameter("mid_skin", p.Mid);
        material.SetShaderParameter("dark_skin", p.Dark);
        material.SetShaderParameter("diffusion", p.Diffusion);
        material.SetShaderParameter("subsurface", p.Subsurface);
        material.SetShaderParameter("has_normal_texture", normal != null);
        if (normal != null)
            material.SetShaderParameter("normal_texture", normal);
        material.SetShaderParameter("normal_depth", normalDepth);
        material.SetShaderParameter("has_specular_texture", specularMap != null);
        if (specularMap != null)
            material.SetShaderParameter("specular_texture", specularMap);
        material.SetShaderParameter("specular_from_alpha", specularFromAlpha);
        // The client's skin shows only a faint sheen (its lights' specular is weaker than their diffuse); the raw
        // specular colours (up to 1.1) look oily here, most of all with the opaque (alpha 1) gloss maps of the
        // split-gloss faces (tuned by eye, not derived).
        material.SetShaderParameter("specular_color", p.Specular * (specularFromAlpha ? 0.15f : 0.3f));
        material.SetShaderParameter("shininess", p.Shininess);
        if (alphaScissor > 0)
            material.SetShaderParameter("alpha_scissor", alphaScissor);
        return material;
    }

    /// <summary>
    /// Shader material for an Eye surface (eye.cfxb): the (composited) face texture times the Diffuse colour, the iris
    /// (red channel of the eye's specular map) darkened by IrisColor, and the eyeball darkened towards its edges by
    /// OutsideDarkness (lerp(1, pow(N.V, 3), OutsideDarkness)).
    /// </summary>
    public static ShaderMaterial CreateEyeMaterial(MtlMaterial sub, Texture2D albedo, Texture2D irisMask, Vector2 uvScale, Vector2 uvOffset)
    {
        var tint = new Vector3(sub.DiffuseColor.X, sub.DiffuseColor.Y, sub.DiffuseColor.Z) * SkinDiffuseScale;
        tint = new Vector3(Math.Min(tint.X, 1f), Math.Min(tint.Y, 1f), Math.Min(tint.Z, 1f));
        var material = new ShaderMaterial { Shader = EyeShader.Value };
        material.SetShaderParameter("albedo_texture", albedo);
        material.SetShaderParameter("uv_scale", uvScale);
        material.SetShaderParameter("uv_offset", uvOffset);
        material.SetShaderParameter("tint", tint);
        material.SetShaderParameter("iris_color", Param(sub, "IrisColor", new Vector3(0.5f, 0.5f, 0.5f)));
        material.SetShaderParameter("outside_darkness", Math.Clamp(Scalar(sub, "OutsideDarkness", 0.9f), 0f, 1f));
        material.SetShaderParameter("has_iris_mask", irisMask != null);
        if (irisMask != null)
            material.SetShaderParameter("iris_mask", irisMask);
        return material;
    }

    private static readonly Lazy<Shader> EyeShader = new(() => new Shader { Code = """
        shader_type spatial;
        render_mode blend_mix, depth_draw_opaque;

        uniform sampler2D albedo_texture : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
        uniform sampler2D iris_mask : hint_default_black, filter_linear_mipmap, repeat_enable;
        uniform bool has_iris_mask = false;
        uniform vec2 uv_scale = vec2(1.0);
        uniform vec2 uv_offset = vec2(0.0);
        uniform vec3 tint = vec3(1.0);
        uniform vec3 iris_color = vec3(0.5);
        uniform float outside_darkness = 0.9;

        void fragment() {
            vec2 uv = UV * uv_scale + uv_offset;
            vec3 c = texture(albedo_texture, uv).rgb * tint;
            if (has_iris_mask) {
                c *= mix(vec3(1.0), iris_color, texture(iris_mask, uv).r);
            }
            float ndv = clamp(dot(NORMAL, VIEW), 0.0, 1.0);
            c *= mix(1.0, ndv * ndv * ndv, outside_darkness);
            ALBEDO = c;
            ROUGHNESS = 0.15;
            SPECULAR = 0.5;
        }
        """ });

    // ================================================================== face composite

    /// <summary>Cache key of a face composite; empty when there is nothing to paint.</summary>
    public static string FaceKey(string facePath, CharacterAssets assets, string maskPath, bool skinLayers, bool eyeLayers)
    {
        var decals = assets.FaceDecals.Where(d => IsPupil(d) ? eyeLayers : skinLayers).ToList();
        var lips = skinLayers && assets.LipColor != 0 && maskPath.Length > 0;
        var hairBase = skinLayers && assets.HairBaseTexture.Length > 0 && assets.HairBaseColor != 0;
        if (decals.Count == 0 && !lips && !hairBase)
            return "";
        var sb = new System.Text.StringBuilder("face|").Append(facePath).Append('|').Append(maskPath).Append('|')
            .Append(lips ? assets.LipColor.ToString("X8", CultureInfo.InvariantCulture) : "-")
            .Append('|').Append(hairBase ? assets.HairBaseTexture + "#" + assets.HairBaseColor.ToString("X8", CultureInfo.InvariantCulture) : "-");
        foreach (var d in decals)
            sb.Append('|').Append(d.Kind).Append(':').Append(d.TexturePath).Append('@').Append(d.X).Append(',').Append(d.Y)
                .Append('*').Append(d.Weight.ToString("F3", CultureInfo.InvariantCulture)).Append('#')
                .Append(d.Color.ToString("X8", CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    /// <summary>
    /// The mask laid out like a face texture: "…/nuian/male/face/face00/nu_m_face00_df.dds" ->
    /// "…/nuian/male/face/nu_m_face_mask.dds" (red = face, green = lips, blue = eyeballs, alpha = scalp/neck). Taken
    /// from the texture, not the race, because some races draw another race's face texture. "" when there is none.
    /// </summary>
    public static string FaceMaskPath(string facePath)
    {
        var path = PakFiles.Normalize(facePath);
        var i = path.IndexOf("/face/", StringComparison.Ordinal);
        var parts = path[(path.LastIndexOf('/') + 1)..].Split('_');
        if (i < 0 || parts.Length < 2)
            return "";
        var mask = path[..(i + 6)] + parts[0] + "_" + parts[1] + "_face_mask.dds";
        return PakFiles.Exists(mask) ? mask : "";
    }

    /// <summary>
    /// The face diffuse with the decals painted in (DXT1 with mipmaps), or null when the face texture can't be read.
    /// Order: tattoo, deco, makeup, scar, eyebrow, lips, pupils. A coloured decal (eyebrow, deco, pupil) is its grey
    /// texture times the colour (twice for the flat-grey eyebrow and deco textures); the texture's alpha times the decal weight blends it in.
    /// </summary>
    public static Image ComposeFace(string facePath, CharacterAssets assets, string maskPath, bool skinLayers = true, bool eyeLayers = true)
    {
        var face = Decode(facePath);
        if (face == null)
            return null;
        int w = face.GetWidth(), h = face.GetHeight();
        var dst = face.GetData();
        var mask = maskPath.Length > 0 ? Decode(maskPath) : null;
        var maskData = mask?.GetData();
        int mw = mask?.GetWidth() ?? 0, mh = mask?.GetHeight() ?? 0;
        byte Mask(int x, int y, int channel) => maskData == null ? (byte)255
            : maskData[((y * mh / h) * mw + x * mw / w) * 4 + channel];

        if (skinLayers && assets.HairBaseTexture.Length > 0 && assets.HairBaseColor != 0)
            PaintHairBase(assets.HairBaseTexture, assets.HairBaseColor);

        string[] order = ["tattoo", "deco", "makeup", "scar", "eyebrow", "pupil_left", "pupil_right"];
        foreach (var kind in order.Take(skinLayers ? 5 : 0))
            foreach (var d in assets.FaceDecals.Where(d => d.Kind == kind))
                Paint(d, kind == "scar");

        if (skinLayers && assets.LipColor != 0 && maskData != null)
        {
            var (lr, lg, lb, la) = Rgba(assets.LipColor);
            var strength = LipColorStrength * (la == 0 ? 1f : la / 255f);
            for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                {
                    var f = Mask(x, y, 1) / 255f * strength;
                    if (f <= 0.002f)
                        continue;
                    var o = (y * w + x) * 4;
                    dst[o] = Mix(dst[o], lr, f);
                    dst[o + 1] = Mix(dst[o + 1], lg, f);
                    dst[o + 2] = Mix(dst[o + 2], lb, f);
                }
        }

        foreach (var kind in order.Skip(5).Where(_ => eyeLayers))
            foreach (var d in assets.FaceDecals.Where(d => d.Kind == kind))
                Paint(d, false);

        var image = Image.CreateFromData(w, h, false, Image.Format.Rgba8, dst);
        image.GenerateMipmaps();
        // One texture per look stays in video memory while the look is cached: DXT1 keeps it at an eighth. Godot's
        // own compressor takes 60-100 ms for 512x512; this range fit takes a few.
        return w % 4 == 0 && h % 4 == 0 ? EncodeDxt1(image) : image;

        // The scalp texture (item_body_parts.hair_base) is a grey shape along the hairline in the face texture's layout; it is
        // painted in the hair's colour, opaque where it is white (the teal sideburn strip of the silver preset).
        void PaintHairBase(string path, uint color)
        {
            var scalp = Decode(path);
            if (scalp == null)
                return;
            int sw = scalp.GetWidth(), sh = scalp.GetHeight();
            var src = scalp.GetData();
            var (cr, cg, cb, _) = Rgba(color);
            for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                {
                    var s = ((y * sh / h) * sw + x * sw / w) * 4;
                    var a = (src[s] + src[s + 1] + src[s + 2]) / (3f * 255f) * HairBaseStrength;
                    if (a <= 0.004f)
                        continue;
                    var o = (y * w + x) * 4;
                    dst[o] = Mix(dst[o], cr, a);
                    dst[o + 1] = Mix(dst[o + 1], cg, a);
                    dst[o + 2] = Mix(dst[o + 2], cb, a);
                }
        }

        void Paint(FaceDecal d, bool masked)
        {
            var decal = Decode(d.TexturePath);
            if (decal == null || d.Weight <= 0)
                return;
            int dw = decal.GetWidth(), dh = decal.GetHeight();
            var src = decal.GetData();
            var colored = d.Kind is "eyebrow" or "deco" or "pupil_left" or "pupil_right" && d.Color != 0;
            // Eyebrow and deco textures are flat 50% grey (times two keeps the colour); the iris texture is the iris
            // itself (a white colour leaves it light grey in the client).
            var gain = d.Kind is "eyebrow" or "deco" ? 2 : 1;
            var (cr, cg, cb, _) = Rgba(d.Color);
            var weight = Math.Clamp(d.Weight, 0f, 1f);
            for (var y = Math.Max(0, -d.Y); y < dh && d.Y + y < h; y++)
                for (var x = Math.Max(0, -d.X); x < dw && d.X + x < w; x++)
                {
                    var s = (y * dw + x) * 4;
                    var a = src[s + 3] / 255f * weight;
                    int tx = d.X + x, ty = d.Y + y;
                    if (masked)
                        a *= Mask(tx, ty, 0) / 255f;
                    if (a <= 0.002f)
                        continue;
                    int r = src[s], g = src[s + 1], b = src[s + 2];
                    if (colored)
                    {
                        // The client's irises keep a little of their grey (a black colour leaves a grey ring).
                        var keep = gain == 1 ? 0.2f : 0f;
                        r = (int)(Math.Min(255, r * cr * gain / 255) * (1 - keep) + r * keep);
                        g = (int)(Math.Min(255, g * cg * gain / 255) * (1 - keep) + g * keep);
                        b = (int)(Math.Min(255, b * cb * gain / 255) * (1 - keep) + b * keep);
                    }
                    var o = (ty * w + tx) * 4;
                    dst[o] = Mix(dst[o], r, a);
                    dst[o + 1] = Mix(dst[o + 1], g, a);
                    dst[o + 2] = Mix(dst[o + 2], b, a);
                }
        }
    }

    private static byte Mix(byte a, int b, float t) => (byte)Math.Clamp((int)MathF.Round(a + (b - a) * t), 0, 255);

    private static bool IsPupil(FaceDecal d) => d.Kind is "pupil_left" or "pupil_right";

    /// <summary>RGBA8 image with mipmaps -> DXT1 (bounding-box range fit per 4x4 block, alpha dropped).</summary>
    private static Image EncodeDxt1(Image rgba)
    {
        int w = rgba.GetWidth(), h = rgba.GetHeight();
        var src = rgba.GetData();
        var levels = rgba.GetMipmapCount() + 1;
        var size = 0;
        for (var l = 0; l < levels; l++)
            size += Math.Max(1, ((w >> l) + 3) / 4) * Math.Max(1, ((h >> l) + 3) / 4) * 8;
        var dst = new byte[size];
        var o = 0;
        Span<int> px = stackalloc int[48];
        for (var l = 0; l < levels; l++)
        {
            int lw = Math.Max(1, w >> l), lh = Math.Max(1, h >> l);
            var baseOffset = (int)rgba.GetMipmapOffset(l);
            for (var by = 0; by < lh; by += 4)
                for (var bx = 0; bx < lw; bx += 4)
                {
                    int r0 = 255, g0 = 255, b0 = 255, r1 = 0, g1 = 0, b1 = 0;
                    for (var i = 0; i < 16; i++)
                    {
                        var s = baseOffset + (Math.Min(by + i / 4, lh - 1) * lw + Math.Min(bx + i % 4, lw - 1)) * 4;
                        int r = src[s], g = src[s + 1], b = src[s + 2];
                        px[i * 3] = r; px[i * 3 + 1] = g; px[i * 3 + 2] = b;
                        r0 = Math.Min(r0, r); g0 = Math.Min(g0, g); b0 = Math.Min(b0, b);
                        r1 = Math.Max(r1, r); g1 = Math.Max(g1, g); b1 = Math.Max(b1, b);
                    }
                    // inset the box by 1/16 (as stb_dxt does), then quantize the end points to 5:6:5
                    int ir = (r1 - r0) >> 4, ig = (g1 - g0) >> 4, ib = (b1 - b0) >> 4;
                    r0 += ir; g0 += ig; b0 += ib; r1 -= ir; g1 -= ig; b1 -= ib;
                    var hi = (r1 >> 3 << 11) | (g1 >> 2 << 5) | (b1 >> 3);
                    var lo = (r0 >> 3 << 11) | (g0 >> 2 << 5) | (b0 >> 3);
                    uint indices = 0;
                    if (hi != lo)
                    {
                        if (hi < lo)
                            (hi, lo, r0, g0, b0, r1, g1, b1) = (lo, hi, r1, g1, b1, r0, g0, b0);
                        // project onto the box diagonal: 0 -> colour0 (hi), 1 -> colour1 (lo), 2 and 3 in between
                        int dr = r1 - r0, dg = g1 - g0, db = b1 - b0;
                        var len = dr * dr + dg * dg + db * db;
                        for (var i = 15; i >= 0; i--)
                        {
                            var t = len == 0 ? 0 : ((px[i * 3] - r0) * dr + (px[i * 3 + 1] - g0) * dg + (px[i * 3 + 2] - b0) * db) * 6 / len;
                            // t: 0 (at colour1) .. 6 (at colour0)
                            // nearest of the four palette points (at t = 0, 2, 4, 6)
                            uint index = t <= 0 ? 1u : t <= 2 ? 3u : t <= 4 ? 2u : 0u;
                            indices = (indices << 2) | index;
                        }
                    }
                    dst[o] = (byte)hi; dst[o + 1] = (byte)(hi >> 8); dst[o + 2] = (byte)lo; dst[o + 3] = (byte)(lo >> 8);
                    dst[o + 4] = (byte)indices; dst[o + 5] = (byte)(indices >> 8); dst[o + 6] = (byte)(indices >> 16); dst[o + 7] = (byte)(indices >> 24);
                    o += 8;
                }
        }
        return Image.CreateFromData(w, h, true, Image.Format.Dxt1, dst);
    }

    /// <summary>0xAABBGGRR -> (r, g, b, a).</summary>
    private static (int R, int G, int B, int A) Rgba(uint c) => ((int)(c & 0xFF), (int)((c >> 8) & 0xFF), (int)((c >> 16) & 0xFF), (int)(c >> 24));

    /// <summary>A pak .dds decoded to RGBA8 without mipmaps (shared; don't modify), or null.</summary>
    public static Image Decode(string pakPath) =>
        string.IsNullOrEmpty(pakPath) ? null : Decoded.GetOrAdd(PakFiles.Normalize(pakPath), p => new Lazy<Image>(() =>
        {
            try
            {
                var bytes = PakFiles.Read(p);
                if (bytes == null)
                    return null;
                var image = new Image();
                if (image.LoadDdsFromBuffer(CryDds.PrepareForGodot(bytes)) != Error.Ok || image.IsEmpty())
                    return null;
                if (image.IsCompressed())
                    image.Decompress();
                image.ClearMipmaps();
                image.Convert(Image.Format.Rgba8);
                return image;
            }
            catch (Exception e)
            {
                GD.PrintErr($"{p}: {e.Message}");
                return null;
            }
        })).Value;

    /// <summary>Average colour of a texture (linear when the file is sRGB), or null.</summary>
    private static Vector3? AverageColor(string pakPath) =>
        AverageColors.GetOrAdd(PakFiles.Normalize(pakPath), p => new Lazy<Vector3?>(() =>
        {
            var image = Decode(p);
            if (image == null)
                return null;
            var data = image.GetData();
            double r = 0, g = 0, b = 0;
            var n = data.Length / 4;
            for (var i = 0; i < data.Length; i += 4)
            {
                r += data[i];
                g += data[i + 1];
                b += data[i + 2];
            }
            var c = new Color((float)(r / n / 255), (float)(g / n / 255), (float)(b / n / 255));
            var bytes = PakFiles.Read(p);
            if (bytes != null && CryDds.ReadInfo(bytes).IsSrgb)
                c = c.SrgbToLinear();
            return new Vector3(c.R, c.G, c.B);
        })).Value;

    private static Vector3 Param(MtlMaterial sub, string name, Vector3 fallback)
    {
        if (!sub.PublicParams.TryGetValue(name, out var text))
            return fallback;
        var c = text.Split(',').Select(x => float.TryParse(x, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : float.NaN).ToArray();
        return c.Length >= 3 && c.Take(3).All(float.IsFinite) ? new Vector3(c[0], c[1], c[2]) : fallback;
    }

    private static float Scalar(MtlMaterial sub, string name, float fallback) =>
        sub.PublicParams.TryGetValue(name, out var text) &&
        float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && float.IsFinite(value)
            ? value : fallback;

    // ================================================================== shader

    private static readonly ConcurrentDictionary<(bool TwoSided, bool Scissor), Lazy<Shader>> Shaders = new();

    private static Shader SkinShader(bool twoSided, bool scissor) => Shaders.GetOrAdd((twoSided, scissor), k => new Lazy<Shader>(() =>
    {
        var code = SkinShaderSource
            .Replace("/*CULL*/", k.TwoSided ? ", cull_disabled" : "")
            .Replace("/*ALPHA*/", k.Scissor ? "ALPHA = texel.a; ALPHA_SCISSOR_THRESHOLD = alpha_scissor;" : "");
        return new Shader { Code = code };
    })).Value;

    /// <summary>
    /// HumanSkin, diffuse per light (humanskin.cfxb SkinfNdotL2 + frag_custom_per_light):
    /// translucence = smoothstep(-DiffusionAmount * dark, 1, N.L);
    /// colour = lerp(dark, lerp(MSkinColor * (1 + SubsurfaceStrenght), BSkinColor, saturate(N.L^2)), translucence);
    /// diffuse = pow(saturate(translucence) * colour, 1.5) * 1.5, 20% less where the light comes from behind the
    /// viewer, times the light colour desaturated by 90%. Specular: Phong with the material's specular colour times the
    /// specular (gloss) map.
    /// </summary>
    private const string SkinShaderSource = """
        shader_type spatial;
        render_mode blend_mix, depth_draw_opaque/*CULL*/;

        uniform sampler2D albedo_texture : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
        uniform vec2 uv_scale = vec2(1.0);
        uniform vec2 uv_offset = vec2(0.0);
        uniform vec3 skin_tint = vec3(0.35);
        uniform vec3 bright_skin = vec3(0.87, 0.91, 0.96);
        uniform vec3 mid_skin = vec3(1.0, 0.71, 0.32);
        uniform vec3 dark_skin = vec3(0.8, 0.66, 0.66);
        uniform float diffusion = 1.5;
        uniform float subsurface = 0.0;
        uniform sampler2D normal_texture : hint_normal, filter_linear_mipmap_anisotropic, repeat_enable;
        uniform bool has_normal_texture = false;
        uniform float normal_depth = 1.0;
        uniform sampler2D specular_texture : hint_default_white, filter_linear_mipmap_anisotropic, repeat_enable;
        uniform bool has_specular_texture = false;
        uniform bool specular_from_alpha = false;
        uniform vec3 specular_color = vec3(0.1);
        uniform float shininess = 25.0;
        uniform float alpha_scissor = 0.5;

        varying vec3 spec_mask;

        void fragment() {
            vec2 uv = UV * uv_scale + uv_offset;
            vec4 texel = texture(albedo_texture, uv);
            ALBEDO = texel.rgb * skin_tint;
            /*ALPHA*/
            if (has_normal_texture) {
                NORMAL_MAP = texture(normal_texture, uv).rgb;
                NORMAL_MAP_DEPTH = normal_depth;
            }
            vec4 gloss = has_specular_texture ? texture(specular_texture, uv) : vec4(1.0);
            spec_mask = specular_from_alpha ? vec3(gloss.a) : gloss.rgb;
            ROUGHNESS = 0.7;
            SPECULAR = 0.2;
        }

        void light() {
            float ndl = dot(NORMAL, LIGHT);
            vec3 translucence = smoothstep(-diffusion * dark_skin, vec3(1.0), vec3(ndl));
            vec3 lit = mix(mid_skin * (subsurface + 1.0), bright_skin, clamp(ndl * ndl, 0.0, 1.0));
            vec3 colour = mix(dark_skin, lit, translucence);
            vec3 d = clamp(translucence, 0.0, 1.0) * colour;
            d = pow(max(d, vec3(0.0)), vec3(1.5)) * 1.5;
            d = mix(d, d * 0.8, clamp(dot(LIGHT, VIEW), 0.0, 1.0));
            float luminance = dot(LIGHT_COLOR, vec3(0.2126, 0.7152, 0.0722));
            vec3 light_colour = vec3(luminance) * 0.9 + LIGHT_COLOR * 0.1;
            DIFFUSE_LIGHT += d * ATTENUATION * light_colour / PI;
            vec3 h = normalize(LIGHT + VIEW);
            float ndh = clamp(dot(NORMAL, h), 0.0, 1.0);
            SPECULAR_LIGHT += pow(ndh, max(shininess, 1.0)) * clamp(ndl * 4.0, 0.0, 1.0) * specular_color * spec_mask
                * ATTENUATION * LIGHT_COLOR / PI * SPECULAR_AMOUNT;
        }
        """;
}
