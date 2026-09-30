using Godot;

namespace AAEmu.GodotViewer;

/// <summary>
/// A zone's CryEngine skybox as a Godot sky. The zone's sky material (Shader="Sky") names "…/box_12.dds"; its siblings
/// "box_34.dds" and "box_5.dds" complete the box. box_12 and box_34 each hold two side faces: the top half is one face,
/// the bottom half the next face stored upside down (horizon in the middle of the texture); box_5 is the top. The box only
/// covers the upper hemisphere; below the horizon the sky fades to the fog colour. Face order (verified by matching the
/// edges): turning right from box_12-top comes box_12-bottom, box_34-top, box_34-bottom; box_5's edges meet them in turn.
/// </summary>
internal static class ZoneSky
{
    private const string ShaderCode = """
        shader_type sky;

        uniform sampler2D sky_12 : source_color, filter_linear_mipmap, repeat_disable;
        uniform sampler2D sky_34 : source_color, filter_linear_mipmap, repeat_disable;
        uniform sampler2D sky_5 : source_color, filter_linear_mipmap, repeat_disable;
        uniform float angle = 0.0;
        uniform float energy = 1.0;
        uniform vec3 clear_sky_color = vec3(0.3, 0.55, 0.8);
        uniform float opacity = 0.5;
        uniform float alpha_saturation = 1.0;
        uniform vec3 cloud_sun_color = vec3(1.0);
        uniform float sun_color_multiplier = 1.0;
        uniform vec3 horizon_color : source_color = vec3(0.6, 0.7, 0.8);
        uniform vec3 night_horizon_color : source_color = vec3(0.03, 0.05, 0.08);
        uniform vec3 night_zenith_color : source_color = vec3(0.02, 0.04, 0.12);
        uniform float night_zenith_shift = 0.5;
        uniform float star_intensity = 0.0;

        float star_hash(vec3 p) {
            p = fract(p * 0.1031);
            p += dot(p, p.yzx + 33.33);
            return fract((p.x + p.y) * p.z);
        }

        void sky() {
            vec3 d = vec3(EYEDIR.x, -EYEDIR.z, EYEDIR.y); // Cry axes: x east, y north, z up
            float c = cos(angle);
            float s = sin(angle);
            d.xy = vec2(c * d.x - s * d.y, s * d.x + c * d.y);
            vec3 color = horizon_color;
            if (d.z > 0.0) {
                float side = max(abs(d.x), abs(d.y));
                vec4 texel;
                if (d.z >= side) {
                    vec2 p = d.xy / d.z;
                    texel = textureLod(sky_5, (p + 1.0) * 0.5, 0.0);
                } else {
                    vec3 p = d / side;
                    float h = clamp(p.z, 0.0, 1.0);
                    if (abs(d.y) >= abs(d.x)) {
                        texel = d.y > 0.0
                            ? textureLod(sky_12, vec2((p.x + 1.0) * 0.5, (1.0 - h) * 0.5), 0.0)
                            : textureLod(sky_34, vec2((1.0 - p.x) * 0.5, (1.0 - h) * 0.5), 0.0);
                    } else {
                        texel = d.x > 0.0
                            ? textureLod(sky_12, vec2((1.0 - p.y) * 0.5, 0.5 + h * 0.5), 0.0)
                            : textureLod(sky_34, vec2((p.y + 1.0) * 0.5, 0.5 + h * 0.5), 0.0);
                    }
                }
                float cloud_alpha = clamp(texel.a * alpha_saturation, 0.0, 1.0) * opacity;
                color = mix(clear_sky_color, texel.rgb * energy * cloud_sun_color * sun_color_multiplier, cloud_alpha);
                color = mix(horizon_color, color, smoothstep(0.0, 0.06, d.z));
            }
            float night = clamp(star_intensity / 3.0, 0.0, 1.0);
            float zenith = pow(clamp(d.z, 0.0, 1.0), mix(2.0, 0.35, clamp(night_zenith_shift, 0.0, 1.0)));
            vec3 night_color = mix(night_horizon_color, night_zenith_color, zenith);
            color = mix(color * energy, night_color, night);
            if (d.z > 0.02 && star_intensity > 0.0) {
                float star = smoothstep(0.9985, 1.0, star_hash(floor(d * 900.0))) * star_intensity;
                color += vec3(star);
            }
            COLOR = color;
        }
        """;

    private static Shader _shader;
    private static readonly Dictionary<string, ShaderMaterial> Cache = [];

    /// <summary>Gets the Cry sky material's authored intensity, separate from the TOD skybox multiplier.</summary>
    public static float ColorMultiplier(ModelLibrary models, string skyMaterial)
    {
        var parameters = models.Mtl(skyMaterial)?.ForSubset(0)?.PublicParams;
        return parameters != null && parameters.TryGetValue("SkyColorMultiplier", out var text) &&
               float.TryParse(text, System.Globalization.NumberStyles.Float,
                   System.Globalization.CultureInfo.InvariantCulture, out var multiplier)
            ? Math.Clamp(multiplier, 0f, 8f)
            : 1f;
    }

    /// <summary>Gets the Cry sky material's authored direct-sun color multiplier.</summary>
    public static float SunColorMultiplier(ModelLibrary models, string skyMaterial)
    {
        var parameters = models.Mtl(skyMaterial)?.ForSubset(0)?.PublicParams;
        return parameters != null && parameters.TryGetValue("SunColorMultiplier", out var text) &&
               float.TryParse(text, System.Globalization.NumberStyles.Float,
                   System.Globalization.CultureInfo.InvariantCulture, out var multiplier)
            ? Math.Clamp(multiplier, 0f, 8f)
            : 1f;
    }

    /// <summary>Main thread. The sky for a zone's sky material path, or null when its textures aren't there.</summary>
    public static ShaderMaterial Load(ModelLibrary models, string skyMaterial)
    {
        if (string.IsNullOrEmpty(skyMaterial))
            return null;
        if (Cache.TryGetValue(skyMaterial, out var cached))
            return cached;
        ShaderMaterial material = null;
        var diffuse = models.Mtl(skyMaterial)?.ForSubset(0)?.DiffuseMap?.PakPath;
        if (diffuse != null && diffuse.Contains("_12", StringComparison.Ordinal))
        {
            var t12 = Texture(diffuse);
            var t34 = Texture(diffuse.Replace("_12", "_34"));
            var t5 = Texture(diffuse.Replace("_12", "_5"));
            if (t12 != null && t34 != null && t5 != null)
            {
                var source = models.Mtl(skyMaterial)?.ForSubset(0);
                _shader ??= new Shader { Code = ShaderCode };
                material = new ShaderMaterial { Shader = _shader };
                material.SetShaderParameter("sky_12", t12);
                material.SetShaderParameter("sky_34", t34);
                material.SetShaderParameter("sky_5", t5);
                material.SetShaderParameter("opacity", source?.Opacity ?? 1f);
                material.SetShaderParameter("alpha_saturation", Param(source, "AlphaSaturation", 1f));
            }
        }
        return Cache[skyMaterial] = material;
    }

    private static float Param(MtlMaterial? material, string name, float fallback) =>
        material != null && material.PublicParams.TryGetValue(name, out var text) &&
        float.TryParse(text, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : fallback;

    private static Texture2D Texture(string pakPath)
    {
        var bytes = PakFiles.Read(pakPath);
        if (bytes == null)
            return null;
        var image = new Image();
        return image.LoadDdsFromBuffer(CryDds.PrepareForGodot(bytes)) == Error.Ok && !image.IsEmpty()
            ? ImageTexture.CreateFromImage(image)
            : null;
    }
}
