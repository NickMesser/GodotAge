using Godot;

namespace AAEmu.GodotViewer;

/// <summary>
/// Cheap analytic approximation of CryEngine 3's SkyHDR dome. It evaluates one Rayleigh/Mie scattering expression per
/// sky pixel; there are no LUTs, compute passes, or per-frame CPU updates. Cry's static Sky material is intentionally not
/// composited here: SkyHDR and the static skybox are separate renderer modes, and the supplied cube alpha is mostly
/// opaque painted sky rather than a reliable cloud mask.
/// </summary>
internal static class DynamicSky
{
    private const string ShaderCode = """
        shader_type sky;
        render_mode disable_fog;

        uniform vec3 sun_direction = vec3(0.0, 1.0, 0.0);
        uniform vec3 sun_color = vec3(1.0);
        uniform float sun_intensity = 1.0;
        uniform float sun_disc_intensity = 1.0;
        uniform float mie_scattering = 1.0;
        uniform float rayleigh_scattering = 1.0;
        uniform float mie_g = -0.999;
        uniform vec3 wavelength_nm = vec3(650.0, 570.0, 475.0);
        uniform vec3 clear_sky_color = vec3(0.12, 0.28, 0.55);
        uniform vec3 horizon_color = vec3(0.55, 0.62, 0.70);
        uniform vec3 ground_color = vec3(0.03, 0.04, 0.05);
        uniform vec3 night_horizon_color = vec3(0.015, 0.025, 0.05);
        uniform vec3 night_zenith_color = vec3(0.004, 0.008, 0.025);
        uniform float night_zenith_shift = 0.5;
        uniform float star_intensity = 0.0;

        float star_hash(vec3 p) {
            p = fract(p * 0.1031);
            p += dot(p, p.yzx + 33.33);
            return fract((p.x + p.y) * p.z);
        }

        void sky() {
            vec3 d = normalize(EYEDIR);
            float height = clamp(d.y, 0.0, 1.0);
            float mu = clamp(dot(d, normalize(sun_direction)), -1.0, 1.0);
            float mu2 = mu * mu;

            vec3 wavelengths = clamp(wavelength_nm, vec3(380.0), vec3(780.0));
            vec3 beta_r = pow(vec3(550.0) / wavelengths, vec3(4.0))
                * max(rayleigh_scattering, 0.0) * 0.035;
            // CE describes Rayleigh particles through roughly 8 km and aerosol Mie haze through roughly 1 km.
            // Keep the authored constants dimensionless, but account for their different column depths.
            float beta_m = max(mie_scattering, 0.0) * (0.018 / 8.0);
            float air_mass = 1.0 / max(height + 0.12, 0.08);
            vec3 extinction = exp(-(beta_r + vec3(beta_m)) * air_mass);

            float rayleigh_phase = 0.0596831 * (1.0 + mu2); // 3 / (16 pi)
            // Cry authors the opposite sign convention (typically near -1). Convert it to standard HG, where a
            // positive g gives the forward lobe around view-to-sun. The clamp avoids the singularity at abs(g) = 1.
            float g = clamp(-mie_g, -0.98, 0.98);
            // Cry authors negative anisotropy for a forward sun lobe; mu is view-ray · to-sun.
            // This sign convention is the standard HG phase with its incident-ray cosine reversed.
            float mie_phase = (1.0 - g * g) /
                (12.5663706 * pow(max(1.0 + g * g + 2.0 * g * mu, 0.001), 1.5));
            vec3 scatter_weight = beta_r * rayleigh_phase + vec3(beta_m * mie_phase);
            vec3 sunlight = max(sun_color, vec3(0.0)) * max(sun_intensity, 0.0);
            // Integrate scattering probability against optical depth. Normalizing by total beta
            // turns a small optical depth into a unit-strength colour mix and washes the sky out.
            vec3 in_scatter = (vec3(1.0) - extinction) * scatter_weight;

            // SkyHDR is illuminated by the authored solar spectrum. Apply Rayleigh's wavelength
            // response to the sunlight tint before the path integral; using the unfiltered light as
            // the dome base erases the blue spectral dominance and raises the whole sky toward white.
            vec3 spectral_sky = max(sun_color, vec3(0.0)) * beta_r / max(beta_r.g, 0.0001);
            vec3 ambient_gradient = mix(horizon_color, clear_sky_color, pow(height, 0.35));
            vec3 day_color = spectral_sky + ambient_gradient * 0.02 + in_scatter * sunlight;
            float sun_disc = smoothstep(0.99982, 0.99996, mu);
            day_color += sunlight * sun_disc_intensity * sun_disc * 5.0;
            // Sky brightening is terrain occlusion, and HDR dynamic power was applied to the light source above;
            // neither is a second whole-dome brightness multiplier.

            float authored_night = clamp(star_intensity / 3.0, 0.0, 1.0);
            float solar_night = 1.0 - smoothstep(-0.14, 0.035, sun_direction.y);
            float night = max(authored_night, solar_night);
            float zenith = pow(height, mix(2.0, 0.35, clamp(night_zenith_shift, 0.0, 1.0)));
            vec3 night_color = mix(night_horizon_color, night_zenith_color, zenith);
            if (height > 0.01 && night > 0.0) {
                float star = smoothstep(0.9987, 1.0, star_hash(floor(d * 900.0)));
                night_color += vec3(star * max(star_intensity, solar_night * 0.35));
            }

            vec3 sky_color = mix(day_color, night_color, night);
            if (d.y < 0.0)
                sky_color = mix(horizon_color, ground_color, smoothstep(0.0, 0.45, -d.y));
            COLOR = max(sky_color, vec3(0.0));
        }
        """;

