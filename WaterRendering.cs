using System.Collections.Concurrent;
using System.Globalization;
using System.Xml.Linq;
using Godot;

namespace AAEmu.GodotViewer;

/// <summary>Viewer water features. Every added effect has a command-line/exported switch in <see cref="WorldViewer"/>.</summary>
internal readonly record struct WaterRenderSettings(
    bool Enhanced, bool AnimatedNormals, bool Reflections, bool DepthEffects, bool OceanVertexWaves);

/// <summary>Water shader values read from the water material and, for the ocean, the world's OceanAnimation element.</summary>
internal readonly record struct WaterSurfaceProfile(
    string NormalTexture, Vector2 NormalTiling, float NormalStrength, float SmallWavesScale, float BigWavesScale,
    float WavesSpeed, float FresnelPower, float FresnelBias, float ReflectionAmount, float SunShinePower,
    float FoamAmount, float FoamIntersection, float HeightScale, float WavesAmount, float WavesSize, bool IsOcean,
    float SunMultiplier = 1f, float SubSurfaceScatteringScale = 1f);

/// <summary>Creates the same evidence-backed water treatment for the world ocean and cell-authored rivers and lakes.</summary>
internal static class WaterMaterialFactory
{
    private const string DefaultNormal = "game/textures/defaults/oceanwaves_ddn.dds";
    private const string DefaultOceanMaterial = "game/materials/ocean/ocean.mtl";

    // Defaults are the extracted ocean.mtl + main_world OceanAnimation values. Runtime material/environment values replace
    // these when present, so other worlds remain data-driven.
    public static WaterSurfaceProfile DefaultOceanProfile { get; } = new(
        DefaultNormal, Vector2.One, 1.2f, 1f, 1f, 1f, 4f, 0.05f, 1f, 96f, 2f, 0.7f, 0.2f, 1.6f, 0.75f, true, 1f, 4f);

    // Defaults are the extracted lake_solzreed.mtl values and cover river records whose material field is empty.
    public static WaterSurfaceProfile DefaultInlandProfile { get; } = new(
        DefaultNormal, Vector2.One, 0.5f, 0.5f, 1f, 1.2f, 4f, 0.333f, 0.5f, 90f, 0.75f, 0.25f, 0f, 0f, 1f, false, 0.5f);

    private static readonly ConcurrentDictionary<string, Lazy<WaterSurfaceProfile>> Profiles =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, Lazy<Texture2D?>> NormalTextures =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<(Color Tint, WaterRenderSettings Settings, WaterSurfaceProfile Profile), Lazy<Material>> Materials = [];
    // Zone 179 OceanFogColor is sRGB (21,74,155); shader uniforms carry its linear-light equivalent.
    public static Color DefaultOceanColor { get; } = new(Srgb(21f / 255f), Srgb(74f / 255f), Srgb(155f / 255f), 0.82f);

    private static readonly Lock OceanStateGate = new();
    private static Color _oceanColor = DefaultOceanColor;
    private static Color _oceanScatteringColor = Colors.Transparent;
    private static Vector3 _sunRayDirection = Vector3.Down;
    private static Color _sunColor = Colors.White;
    private static float _sunSpecular = 1f;
    private static Color _skyReflectionColor = Colors.Transparent;
    private static bool _dynamicSkyReflection;
    private static bool _cameraInOcean;

