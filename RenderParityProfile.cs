using Godot;

namespace AAEmu.GodotViewer;

/// <summary>
/// Optional rendering translations which use values already parsed from CryEngine environment and material files.
/// Keeping this profile separate makes its visual and GPU cost changes easy to disable for comparisons.
/// </summary>
internal static class RenderParityProfile
{
    public static void ConfigureEnvironment(Godot.Environment environment, bool ssao, bool ssr, bool glow, bool bicubicGlow)
    {
        environment.ReflectedLightSource = Godot.Environment.ReflectionSource.Sky;
        // Cry's full film curve cannot be expressed by one white point; Filmic retains the legacy viewer's contrast.
        environment.TonemapMode = Godot.Environment.ToneMapper.Filmic;
        environment.TonemapExposure = 1f;
        environment.SsaoEnabled = ssao;
        environment.SsrEnabled = ssr;
        environment.GlowEnabled = glow;
        // The client's HDR bloom (postprocess.cfx): a bright pass on the quarter-resolution scene, blurred at 1/4, 1/8 and
        // 1/16 resolution, composed with the weights 2.0 / 1.15 / 0.45 (ComposeFinalHDRGlow) and added to the exposed
        // scene before the film curve (FilmicMapping: exposure * scene + bloom * 0.5). Godot's additive glow is the same
        // pre-tonemap addition; its levels 2..4 are the 1/4..1/16 resolution blurs.
        environment.GlowBlendMode = Godot.Environment.GlowBlendModeEnum.Additive;
        environment.GlowNormalized = false;
        environment.GlowStrength = 1f;
        environment.GlowBloom = 0f;
        for (var level = 0; level < 7; level++)
            environment.SetGlowLevel(level, BloomLevelWeights.TryGetValue(level, out var weight) ? weight : 0f);
        RenderingServer.EnvironmentGlowSetUseBicubicUpscale(bicubicGlow);

        // A modest aerial component lets distance fog retain sky and sun colour instead of becoming a flat overlay.
        environment.FogAerialPerspective = 0.35f;
        environment.FogSunScatter = 0.12f;
        environment.AmbientLightSkyContribution = 0.5f;
    }

    public static void ApplyEnvironmentSample(Godot.Environment environment, DirectionalLight3D sun,
        ProceduralSkyMaterial sky, EnvironmentSample sample)
    {
        ApplyBloom(environment, sample);
        environment.SsaoIntensity = (float)Math.Clamp(sample.SsdoAmount * sample.SsdoAmbientAmount, 0.0, 4.0);
        environment.AdjustmentEnabled = true;
        environment.AdjustmentSaturation = (float)Math.Clamp(sample.HdrCurveSaturation * sample.ColorSaturation, 0.0, 4.0);
        environment.AdjustmentContrast = (float)Math.Clamp(sample.ColorContrast, 0.0, 4.0);
        environment.AdjustmentBrightness = (float)Math.Clamp(sample.ColorBrightness, 0.0, 4.0);

        var sunColor = FromSource(sample.SunColor);
        var sunMax = Math.Max(sample.SunColor.X, Math.Max(sample.SunColor.Y, sample.SunColor.Z));
        sun.LightColor = sunColor;
        sun.LightEnergy = (float)Math.Clamp(sample.SunIntensity * sunMax * LightScale, 0.01, 4.0);
        sun.LightSpecular = (float)Math.Clamp(sample.SunSpecularMultiplier, 0.0, 8.0);
        sun.ShadowOpacity = (float)Math.Clamp(sample.ShadowIntensity, 0.0, 1.0);

        environment.FogLightColor = FromSource(sample.FogColor);
        // Ramp influence is a weight on Cry's global fog density, not a normalized opacity. Passing it directly as
        // Godot's depth-fog density (often 0.5-0.7) turns the horizon into a nearly opaque color wash.
        environment.FogMode = Godot.Environment.FogModeEnum.Depth;
        environment.FogDepthBegin = (float)Math.Max(0.0, sample.FogRampStart);
        environment.FogDepthEnd = (float)Math.Max(environment.FogDepthBegin + 1.0, sample.FogRampEnd);
        environment.FogDepthCurve = 1f;
        environment.FogDensity = (float)Math.Clamp(sample.FogDensity * sample.FogRampInfluence, 0.0, 0.45);
        environment.AmbientLightColor = FromSource(sample.AmbientColor);
        environment.AmbientLightEnergy = 1f;
        environment.AmbientLightSkyContribution = 0.5f;

        // Sky color is an ambient-light tint. The visible static sky is the zone's authored cube; where its
        // cloud alpha is transparent, the blue fog color supplies a data-driven clear-sky base.
        sky.SkyTopColor = Normalized(FromSource(sample.FogColor), 0.55f);
        sky.SkyHorizonColor = Normalized(FromSource(sample.FogColor), 0.45f)
            .Lerp(Normalized(FromSource(sample.FogTopColor), 0.35f), 0.35f);
        sky.GroundHorizonColor = sky.SkyHorizonColor;
        sky.GroundBottomColor = FromSource(sample.AmbientColor) * 0.4f;

        // Godot exposes one exponential height layer. Match the source bottom/top density ratio over its height span.
        if (sample.FogTopHeight > sample.FogBottomHeight && sample.FogBottomLayerDensity > 0 && sample.FogTopLayerDensity > 0)
        {
            environment.FogHeight = (float)sample.FogBottomHeight;
            environment.FogHeightDensity = (float)Math.Clamp(
                Math.Log(sample.FogBottomLayerDensity / sample.FogTopLayerDensity) /
                (sample.FogTopHeight - sample.FogBottomHeight), -1.0, 1.0);
        }

        // Godot has no Cry-style Rayleigh/Mie sky model here. Approximate the Mie sun halo through fog scatter.
        if (string.IsNullOrWhiteSpace(sample.SkyboxMaterial))
        {
            var scattering = Math.Max(0, sample.MieScattering) + Math.Max(0, sample.RayleighScattering);
            var mieShare = scattering > 1e-6 ? Math.Max(0, sample.MieScattering) / scattering : 0.25;
            environment.FogSunScatter = (float)Math.Clamp(0.05 + mieShare * 0.3, 0.05, 0.35);
        }
        else
            environment.FogSunScatter = 0f; // CryEngine ignores dynamic sky scattering controls when a static sky is used.
    }

