using System.Collections.Concurrent;
using System.Globalization;
using Godot;

namespace AAEmu.GodotViewer;

/// <summary>
/// The client's Illum shader (illum.cfx, fraglib.cfi) for the surfaces of equipment on characters (armour, cloth,
/// leather, weapons), in Godot. A plain StandardMaterial shows only the diffuse texture: the client also adds a
/// specular highlight tinted by the gloss (Specular slot) map and, for the metals, a reflection of the material's own
/// environment cube map (Environment slot, mostly a warm blurred beach). Without them gold armour is dark bronze and
/// picks up the blue of the lobby sky.
/// <list type="bullet">
/// <item>Diffuse: <c>lightPass * diffuseMap * MatDifColor * DiffuseModifierInHDR</c>.</item>
/// <item>Specular: <c>Phong(reflect, light, Shininess) * light specular * specularMap * X2CustomSpecularForActor *
///   MatSpecColor * SpecularModifierInHDR</c>. %SPLIT_GLOSS_MAP: the map's red channel is the specular amount and its green
///   channel the reflection mask; otherwise its colour is both.</item>
/// <item>Reflection (%ENVCMSPEC): <c>cube(reflect) * ReflectAmount * (FresnelBias + pow(1 - N.V, 5) * FresnelScale) *
///   (ambient + diffuse light) * glossMask</c>. The cube is sampled with the reflection vector in CryEngine world axes.</item>
/// </list>
/// Materials without a gloss map get the client's emulated glossiness (diffuse luminance^8) and no reflection.
/// </summary>
internal static class CharacterIllum
{
    private static float Env(string name, float fallback) =>
        float.TryParse(System.Environment.GetEnvironmentVariable(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    /// <summary>
    /// Illum Diffuse x DiffuseModifierInHDR is 0.216 on most equipment (median over 7,482 sub-materials of three races).
    /// The client's lights bring that back to display level; the lobby's lights were tuned for textures shown at full
    /// brightness, so the product is scaled by 1 / 0.216 to keep the overall level.
    /// </summary>
    public static readonly float DiffuseScale = Env("GV_ILLUM_GAIN", 4.6f);

    /// <summary>Scale of the specular highlight (Specular x SpecularLevel x SpecularModifierInHDR is 2.0 on most equipment).</summary>
    public static readonly float SpecularScale = Env("GV_ILLUM_SPEC", 0.6f);

    /// <summary>
    /// Scale of the environment reflection, which is lit like the diffuse (the client multiplies it by ambient + diffuse light;
    /// here it rides on the albedo). Calibrated against the gold armour of the race page.
    /// </summary>
    public static readonly float ReflectScale = Env("GV_ILLUM_REFLECT", 3.0f);

    /// <summary>Unlit share of the reflection (see the shader).</summary>
    public static readonly float ReflectGlow = Env("GV_ILLUM_GLOW", 0.8f);

    /// <summary>X2CustomBalanceParamsA.w / 3: how much of the diffuse colour goes into an actor's specular colour (a guess).</summary>
    public static readonly float ActorSpecular = Env("GV_ILLUM_ACTORSPEC", 0.33f);

    public sealed record IllumParameters(Vector3 Diffuse, Vector3 Specular, float Shininess, float Reflect, float FresnelBias,
        float FresnelScale, bool HasGloss, bool SplitGloss, bool HasEnv, bool GlossAlphaPower)
    {
        public string Key => string.Create(CultureInfo.InvariantCulture,
            $"{V(Diffuse)}|{V(Specular)}|{Shininess:F1}|{Reflect:F2}|{FresnelBias:F3}|{FresnelScale:F3}|{HasGloss}|{SplitGloss}|{HasEnv}|{GlossAlphaPower}");

        private static string V(Vector3 v) => string.Create(CultureInfo.InvariantCulture, $"{v.X:F3},{v.Y:F3},{v.Z:F3}");
    }

    public static bool IsIllum(MtlMaterial sub) => sub != null && sub.Shader.Equals("Illum", StringComparison.OrdinalIgnoreCase);

    public static IllumParameters Read(MtlMaterial sub)
    {
        var hdr = HasBit(sub, 0x4);
        var diffuseModifier = hdr ? Scalar(sub, "DiffuseModifierInHDR", 1f) : 1f;
        var specularModifier = hdr ? Scalar(sub, "SpecularModifierInHDR", 1f) : 1f;
        var diffuse = new Vector3(sub.DiffuseColor.X, sub.DiffuseColor.Y, sub.DiffuseColor.Z) * diffuseModifier * DiffuseScale;
        var specular = new Vector3(sub.SpecularColor.X, sub.SpecularColor.Y, sub.SpecularColor.Z) * Math.Max(sub.SpecularLevel, 0f) * specularModifier * SpecularScale;
        return new IllumParameters(diffuse, specular, sub.Shininess > 0 ? sub.Shininess : 10f, Scalar(sub, "ReflectAmount", 0f),
            Scalar(sub, "FresnelBias", 0f), Scalar(sub, "FresnelScale", 0f),
            HasBit(sub, 0x10) && sub.SpecularMap != null, HasBit(sub, 0x8), HasBit(sub, 0x80) && sub.Texture("Environment") != null,
            HasBit(sub, 0x800));
    }

    public static ShaderMaterial Create(IllumParameters p, Texture2D albedo, Texture2D gloss, Texture2D normal, Cubemap cube,
        Vector2 uvScale, Vector2 uvOffset, bool twoSided, float alphaScissor, bool blend)
    {
        var m = new ShaderMaterial { Shader = IllumShader(twoSided, alphaScissor > 0, blend) };
        m.SetShaderParameter("albedo_texture", albedo);
        m.SetShaderParameter("uv_scale", uvScale);
        m.SetShaderParameter("uv_offset", uvOffset);
        m.SetShaderParameter("has_gloss", gloss != null && p.HasGloss);
        if (gloss != null)
            m.SetShaderParameter("gloss_texture", gloss);
        m.SetShaderParameter("split_gloss", p.SplitGloss);
        m.SetShaderParameter("has_normal_texture", normal != null);
        if (normal != null)
            m.SetShaderParameter("normal_texture", normal);
        m.SetShaderParameter("has_env", cube != null && p.HasEnv);
        if (cube != null)
            m.SetShaderParameter("env_cube", cube);
        m.SetShaderParameter("diffuse_gain", p.Diffuse);
        m.SetShaderParameter("specular_gain", p.Specular);
        m.SetShaderParameter("shininess", p.Shininess);
        m.SetShaderParameter("reflect_amount", p.Reflect);
        m.SetShaderParameter("fresnel_bias", p.FresnelBias);
        m.SetShaderParameter("fresnel_scale", p.FresnelScale);
        m.SetShaderParameter("reflect_gain", ReflectScale);
        m.SetShaderParameter("reflect_glow", ReflectGlow);
        m.SetShaderParameter("actor_specular", ActorSpecular);
        if (alphaScissor > 0)
            m.SetShaderParameter("alpha_scissor", alphaScissor);
        return m;
    }

    private static float Scalar(MtlMaterial sub, string name, float fallback) =>
        sub.PublicParams.TryGetValue(name, out var text) &&
        float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && float.IsFinite(value)
            ? value : fallback;

    private static bool HasBit(MtlMaterial sub, ulong bit) =>
        ulong.TryParse(sub.GenMask, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var mask) && (mask & bit) != 0;

    private static readonly ConcurrentDictionary<(bool, bool, bool), Lazy<Shader>> Shaders = new();

    private static Shader IllumShader(bool twoSided, bool scissor, bool blend) => Shaders.GetOrAdd((twoSided, scissor, blend), k => new Lazy<Shader>(() =>
        new Shader
        {
            Code = ShaderSource
                .Replace("/*CULL*/", k.Item1 ? ", cull_disabled" : "")
                .Replace("/*ALPHA*/", k.Item2 ? "ALPHA = texel.a; ALPHA_SCISSOR_THRESHOLD = alpha_scissor;" : k.Item3 ? "ALPHA = texel.a * opacity;" : "")
                .Replace("/*DEPTH*/", k.Item3 ? "depth_draw_alpha_prepass" : "depth_draw_opaque"),
        })).Value;

    private const string ShaderSource = """
        shader_type spatial;
        render_mode blend_mix, /*DEPTH*//*CULL*/;

        uniform sampler2D albedo_texture : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
        uniform sampler2D gloss_texture : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
        uniform sampler2D normal_texture : hint_normal, filter_linear_mipmap_anisotropic, repeat_enable;
        uniform samplerCube env_cube : source_color, filter_linear_mipmap;
        uniform bool has_gloss = false;
        uniform bool split_gloss = false;
        uniform bool has_normal_texture = false;
        uniform bool has_env = false;
        uniform vec2 uv_scale = vec2(1.0);
        uniform vec2 uv_offset = vec2(0.0);
        uniform vec3 diffuse_gain = vec3(1.0);
        uniform vec3 specular_gain = vec3(1.0);
        uniform float shininess = 10.0;
        uniform float reflect_amount = 0.0;
        uniform float fresnel_bias = 0.0;
        uniform float fresnel_scale = 0.0;
        uniform float reflect_gain = 1.0;
        uniform float reflect_glow = 0.0;
        uniform float actor_specular = 0.33;
        uniform float alpha_scissor = 0.5;
        uniform float opacity = 1.0;

        varying vec3 spec_amount;

        void fragment() {
            vec2 uv = UV * uv_scale + uv_offset;
            vec4 texel = texture(albedo_texture, uv);
            ALBEDO = texel.rgb * diffuse_gain;
            /*ALPHA*/
            vec3 n = NORMAL;
            if (has_normal_texture) {
                vec3 nm = texture(normal_texture, uv).rgb * 2.0 - 1.0;
                n = normalize(TANGENT * nm.x + BINORMAL * nm.y + NORMAL * nm.z);
                NORMAL = n;
            }
            float luma = dot(texel.rgb, vec3(0.2126, 0.7152, 0.0722));
            vec3 amount;
            vec3 mask;
            if (has_gloss) {
                vec4 g = texture(gloss_texture, uv);
                amount = split_gloss ? vec3(g.r) : g.rgb;
                mask = split_gloss ? vec3(g.g) : g.rgb;
                // X2CustomSpecularForActor: the diffuse colour (over-saturated) leaks into the specular colour
                vec3 saturated = mix(vec3(luma), texel.rgb, 3.0);
                vec3 from_diffuse = 0.02 + (saturated + amount) * 0.5 * 0.98;
                amount = mix(amount, from_diffuse, actor_specular);
            } else {
                float l2 = luma * luma;
                l2 *= l2;
                l2 *= l2;
                float emulated = 0.5 * mix(0.05, 1.0, clamp(l2, 0.0, 1.0));
                vec3 saturated = mix(vec3(luma), texel.rgb, 3.0);
                vec3 from_diffuse = 0.02 + (saturated + vec3(0.5 * emulated)) * 0.5 * 0.98;
                amount = mix(vec3(0.5 * emulated), from_diffuse, actor_specular);
                mask = vec3(0.0); // actors get no reflection without a gloss map
            }
            spec_amount = amount;
            vec3 reflection = vec3(0.0);
            if (has_env && reflect_amount > 0.0) {
                float ndv = clamp(dot(n, VIEW), 0.0, 1.0);
                float fresnel = fresnel_bias + pow(1.0 - ndv, 5.0) * fresnel_scale;
                vec3 world_r = (INV_VIEW_MATRIX * vec4(reflect(-VIEW, n), 0.0)).xyz;
                // the cube is authored in CryEngine world axes (Z up, +Y north): Godot (x, y, z) is Cry (x, -z, y)
                vec3 env = texture(env_cube, vec3(world_r.x, -world_r.z, world_r.y)).rgb;
                reflection = env * reflect_amount * fresnel * mask;
            }
            // The client lights the reflection like the diffuse (cReflect = env * (ambient + diffuse light)): adding it to the
            // albedo makes Godot's own ambient and per-light diffuse do the same, so it dims in shadow and at night.
            ALBEDO += reflection * reflect_gain;
            // Plus a little unlit part: the client's characters also get a virtual character light (VirtuaCLDiffuse/VirtualCLSpecular in
            // the materials) so they never go black; the lobby's lights leave legs and backs dark.
            EMISSION += reflection * reflect_glow;
            ROUGHNESS = 1.0;
            SPECULAR = 0.0;
        }

        void light() {
            float ndl = clamp(dot(NORMAL, LIGHT), 0.0, 1.0);
            DIFFUSE_LIGHT += ndl * ATTENUATION * LIGHT_COLOR / PI;
            vec3 r = reflect(-VIEW, NORMAL);
            float phong = pow(clamp(dot(LIGHT, r), 0.0, 1.0), max(shininess, 1.0));
            vec3 lit = ATTENUATION * LIGHT_COLOR / PI;
            SPECULAR_LIGHT += phong * spec_amount * specular_gain * lit * SPECULAR_AMOUNT;
        }
        """;
}