    private const string ShaderCode = """
        shader_type spatial;
        render_mode blend_mix, cull_disabled, diffuse_burley, specular_schlick_ggx, ambient_light_disabled;

        uniform sampler2D depth_texture : hint_depth_texture, repeat_disable, filter_nearest;
        uniform sampler2D wave_normal : hint_normal, repeat_enable, filter_linear_mipmap_anisotropic;
        uniform bool has_wave_normal = false;
        uniform vec4 deep_color;
        uniform vec4 shallow_color;
        uniform vec3 ocean_scattering_color;
        uniform float subsurface_scattering_scale = 1.0;
        uniform vec3 sun_ray_direction;
        uniform vec3 sun_color;
        uniform float sun_specular = 1.0;
        uniform float sun_multiplier = 1.0;
        uniform vec3 sky_reflection_color;
        uniform bool dynamic_sky_reflection = false;
        uniform bool camera_in_ocean = false;
        uniform bool is_ocean = false;
        uniform bool animated_normals = true;
        uniform bool reflections = true;
        uniform bool depth_effects = true;
        uniform bool vertex_waves = false;
        uniform vec2 normal_tiling = vec2(1.0);
        uniform float normal_strength = 0.5;
        uniform float small_waves_scale = 0.5;
        uniform float big_waves_scale = 1.0;
        uniform float waves_speed = 1.0;
        uniform float fresnel_power = 4.0;
        uniform float fresnel_bias = 0.05;
        uniform float reflection_amount = 1.0;
        uniform float sun_shine_power = 64.0;
        uniform float foam_amount = 0.75;
        uniform float foam_intersection = 0.25;
        uniform float height_scale = 0.2;
        uniform float waves_amount = 1.6;
        uniform float waves_size = 0.75;

        varying vec3 world_position;

        vec3 unpack_normal(vec3 encoded) {
            vec2 xy = encoded.rg * 2.0 - 1.0;
            return vec3(xy, sqrt(max(1.0 - dot(xy, xy), 0.0)));
        }

        void vertex() {
            vec3 initial_world = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
            if (vertex_waves) {
                // World OceanAnimation controls phase speed/size/amount; material HeightScale caps displacement.
                float frequency = 0.045 / max(waves_size, 0.05);
                float phase_a = dot(initial_world.xz, normalize(vec2(1.0, 0.70710677))) * frequency + TIME * waves_speed;
                float phase_b = dot(initial_world.xz, normalize(vec2(-0.52, 0.85))) * frequency * 1.47 - TIME * waves_speed * 0.73;
                VERTEX.y += height_scale * waves_amount * (sin(phase_a) * 0.65 + sin(phase_b) * 0.35);
            }
            world_position = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
        }

        void fragment() {
            vec3 world_normal = vec3(0.0, 1.0, 0.0);
            if (animated_normals && has_wave_normal) {
                // Bumpmap TexMod defaults to 1x1; make one repeat a broad 50 m swell, then add finer ripples.
                float t = TIME * waves_speed;
                vec2 base_uv = world_position.xz * normal_tiling * 0.02;
                vec3 big = unpack_normal(texture(wave_normal,
                    base_uv * max(big_waves_scale, 0.01) + vec2(0.013, 0.009) * t).rgb);
                vec3 small = unpack_normal(texture(wave_normal,
                    base_uv * 5.3 * max(small_waves_scale, 0.01) + vec2(-0.021, 0.017) * t).rgb);
                vec2 slope = (big.xy * 0.28 + small.xy * 0.12) * normal_strength;
                world_normal = normalize(vec3(slope.x, sqrt(max(1.0 - min(dot(slope, slope), 0.96), 0.04)), slope.y));
            }

            bool above_surface = FRONT_FACING;
            if (!above_surface && is_ocean && !camera_in_ocean)
                discard;
            if (!above_surface)
                world_normal = -world_normal;
            NORMAL = normalize((VIEW_MATRIX * vec4(world_normal, 0.0)).xyz);

            float shallow_mix = 0.0;
            float foam = 0.0;
            // Opaque depth describes the ground under a top face. It is not meaningful through the underside of water.
            if (depth_effects && above_surface) {
                float raw_depth = textureLod(depth_texture, SCREEN_UV, 0.0).r;
                if (raw_depth > 0.00001) {
                    vec4 scene_position = INV_PROJECTION_MATRIX * vec4(SCREEN_UV * 2.0 - 1.0, raw_depth, 1.0);
                    scene_position.xyz /= scene_position.w;
                    float water_depth = max(VERTEX.z - scene_position.z, 0.0);
                    shallow_mix = 1.0 - smoothstep(0.25, 6.0, water_depth);
                    float foam_band = 1.0 - smoothstep(0.08, max(foam_intersection * 2.0, 0.1), water_depth);
                    float foam_breakup = has_wave_normal
                        ? texture(wave_normal,
                            world_position.xz * 0.08 + vec2(TIME * 0.018, TIME * -0.013) * waves_speed).r
                        : 0.6;
                    foam = clamp(foam_band * smoothstep(0.48, 0.72, foam_breakup) * foam_amount, 0.0, 1.0);
                }
            }

            float facing = clamp(abs(dot(normalize(NORMAL), normalize(VIEW))), 0.0, 1.0);
            float fresnel = clamp(fresnel_bias + (1.0 - fresnel_bias) * pow(1.0 - facing, fresnel_power), 0.0, 1.0);
            vec3 water_color = mix(deep_color.rgb, shallow_color.rgb, shallow_mix);
            if (is_ocean && above_surface) {
                water_color += ocean_scattering_color * subsurface_scattering_scale;
            }
            if (!above_surface)
                water_color = deep_color.rgb;
            if (reflections && above_surface) {
                float reflection_weight = clamp(fresnel * reflection_amount, 0.0, 1.0);
                water_color = mix(water_color,
                    dynamic_sky_reflection ? sky_reflection_color : shallow_color.rgb,
                    reflection_weight);
            }
            water_color = mix(water_color, vec3(0.90, 0.97, 1.0), foam * 0.72);

            vec3 view_world = normalize(CAMERA_POSITION_WORLD - world_position);
            vec3 to_sun = normalize(-sun_ray_direction);
            vec3 half_world = normalize(to_sun + view_world);
            float sun_facing = max(dot(world_normal, to_sun), 0.0);
            float glint = reflections && above_surface
                ? pow(max(dot(world_normal, half_world), 0.0), max(sun_shine_power, 1.0)) *
                  sun_facing * sun_specular * sun_multiplier
                : 0.0;

            ALBEDO = water_color;
            EMISSION = sun_color * min(glint, 2.0) * (above_surface ? 0.85 : 0.25);
            ROUGHNESS = reflections ? clamp(sqrt(2.0 / (sun_shine_power + 2.0)), 0.045, 0.35) : 0.45;
            METALLIC = reflections ? 0.03 : 0.0;
            // The shader already adds its Fresnel sky reflection explicitly; a PBR specular term would add a
            // second, Godot radiance-map reflection on top of the authored Cry ReflectionScale.
            SPECULAR = 0.0;
            float shore_alpha = above_surface ? mix(0.34, deep_color.a, 1.0 - shallow_mix) : 0.88;
            ALPHA = clamp(shore_alpha + (reflections ? fresnel * 0.12 : 0.0) + foam * 0.16, 0.05, 0.96);
        }
        """;