    public static Color FromSource(System.Numerics.Vector3 color) => new(color.X, color.Y, color.Z);

    /// <summary>
    /// Godot light units per client light unit: the sun's energy is its TOD colour x multiplier times this (about 6.2 x
    /// 0.16 = 1 at noon). Every other client light term (the ambient) takes the same factor so their ratio is kept.
    /// </summary>
    public const double LightScale = 0.16;

    // ComposeFinalHDRGlow weights of the 1/4, 1/8 and 1/16 resolution bloom maps, keyed by Godot glow level index.
    private static readonly Dictionary<int, float> BloomLevelWeights = new() { [1] = 2.0f, [2] = 1.15f, [3] = 0.45f };

    // Client cvar dump (C:\AA\consolecommandsandvars.txt): r_HDRLevel 8, r_HDRBrightThreshold 6, r_HDRBrightOffset 5,
    // r_HDRBloomMul 0.2.
    private const float HdrLevel = 8f, HdrBrightThreshold = 6f, HdrBrightOffset = 5f, HdrBloomMul = 0.2f;

    /// <summary>
    /// The client's bloom for one environment sample. HDRBrightPassFilter keeps, per channel, what exceeds
    /// r_HDRBrightThreshold / r_HDRLevel (0.75) of the adapted scene level and compresses it with
    /// y / (r_HDRBrightOffset + y); the final pass multiplies the blurred result by env.xml HDRSetup BloomColor x BloomMul.
    /// In daylight the adapted level is the bright sky, so only what renders brighter than the sky (sun, glints, hot
    /// specular, additive effects) blooms; here that level is the zone's film white point, the value this environment
    /// maps to display white.
    /// </summary>
    public static void ApplyBloom(Godot.Environment environment, EnvironmentSample sample)
    {
        var white = (float)Math.Clamp(sample.FilmCurveWhitepoint, 1.0, 16.0);
        environment.GlowHdrThreshold = white * HdrBrightThreshold / HdrLevel;
        // Soft knee over r_HDRBrightOffset / r_HDRLevel of white, the span in which y / (offset + y) rises to one half.
        environment.GlowHdrScale = white * HdrBrightOffset / HdrLevel;
        // y / (offset + y) saturates: very bright sources (the sun disc) cannot outweigh the rest of the bloom.
        environment.GlowHdrLuminanceCap = white * (HdrBrightThreshold + HdrBrightOffset) / HdrLevel * 2f;
        environment.GlowIntensity = (float)Math.Clamp(sample.BloomMultiplier * HdrBloomMul * BloomGain, 0.0, 8.0);
        environment.GlowMap = CreateGlowMap(sample.BloomColor);
        environment.GlowMapStrength = 1f;
    }

