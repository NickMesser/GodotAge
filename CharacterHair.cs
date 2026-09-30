using System.Collections.Concurrent;
using System.Globalization;
using Godot;

namespace AAEmu.GodotViewer;

/// <summary>
/// The client's Hair shader (game/shaders/hwscripts/cryfx/hair.cfx) for character hair, in Godot.
/// <list type="bullet">
/// <item>Colour: <c>cHair = diffuse texture * HairColor * 2</c> (the texture is grey), the lit result times the material
///   Diffuse colour. HairColor is a linear colour (public parameter of the material; a linear 1,0.67,0.37 is the pale
///   blond of the default nuian male, read as sRGB it is saturated orange).</item>
/// <item>Palette hair (customizing_item_assets.use_pallet): the unit's own colour replaces HairColor. Two-tone hair
///   (%USE_SECOND_HAIR_COLOR) blends HairColor with SecondHairColor by a mask (the material's SubSurface texture):
///   red channel along the lock (width 1), green channel for the streaks (width 2), each
///   <c>smoothstep(0, 1 - width, saturate(width - (1 - mask) * 0.5))</c>, weight = max of the two.</item>
/// <item>Lighting: wrapped diffuse ((N.L) * DiffuseWrap + (1 - DiffuseWrap)) plus a rim term, and two Kajiya-Kay
///   anisotropic highlights (primary tinted by HairColor, secondary by SecondarySpecColor) along the strand (the mesh
///   binormal), shifted by the material's shift values and the gloss map.</item>
/// </list>
/// Runs on any worker thread like the other character materials (the caller acquires the render budget).
/// </summary>
internal static class CharacterHair
{
    private static float Env(string name, float fallback) =>
        float.TryParse(System.Environment.GetEnvironmentVariable(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    /// <summary>
    /// Hair Diffuse colours are tiny (0.067 for most hairs): the client's HDR lights compensate. The same 1 / 0.216 that
    /// <see cref="CharacterIllum.DiffuseScale"/> uses for equipment brings hair to display level (4.5 x 0.067 x 2 = 0.6 of the
    /// colour times the texture); checked against the original client's default blond and silver preset.
    /// </summary>
    public static readonly float DiffuseScale = Env("GV_HAIR_GAIN", 4.5f);

    /// <summary>Strength of the highlights (the material Specular colour is 0.12-0.4).</summary>
    public static readonly float SpecularScale = Env("GV_HAIR_SPEC", 1.0f);

    /// <summary>
    /// How much of the hair colour's saturation the render keeps (1 = the material colour as it is). The client desaturates
    /// actor diffuse colours (X2CustomBalanceParamsA.y, CharDiffMapSaturation); measured against the original client's
    /// captures (default blond and the silver preset) the hair colours come out about 40% greyer than HairColor alone.
    /// </summary>
    public static readonly float Saturation = Env("GV_HAIR_SAT", 0.6f);

    /// <summary>0: palette colours (bytes) are used as they are (linear); 1: they are sRGB and converted.</summary>
    public static readonly bool PaletteIsSrgb = Env("GV_HAIR_PALETTE_SRGB", 1f) > 0.5f;

    public sealed record HairParameters(Vector3 Color, Vector3 Second, float Width1, float Width2, bool TwoTone,
        Vector3 PrimarySpec, float PrimaryPow, float PrimaryShift, Vector3 SecondarySpec, float SecondaryPow, float SecondaryShift,
        float Rim, float Wrap, float AmbientSpec, float Bump, Vector3 Diffuse, Vector3 Specular, float DetailTiling)
    {
        public string Key => string.Create(CultureInfo.InvariantCulture,
            $"{V(Color)}|{V(Second)}|{Width1:F3}|{Width2:F3}|{TwoTone}|{V(PrimarySpec)}|{PrimaryPow:F1}|{PrimaryShift:F3}|{V(SecondarySpec)}|{SecondaryPow:F1}|{SecondaryShift:F3}|{Rim:F3}|{Wrap:F3}|{AmbientSpec:F2}|{Bump:F3}|{V(Diffuse)}|{V(Specular)}|{DetailTiling:F2}");

        private static string V(Vector3 v) => string.Create(CultureInfo.InvariantCulture, $"{v.X:F3},{v.Y:F3},{v.Z:F3}");
    }

    /// <summary>True for a sub-material drawn by the client's Hair shader.</summary>
    public static bool IsHair(MtlMaterial sub) => sub != null && sub.Shader.Equals("Hair", StringComparison.OrdinalIgnoreCase);

    /// <summary>0xAABBGGRR (red in the low byte) as a colour the shader takes as linear.</summary>
    public static Vector3 FromBytes(uint abgr)
    {
        var c = new Color((abgr & 0xFF) / 255f, ((abgr >> 8) & 0xFF) / 255f, ((abgr >> 16) & 0xFF) / 255f);
        if (PaletteIsSrgb)
            c = c.SrgbToLinear();
        return new Vector3(c.R, c.G, c.B);
    }

    /// <summary>
    /// The sub-material's parameters. <paramref name="assets"/> supplies the unit's palette colours when the worn hair
    /// is a palette hair and this sub-material belongs to it (<paramref name="isWornHair"/>).
    /// </summary>
    public static HairParameters Read(MtlMaterial sub, CharacterAssets assets, bool isWornHair)
    {
        var color = Param(sub, "HairColor", Vector3.One);
        var second = Param(sub, "SecondHairColor", color);
        var width1 = Scalar(sub, "HairColorWidth", 0f);
        var width2 = Scalar(sub, "HairColorWidth2", 0f);
        var twoTone = HasBit(sub, 0x80000) && sub.Texture("SubSurface") != null;
        if (isWornHair && assets.HairUsesPalette && assets.HairColor != 0)
        {
            color = FromBytes(assets.HairColor);
            if (twoTone)
            {
                second = assets.HairSecondColor != 0 ? FromBytes(assets.HairSecondColor) : color;
                width1 = assets.HairWidth1;
                width2 = assets.HairWidth2;
            }
        }
        var diffuse = new Vector3(sub.DiffuseColor.X, sub.DiffuseColor.Y, sub.DiffuseColor.Z) * DiffuseScale;
        var specular = new Vector3(sub.SpecularColor.X, sub.SpecularColor.Y, sub.SpecularColor.Z) * Math.Max(sub.SpecularLevel, 0f) * SpecularScale;
        return new HairParameters(color, second, width1, width2, twoTone,
            Param(sub, "PrimarySpecColor", Vector3.One), Scalar(sub, "PrimarySpecPow", 200f), Scalar(sub, "PrimarySpecShift", 0.08f),
            Param(sub, "SecondarySpecColor", Vector3.One), Scalar(sub, "SecondarySpecPow", 16f), Scalar(sub, "SecondarySpecShift", 0.54f),
            Scalar(sub, "RimMultiplier", 0.2f), Scalar(sub, "DiffuseWrap", 0.75f), Scalar(sub, "AmbientSpecMult", 1f), Scalar(sub, "BumpScale", 0.05f),
            diffuse, specular, Scalar(sub, "DetailTilling", 1f));
    }

    public static ShaderMaterial Create(HairParameters p, Texture2D albedo, Texture2D mask, Texture2D gloss, Texture2D normal,
        Vector2 uvScale, Vector2 uvOffset, bool twoSided, float alphaTest)
    {
        var m = new ShaderMaterial { Shader = HairShader.Value };
        m.SetShaderParameter("albedo_texture", albedo);
        m.SetShaderParameter("uv_scale", uvScale);
        m.SetShaderParameter("uv_offset", uvOffset);
        m.SetShaderParameter("has_mask", mask != null && p.TwoTone);
        if (mask != null)
            m.SetShaderParameter("mask_texture", mask);
        m.SetShaderParameter("has_gloss", gloss != null);
        if (gloss != null)
            m.SetShaderParameter("gloss_texture", gloss);
        m.SetShaderParameter("has_normal_texture", normal != null);
        if (normal != null)
            m.SetShaderParameter("normal_texture", normal);
        m.SetShaderParameter("hair_color", p.Color);
        m.SetShaderParameter("second_color", p.Second);
        m.SetShaderParameter("width1", p.Width1);
        m.SetShaderParameter("width2", p.Width2);
        m.SetShaderParameter("primary_spec", p.PrimarySpec);
        m.SetShaderParameter("primary_pow", p.PrimaryPow);
        m.SetShaderParameter("primary_shift", p.PrimaryShift);
        m.SetShaderParameter("secondary_spec", p.SecondarySpec);
        m.SetShaderParameter("secondary_pow", p.SecondaryPow);
        m.SetShaderParameter("secondary_shift", p.SecondaryShift);
        m.SetShaderParameter("rim", p.Rim);
        m.SetShaderParameter("wrap", p.Wrap);
        m.SetShaderParameter("ambient_spec", p.AmbientSpec);
        m.SetShaderParameter("bump_scale", p.Bump);
        m.SetShaderParameter("diffuse_gain", p.Diffuse);
        m.SetShaderParameter("specular_gain", p.Specular);
        m.SetShaderParameter("saturation", Saturation);
        m.SetShaderParameter("detail_tiling", p.DetailTiling);
        m.SetShaderParameter("alpha_scissor", alphaTest > 0 ? alphaTest : 0.3f);
        return m;
    }

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

    private static bool HasBit(MtlMaterial sub, ulong bit) =>
        ulong.TryParse(sub.GenMask, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var mask) && (mask & bit) != 0;

    private static readonly Lazy<Shader> HairShader = new(() => new Shader { Code = """
        shader_type spatial;
        render_mode blend_mix, depth_draw_opaque, cull_disabled;

        uniform sampler2D albedo_texture : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
        uniform sampler2D mask_texture : hint_default_white, filter_linear_mipmap, repeat_enable;
        uniform sampler2D gloss_texture : hint_default_white, filter_linear_mipmap, repeat_enable;
        uniform sampler2D normal_texture : hint_normal, filter_linear_mipmap_anisotropic, repeat_enable;
        uniform bool has_mask = false;
        uniform bool has_gloss = false;
        uniform bool has_normal_texture = false;
        uniform vec2 uv_scale = vec2(1.0);
        uniform vec2 uv_offset = vec2(0.0);
        uniform vec3 hair_color = vec3(1.0);
        uniform vec3 second_color = vec3(1.0);
        uniform float width1 = 0.0;
        uniform float width2 = 0.0;
        uniform vec3 primary_spec = vec3(1.0);
        uniform float primary_pow = 200.0;
        uniform float primary_shift = 0.08;
        uniform vec3 secondary_spec = vec3(1.0);
        uniform float secondary_pow = 16.0;
        uniform float secondary_shift = 0.54;
        uniform float rim = 0.2;
        uniform float wrap = 0.75;
        uniform float ambient_spec = 1.0;
        uniform float bump_scale = 0.05;
        uniform vec3 diffuse_gain = vec3(0.2);
        uniform vec3 specular_gain = vec3(0.3);
        uniform float saturation = 1.0;
        uniform float detail_tiling = 1.0;
        uniform float alpha_scissor = 0.3;

        varying vec3 tangent1;
        varying vec3 tangent2;
        varying vec3 spec1;
        varying vec3 gloss_colour;
        varying float rim_amount;

        void fragment() {
            vec2 uv = UV * uv_scale + uv_offset;
            vec4 texel = texture(albedo_texture, uv);
            vec3 base = hair_color;
            if (has_mask) {
                vec2 fading = (1.0 - texture(mask_texture, uv).rg) * 0.5;
                float f1 = smoothstep(0.0, max(1.0 - width1, 0.001), clamp(width1 - fading.x, 0.0, 1.0));
                float f2 = smoothstep(0.0, max(1.0 - width2, 0.001), clamp(width2 - fading.y, 0.0, 1.0));
                base = mix(hair_color, second_color, max(f1, f2));
            }
            vec3 colour = texel.rgb * base * 2.0;
            colour = mix(vec3(dot(colour, vec3(0.2126, 0.7152, 0.0722))), colour, saturation);
            ALBEDO = colour * diffuse_gain;
            ALPHA = clamp(texel.a * 2.0, 0.0, 1.0);
            ALPHA_SCISSOR_THRESHOLD = alpha_scissor;
            vec3 n = NORMAL;
            if (has_normal_texture) {
                vec3 nm = texture(normal_texture, uv).rgb * 2.0 - 1.0;
                n = normalize(TANGENT * nm.x + BINORMAL * nm.y + NORMAL * nm.z);
                NORMAL = n;
            }
            vec4 gloss = has_gloss ? texture(gloss_texture, uv) : vec4(1.0);
            gloss_colour = gloss.rgb;
            float shift_map = has_gloss ? texture(gloss_texture, uv * detail_tiling).a : 1.0;
            float shift = ((shift_map * 2.0 - 1.0) * 2.0 - 1.0) * bump_scale;
            // the strand runs along the binormal (view space here)
            vec3 strand = normalize(BINORMAL);
            tangent1 = normalize(strand + (shift + primary_shift) * n);
            tangent2 = normalize(strand + (shift + secondary_shift) * n);
            spec1 = primary_spec * base;
            rim_amount = pow(1.0 - clamp(dot(n, VIEW), 0.0, 1.0), 3.0) * rim; // pow(GetFresnel(N.E, 0, 2), 1.5) * RimMultiplier
            ROUGHNESS = 1.0;
            SPECULAR = 0.0;
        }

        float kajiya(vec3 t, vec3 h, float exponent) {
            float th = dot(t, h);
            return pow(sqrt(max(1.0 - th * th, 0.01)), exponent);
        }

        void light() {
            float ndl = dot(NORMAL, LIGHT);
            float wrapped = clamp(ndl * wrap + (1.0 - wrap), 0.0, 1.0);
            float edl = clamp(dot(LIGHT, VIEW), 0.0, 1.0);
            float diffuse = wrapped + clamp(1.0 - edl, 0.0, 1.0) * rim_amount;
            DIFFUSE_LIGHT += diffuse * ATTENUATION * LIGHT_COLOR / PI;
            vec3 h = normalize(LIGHT + VIEW);
            vec3 s = kajiya(tangent1, h, primary_pow) * spec1 + kajiya(tangent2, h, secondary_pow) * secondary_spec;
            SPECULAR_LIGHT += s * gloss_colour * specular_gain * wrapped * ATTENUATION * LIGHT_COLOR / PI;
        }
        """ });
}