    private static readonly Lazy<Shader> SharedShader = new(() => new Shader { Code = ShaderCode });

    public static WaterSurfaceProfile ProfileFor(string? materialPath)
    {
        var path = CellPaths.MaterialFile(CellPaths.Normalize(materialPath));
        if (path == null)
            return DefaultInlandProfile;
        return Profiles.GetOrAdd(path, p => new Lazy<WaterSurfaceProfile>(() => LoadProfile(p, DefaultInlandProfile)))
            .Value;
    }

    public static WaterSurfaceProfile OceanProfileFor(string worldRoot)
    {
        try
        {
            var xml = PakFiles.ReadText($"{worldRoot}/env.xml");
            if (xml == null)
                return LoadProfile(DefaultOceanMaterial, DefaultOceanProfile) with { IsOcean = true };
            var root = XDocument.Parse(xml).Root;
            var ocean = root?.Element("Ocean");
            var animation = root?.Element("OceanAnimation");
            var material = CellPaths.MaterialFile(CellPaths.Normalize((string?)ocean?.Attribute("Material"))) ?? DefaultOceanMaterial;
            var profile = LoadProfile(material, DefaultOceanProfile);
            return profile with
            {
                WavesSpeed = Attr(animation, "WavesSpeed", profile.WavesSpeed),
                WavesAmount = Attr(animation, "WavesAmount", profile.WavesAmount),
                WavesSize = Attr(animation, "WavesSize", profile.WavesSize),
                IsOcean = true,
            };
        }
        catch (Exception e)
        {
            GD.PrintErr($"{worldRoot}/env.xml water settings: {e.Message}");
            return DefaultOceanProfile;
        }
    }

    public static Material Get(Color tint, WaterRenderSettings settings, WaterSurfaceProfile profile) =>
        Materials.GetOrAdd((tint, settings, profile), static key =>
            new Lazy<Material>(() => Create(key.Tint, key.Settings, key.Profile))).Value;

    /// <summary>
    /// Worker-thread warm-up before the ocean node is posted. This keeps the main thread from waiting on a texture Lazy
    /// whose owner is paused by RenderBudget, while preserving one decode/upload across ocean and cell loaders.
    /// </summary>
    public static void Prepare(WaterSurfaceProfile profile) => _ = GetNormalTexture(profile.NormalTexture);