    private static readonly Shader SharedShader = new() { Code = ShaderCode };

    public static ShaderMaterial Create() => new() { Shader = SharedShader };

    /// <summary>A broad, low-frequency sample for the water shader's explicit Fresnel tint.</summary>
    public static Color ReflectionColor(EnvironmentSample sample, Vector3 sunDirection, Color skyColor,
        Color horizonColor, Color sunColor)
    {
        // Sample the shader's same Rayleigh/Mie expression along four near-horizon directions. The old
        // estimate added the full atmospheric sun multiplier as a flat highlight, which is not sky radiance.
        // Keep this result linear and unclamped; Filmic handles the HDR sky consistently with the dome.
        var sunLength = MathF.Sqrt(sunDirection.X * sunDirection.X + sunDirection.Y * sunDirection.Y + sunDirection.Z * sunDirection.Z);
        var sx = sunLength > 1e-6f ? sunDirection.X / sunLength : 0f;
        var sy = sunLength > 1e-6f ? sunDirection.Y / sunLength : 1f;
        var sz = sunLength > 1e-6f ? sunDirection.Z / sunLength : 0f;
        const float horizonHeight = 0.05f;
        const float horizonRadius = 0.9987492f;
        var a = EvaluateSky(sample, sunColor, skyColor, horizonColor, sx, sy, sz,
            horizonRadius, horizonHeight, 0f);
        var b = EvaluateSky(sample, sunColor, skyColor, horizonColor, sx, sy, sz,
            -horizonRadius, horizonHeight, 0f);
        var c = EvaluateSky(sample, sunColor, skyColor, horizonColor, sx, sy, sz,
            0f, horizonHeight, horizonRadius);
        var d = EvaluateSky(sample, sunColor, skyColor, horizonColor, sx, sy, sz,
            0f, horizonHeight, -horizonRadius);
        var day = new Color((a.R + b.R + c.R + d.R) * 0.25f,
            (a.G + b.G + c.G + d.G) * 0.25f,
            (a.B + b.B + c.B + d.B) * 0.25f);
        var authoredNight = (float)Math.Clamp(sample.StarIntensity / 3.0, 0.0, 1.0);
        var solarNight = 1f - Mathf.SmoothStep(-0.14f, 0.035f, sunDirection.Y);
        var night = Math.Max(authoredNight, solarNight);
        var nightColor = new Color(sample.NightHorizonColor.X, sample.NightHorizonColor.Y,
            sample.NightHorizonColor.Z).Lerp(new Color(sample.NightZenithColor.X,
            sample.NightZenithColor.Y, sample.NightZenithColor.Z), 0.4f);
        return day.Lerp(nightColor, night);
    }