    /// <summary>Scale between the client's bloom maps and Godot's glow buffer (the bright pass's x8 range, 3/8 and 0.5).</summary>
    private const float BloomGain = 8f * 3f / 8f * 0.5f;

    private static Color Normalized(Color color, float scale)
    {
        var max = Math.Max(color.R, Math.Max(color.G, Math.Max(color.B, 1e-4f)));
        return new Color(color.R / max * scale, color.G / max * scale, color.B / max * scale);
    }

    public static float SkyboxEnergy(EnvironmentSample sample) =>
        (float)Math.Clamp(sample.SkyboxMultiplier, 0.0, 4.0);

    private static Texture2D CreateGlowMap(System.Numerics.Vector3 color)
    {
        var image = Image.CreateEmpty(1, 1, false, Image.Format.Rgbf);
        image.SetPixel(0, 0, new Color(color.X, color.Y, color.Z));
        return ImageTexture.CreateFromImage(image);
    }

    /// <summary>
    /// Best-fit conversion from Cry's Blinn-Phong material values to Godot's PBR controls. Specular RGB must collapse
    /// to Godot's dielectric scalar; emissive RGB and GlowAmount map directly to emission colour and energy.
    /// </summary>
    public static void ApplyMaterial(StandardMaterial3D material, MtlMaterial source, Texture2D glowTexture)
    {
        // MultiMesh instance colours carry vegetation.xml fBrightness; white is neutral for all other instances.
        material.VertexColorUseAsAlbedo = true;
        material.VertexColorIsSrgb = false; // instance tint colours were explicitly converted from cover sRGB to linear.
        if (source.Shininess > 0)
            material.Roughness = Mathf.Clamp(MathF.Sqrt(2f / (source.Shininess + 2f)), 0.06f, 1f);

        var specularMax = Math.Max(source.SpecularColor.X, Math.Max(source.SpecularColor.Y, source.SpecularColor.Z));
        material.MetallicSpecular = Mathf.Clamp(specularMax * source.SpecularLevel, 0f, 1f);

        var emissionMax = Math.Max(source.EmissiveColor.X, Math.Max(source.EmissiveColor.Y, source.EmissiveColor.Z));
        var glowAmount = Math.Max(0f, source.GlowAmount);
        var emittance = source.Shader.Equals("Illum", StringComparison.OrdinalIgnoreCase)
            ? Math.Max(PublicScalar(source, "Emittance"), PublicScalar(source, "Glow"))
            : 0f;
        if (emissionMax <= 0.001f && glowAmount <= 0.001f && emittance <= 0.001f)
            return;
        material.EmissionEnabled = true;
        if (emissionMax > 0.001f)
        {
            material.Emission = new Color(source.EmissiveColor.X / emissionMax,
                source.EmissiveColor.Y / emissionMax, source.EmissiveColor.Z / emissionMax);
            material.EmissionEnergyMultiplier = emissionMax * Math.Max(1f, Math.Max(glowAmount, emittance));
        }
        else
        {
            // Cry Glow Amount bleeds the diffuse texture's RGB; Illum decal textures may override this source.
            material.Emission = Colors.White;
            material.EmissionTexture = glowTexture;
            material.EmissionEnergyMultiplier = Math.Max(1f, 1f + Math.Max(glowAmount, emittance));
        }
        if (glowTexture != null && (glowAmount > 0.001f || emittance > 0.001f))
        {
            material.EmissionTexture = glowTexture;
            material.EmissionOperator = BaseMaterial3D.EmissionOperatorEnum.Multiply;
        }
    }

    private static float PublicScalar(MtlMaterial source, string name) =>
        source.PublicParams.TryGetValue(name, out var text) &&
        float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? Math.Max(0f, value)
            : 0f;

}