    private static WaterSurfaceProfile LoadProfile(string materialPath, WaterSurfaceProfile fallback)
    {
        try
        {
            var bytes = PakFiles.Read(materialPath);
            var source = bytes == null ? null : MtlReader.Parse(bytes).ForSubset(0);
            if (source == null)
                return fallback;
            var normal = source.NormalMap?.PakPath ?? fallback.NormalTexture;
            return fallback with
            {
                NormalTexture = normal,
                NormalTiling = source.NormalMap is { } bump
                    ? new Vector2(bump.TileU, bump.TileV)
                    : fallback.NormalTiling,
                NormalStrength = Param(source, "NormalsScale", Param(source, "BumpScale", fallback.NormalStrength)),
                SmallWavesScale = Param(source, "SmallWavesScale", fallback.SmallWavesScale),
                BigWavesScale = Param(source, "BigWavesScale", fallback.BigWavesScale),
                WavesSpeed = Param(source, "WavesSpeed", fallback.WavesSpeed),
                FresnelPower = Param(source, "FresnelPower", fallback.FresnelPower),
                FresnelBias = Param(source, "FresnelBias", fallback.FresnelBias),
                ReflectionAmount = Param(source, "ReflectionScale", Param(source, "ReflectionAmount", fallback.ReflectionAmount)),
                SunShinePower = Param(source, "SunShinePow", fallback.SunShinePower),
                SunMultiplier = Param(source, "SunMultiplier", fallback.SunMultiplier),
                SubSurfaceScatteringScale = Param(source, "SubSurfaceScatteringScale", fallback.SubSurfaceScatteringScale),
                FoamAmount = Param(source, "FoamAmount", Param(source, "FoamMultiplier", fallback.FoamAmount)),
                FoamIntersection = Param(source, "FoamSoftIntersectionFactor", Param(source, "FoamIntersectionFactor", fallback.FoamIntersection)),
                HeightScale = Param(source, "HeightScale", fallback.HeightScale),
            };
        }
        catch (Exception e)
        {
            GD.PrintErr($"{materialPath} water material: {e.Message}");
            return fallback;
        }
    }

    private static float Param(MtlMaterial material, string name, float fallback) =>
        material.PublicParams.TryGetValue(name, out var text) &&
        float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : fallback;

