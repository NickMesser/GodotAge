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
        // No Cry bright-pass threshold is serialized in this world's environment files; keep Godot's documented 1.0 threshold.
        environment.GlowHdrThreshold = 1f;
        environment.GlowBloom = 0f;
        RenderingServer.EnvironmentGlowSetUseBicubicUpscale(bicubicGlow);

        // A modest aerial component lets distance fog retain sky and sun colour instead of becoming a flat overlay.
        environment.FogAerialPerspective = 0.35f;
        environment.FogSunScatter = 0.12f;
        environment.AmbientLightSkyContribution = 0.5f;
    }

    public static void ApplyEnvironmentSample(Godot.Environment environment, DirectionalLight3D sun,
        ProceduralSkyMaterial sky, EnvironmentSample sample)
    {
        environment.GlowIntensity = (float)Math.Clamp(sample.BloomMultiplier, 0.0, 8.0);
        environment.GlowMap = CreateGlowMap(sample.BloomColor);
        environment.GlowMapStrength = 1f;
        environment.SsaoIntensity = (float)Math.Clamp(sample.SsdoAmount * sample.SsdoAmbientAmount, 0.0, 4.0);
        environment.AdjustmentEnabled = true;
        environment.AdjustmentSaturation = (float)Math.Clamp(sample.HdrCurveSaturation * sample.ColorSaturation, 0.0, 4.0);
        environment.AdjustmentContrast = (float)Math.Clamp(sample.ColorContrast, 0.0, 4.0);
        environment.AdjustmentBrightness = (float)Math.Clamp(sample.ColorBrightness, 0.0, 4.0);

        var sunColor = FromSource(sample.SunColor);
        var sunMax = Math.Max(sample.SunColor.X, Math.Max(sample.SunColor.Y, sample.SunColor.Z));
        sun.LightColor = sunColor;
        sun.LightEnergy = (float)Math.Clamp(sample.SunIntensity * sunMax * 0.16, 0.01, 4.0);
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