    private static Color EvaluateSky(EnvironmentSample sample, Color sunColor, Color skyColor,
        Color horizonColor, float sunX, float sunY, float sunZ, float viewX, float viewY, float viewZ)
    {
        var mu = Math.Clamp(viewX * sunX + viewY * sunY + viewZ * sunZ, -1f, 1f);
        var mu2 = mu * mu;
        var airMass = 1f / Math.Max(viewY + 0.12f, 0.08f);
        var betaM = Math.Max((float)sample.MieScattering, 0f) * (0.018f / 8f);
        var rayleighPhase = 0.0596831f * (1f + mu2);
        var g = Math.Clamp(-(float)sample.SkySunAnisotropy, -0.98f, 0.98f);
        var miePhase = (1f - g * g) /
            (12.5663706f * MathF.Pow(Math.Max(1f + g * g + 2f * g * mu, 0.001f), 1.5f));
        var baseColor = horizonColor.Lerp(skyColor, MathF.Pow(viewY, 0.35f));
        float Channel(float wavelength, float sunChannel, float baseChannel)
        {
            var betaR = MathF.Pow(550f / Math.Clamp(wavelength, 380f, 780f), 4f) *
                Math.Max((float)sample.RayleighScattering, 0f) * 0.035f;
            var extinction = MathF.Exp(-(betaR + betaM) * airMass);
            var scatterWeight = betaR * rayleighPhase + betaM * miePhase;
            var inScatter = (1f - extinction) * scatterWeight;
            var betaGreen = MathF.Pow(550f / Math.Clamp((float)sample.SkyWavelengthG, 380f, 780f), 4f) *
                Math.Max((float)sample.RayleighScattering, 0f) * 0.035f;
            var spectralBase = Math.Max(sunChannel, 0f) * betaR / Math.Max(betaGreen, 0.0001f);
            return spectralBase + baseChannel * 0.02f + inScatter * sunChannel * DynamicSunIntensity(sample);
        }

        return new Color(
            Channel((float)sample.SkyWavelengthR, sunColor.R, baseColor.R),
            Channel((float)sample.SkyWavelengthG, sunColor.G, baseColor.G),
            Channel((float)sample.SkyWavelengthB, sunColor.B, baseColor.B));
    }

    private static float DynamicSunIntensity(EnvironmentSample sample) =>
        // Cry applies this value as an HDR lighting stop factor: zero is neutral and positive
        // values brighten. The equivalent linear multiplier is 2^factor, not 1 + factor.
        (float)Math.Clamp(Math.Pow(2.0,
            Math.Clamp(sample.HdrDynamicPowerFactor, -8.0, 8.0)), 0.0, 64.0);

    public static void Apply(ShaderMaterial material, EnvironmentSample sample, Vector3 sunDirection,
        Color skyColor, Color horizonColor, Color groundColor, Color sunColor)
    {
        material.SetShaderParameter("sun_direction", sunDirection);
        material.SetShaderParameter("sun_color", new Vector3(sunColor.R, sunColor.G, sunColor.B));
        material.SetShaderParameter("sun_intensity", DynamicSunIntensity(sample));
        material.SetShaderParameter("sun_disc_intensity", (float)Math.Clamp(sample.SkySunIntensity, 0.0, 64.0));
        material.SetShaderParameter("mie_scattering", (float)Math.Clamp(sample.MieScattering, 0.0, 32.0));
        material.SetShaderParameter("rayleigh_scattering", (float)Math.Clamp(sample.RayleighScattering, 0.0, 32.0));
        material.SetShaderParameter("mie_g", (float)Math.Clamp(sample.SkySunAnisotropy, -0.98, 0.98));
        material.SetShaderParameter("wavelength_nm", new Vector3(
            (float)Math.Clamp(sample.SkyWavelengthR, 380.0, 780.0),
            (float)Math.Clamp(sample.SkyWavelengthG, 380.0, 780.0),
            (float)Math.Clamp(sample.SkyWavelengthB, 380.0, 780.0)));
        material.SetShaderParameter("clear_sky_color", new Vector3(skyColor.R, skyColor.G, skyColor.B));
        material.SetShaderParameter("horizon_color", new Vector3(horizonColor.R, horizonColor.G, horizonColor.B));
        material.SetShaderParameter("ground_color", new Vector3(groundColor.R, groundColor.G, groundColor.B));
        material.SetShaderParameter("night_horizon_color", new Vector3(sample.NightHorizonColor.X,
            sample.NightHorizonColor.Y, sample.NightHorizonColor.Z));
        material.SetShaderParameter("night_zenith_color", new Vector3(sample.NightZenithColor.X,
            sample.NightZenithColor.Y, sample.NightZenithColor.Z));
        material.SetShaderParameter("night_zenith_shift", (float)sample.NightZenithShift);
        material.SetShaderParameter("star_intensity", (float)Math.Max(0.0, sample.StarIntensity));
    }
}