    private static float Attr(XElement? element, string name, float fallback) =>
        float.TryParse((string?)element?.Attribute(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value : fallback;

    private static float Srgb(float value) => value <= 0.04045f ? value / 12.92f : MathF.Pow((value + 0.055f) / 1.055f, 2.4f);

    public static void UpdateOceanEnvironment(Color linearOceanColor, Color linearScatteringColor,
        Vector3 sunRayDirection, Color linearSunColor, float sunSpecularMultiplier,
        Color? linearSkyReflectionColor = null)
    {
        lock (OceanStateGate)
        {
            _oceanColor = linearOceanColor;
            _oceanScatteringColor = linearScatteringColor;
            _sunRayDirection = sunRayDirection.LengthSquared() > 1e-6f ? sunRayDirection.Normalized() : Vector3.Down;
            _sunColor = linearSunColor;
            _sunSpecular = Math.Clamp(sunSpecularMultiplier, 0f, 4f);
            _dynamicSkyReflection = linearSkyReflectionColor.HasValue;
            _skyReflectionColor = linearSkyReflectionColor ?? Colors.Transparent;
            foreach (var (key, lazy) in Materials)
                if (key.Profile.IsOcean && lazy.IsValueCreated && lazy.Value is ShaderMaterial material)
                    ApplyOceanEnvironment(material);
        }
    }

    public static void SetCameraInOcean(bool value)
    {
        lock (OceanStateGate)
        {
            if (_cameraInOcean == value)
                return;
            _cameraInOcean = value;
            foreach (var (key, lazy) in Materials)
                if (key.Profile.IsOcean && lazy.IsValueCreated && lazy.Value is ShaderMaterial material)
                    material.SetShaderParameter("camera_in_ocean", value);
        }
    }

    private static void ApplyOceanEnvironment(ShaderMaterial material)
    {
        material.SetShaderParameter("deep_color", _oceanColor);
        material.SetShaderParameter("ocean_scattering_color", _oceanScatteringColor);
        material.SetShaderParameter("shallow_color", _oceanColor.Lerp(new Color(0.16f, 0.34f, 0.60f, _oceanColor.A), 0.46f));
        material.SetShaderParameter("sun_ray_direction", _sunRayDirection);
        material.SetShaderParameter("sun_color", new Vector3(_sunColor.R, _sunColor.G, _sunColor.B));
        material.SetShaderParameter("sun_specular", _sunSpecular);
        material.SetShaderParameter("dynamic_sky_reflection", _dynamicSkyReflection);
        material.SetShaderParameter("sky_reflection_color", new Vector3(
            _skyReflectionColor.R, _skyReflectionColor.G, _skyReflectionColor.B));
        material.SetShaderParameter("camera_in_ocean", _cameraInOcean);
    }

    private static Material Create(Color tint, WaterRenderSettings settings, WaterSurfaceProfile profile)
    {
        RenderBudget.Acquire();
        if (!settings.Enhanced)
        {
            return new StandardMaterial3D
            {
                AlbedoColor = tint,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                Roughness = 0.05f,
                Metallic = 0.1f,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            };
        }

        var shallow = new Color(
            Mathf.Clamp(tint.R * 1.35f + 0.04f, 0f, 1f),
            Mathf.Clamp(tint.G * 1.28f + 0.05f, 0f, 1f),
            Mathf.Clamp(tint.B * 1.18f + 0.04f, 0f, 1f), tint.A);
        var normal = settings.AnimatedNormals || settings.DepthEffects ? GetNormalTexture(profile.NormalTexture) : null;
        var material = new ShaderMaterial { Shader = SharedShader.Value };
        var deep = profile.IsOcean ? _oceanColor : tint;
        var shallowColor = profile.IsOcean
            ? deep.Lerp(new Color(0.16f, 0.34f, 0.60f, deep.A), 0.46f)
            : shallow;
        material.SetShaderParameter("deep_color", deep);
        material.SetShaderParameter("ocean_scattering_color", _oceanScatteringColor);
        material.SetShaderParameter("subsurface_scattering_scale", profile.SubSurfaceScatteringScale);
        material.SetShaderParameter("shallow_color", shallowColor);
        material.SetShaderParameter("sun_ray_direction", _sunRayDirection);
        material.SetShaderParameter("sun_color", new Vector3(_sunColor.R, _sunColor.G, _sunColor.B));
        material.SetShaderParameter("sun_specular", _sunSpecular);
        material.SetShaderParameter("dynamic_sky_reflection", _dynamicSkyReflection);
        material.SetShaderParameter("sky_reflection_color", new Vector3(
            _skyReflectionColor.R, _skyReflectionColor.G, _skyReflectionColor.B));
        material.SetShaderParameter("sun_multiplier", profile.SunMultiplier);
        material.SetShaderParameter("camera_in_ocean", _cameraInOcean);
        material.SetShaderParameter("is_ocean", profile.IsOcean);
        material.SetShaderParameter("has_wave_normal", normal != null);
        if (normal != null)
            material.SetShaderParameter("wave_normal", normal);
        material.SetShaderParameter("animated_normals", settings.AnimatedNormals);
        material.SetShaderParameter("reflections", settings.Reflections);
        material.SetShaderParameter("depth_effects", settings.DepthEffects);
        material.SetShaderParameter("vertex_waves", settings.OceanVertexWaves && profile.IsOcean);
        material.SetShaderParameter("normal_tiling", profile.NormalTiling);
        material.SetShaderParameter("normal_strength", profile.NormalStrength);
        material.SetShaderParameter("small_waves_scale", profile.SmallWavesScale);
        material.SetShaderParameter("big_waves_scale", profile.BigWavesScale);
        material.SetShaderParameter("waves_speed", profile.WavesSpeed);
        material.SetShaderParameter("fresnel_power", profile.FresnelPower);
        material.SetShaderParameter("fresnel_bias", profile.FresnelBias);
        material.SetShaderParameter("reflection_amount", profile.ReflectionAmount);
        material.SetShaderParameter("sun_shine_power", profile.SunShinePower);
        material.SetShaderParameter("foam_amount", profile.FoamAmount);
        material.SetShaderParameter("foam_intersection", profile.FoamIntersection);
        material.SetShaderParameter("height_scale", profile.HeightScale);
        material.SetShaderParameter("waves_amount", profile.WavesAmount);
        material.SetShaderParameter("waves_size", profile.WavesSize);
        return material;
    }

    private static Texture2D? GetNormalTexture(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        return NormalTextures.GetOrAdd(path, p => new Lazy<Texture2D?>(() => LoadNormalTexture(p))).Value;
    }

    private static Texture2D? LoadNormalTexture(string path)
    {
        try
        {
            var bytes = PakFiles.Read(path);
            if (bytes == null)
                return null;
            var image = new Image();
            if (image.LoadDdsFromBuffer(CryDds.PrepareForGodot(bytes)) != Error.Ok || image.IsEmpty())
                return null;
            RenderBudget.Acquire();
            return ImageTexture.CreateFromImage(image);
        }
        catch (Exception e)
        {
            GD.PrintErr($"{path}: {e.Message}");
            return null;
        }
    }
}
