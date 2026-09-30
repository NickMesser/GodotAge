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
    /// <summary>
    /// The sky radiance function and its uniforms. The sky shader and the ocean shader both include it, so the water's
    /// Fresnel reflection samples exactly the radiance the visible dome has in the reflected direction.
    /// </summary>
    public const string SkyFunctionCode = """
        uniform vec3 sky_sun_direction = vec3(0.0, 1.0, 0.0);
        uniform vec3 sky_sun_color = vec3(1.0);
        uniform float sky_sun_intensity = 1.0;
        uniform float sky_sun_disc_intensity = 1.0;
        uniform float sky_mie_scattering = 1.0;
        uniform float sky_rayleigh_scattering = 1.0;
        uniform float sky_mie_g = -0.999;
        uniform vec3 sky_wavelength_nm = vec3(650.0, 570.0, 475.0);
        uniform vec3 sky_clear_color = vec3(0.12, 0.28, 0.55);
        uniform vec3 sky_horizon_color = vec3(0.55, 0.62, 0.70);
        uniform vec3 sky_ground_color = vec3(0.03, 0.04, 0.05);
        uniform vec3 sky_night_horizon_color = vec3(0.015, 0.025, 0.05);
        uniform vec3 sky_night_zenith_color = vec3(0.004, 0.008, 0.025);
        uniform float sky_night_zenith_shift = 0.5;
        uniform float sky_star_intensity = 0.0;

        float cry_star_hash(vec3 p) {
            p = fract(p * 0.1031);
            p += dot(p, p.yzx + 33.33);
            return fract((p.x + p.y) * p.z);
        }

        vec3 cry_sky_radiance(vec3 d, bool sun_disc_and_stars) {
            float height = clamp(d.y, 0.0, 1.0);
            float mu = clamp(dot(d, normalize(sky_sun_direction)), -1.0, 1.0);
            float mu2 = mu * mu;

            vec3 wavelengths = clamp(sky_wavelength_nm, vec3(380.0), vec3(780.0));
            vec3 beta_r = pow(vec3(550.0) / wavelengths, vec3(4.0))
                * max(sky_rayleigh_scattering, 0.0) * 0.035;
            // CE describes Rayleigh particles through roughly 8 km and aerosol Mie haze through roughly 1 km.
            // Keep the authored constants dimensionless, but account for their different column depths.
            float beta_m = max(sky_mie_scattering, 0.0) * (0.018 / 8.0);
            float air_mass = 1.0 / max(height + 0.12, 0.08);
            vec3 extinction = exp(-(beta_r + vec3(beta_m)) * air_mass);

            float rayleigh_phase = 0.0596831 * (1.0 + mu2); // 3 / (16 pi)
            // Cry authors the opposite sign convention (typically near -1). Convert it to standard HG, where a
            // positive g gives the forward lobe around view-to-sun. The clamp avoids the singularity at abs(g) = 1.
            float g = clamp(-sky_mie_g, -0.98, 0.98);
            // Cry authors negative anisotropy for a forward sun lobe; mu is view-ray · to-sun.
            // This sign convention is the standard HG phase with its incident-ray cosine reversed.
            float mie_phase = (1.0 - g * g) /
                (12.5663706 * pow(max(1.0 + g * g + 2.0 * g * mu, 0.001), 1.5));
            vec3 scatter_weight = beta_r * rayleigh_phase + vec3(beta_m * mie_phase);
            vec3 sunlight = max(sky_sun_color, vec3(0.0)) * max(sky_sun_intensity, 0.0);
            // Integrate scattering probability against optical depth. Normalizing by total beta
            // turns a small optical depth into a unit-strength colour mix and washes the sky out.
            vec3 in_scatter = (vec3(1.0) - extinction) * scatter_weight;

            // SkyHDR is illuminated by the authored solar spectrum. Apply Rayleigh's wavelength
            // response to the sunlight tint before the path integral; using the unfiltered light as
            // the dome base erases the blue spectral dominance and raises the whole sky toward white.
            vec3 spectral_sky = max(sky_sun_color, vec3(0.0)) * beta_r / max(beta_r.g, 0.0001);
            vec3 ambient_gradient = mix(sky_horizon_color, sky_clear_color, pow(height, 0.35));
            vec3 day_color = spectral_sky + ambient_gradient * 0.02 + in_scatter * sunlight;
            if (sun_disc_and_stars) {
                float sun_disc = smoothstep(0.99982, 0.99996, mu);
                day_color += sunlight * sky_sun_disc_intensity * sun_disc * 5.0;
            }
            // Sky brightening is terrain occlusion, and HDR dynamic power was applied to the light source above;
            // neither is a second whole-dome brightness multiplier.

            float authored_night = clamp(sky_star_intensity / 3.0, 0.0, 1.0);
            float solar_night = 1.0 - smoothstep(-0.14, 0.035, sky_sun_direction.y);
            float night = max(authored_night, solar_night);
            float zenith = pow(height, mix(2.0, 0.35, clamp(sky_night_zenith_shift, 0.0, 1.0)));
            vec3 night_color = mix(sky_night_horizon_color, sky_night_zenith_color, zenith);
            if (sun_disc_and_stars && height > 0.01 && night > 0.0) {
                float star = smoothstep(0.9987, 1.0, cry_star_hash(floor(d * 900.0)));
                night_color += vec3(star * max(sky_star_intensity, solar_night * 0.35));
            }

            vec3 sky_color = mix(day_color, night_color, night);
            if (d.y < 0.0)
                sky_color = mix(sky_horizon_color, sky_ground_color, smoothstep(0.0, 0.45, -d.y));
            return max(sky_color, vec3(0.0));
        }
        """;

    private const string ShaderCode = """
        shader_type sky;
        render_mode disable_fog;

        """ + SkyFunctionCode + """

        void sky() {
            COLOR = cry_sky_radiance(normalize(EYEDIR), true);
        }
        """;

    private static readonly Shader SharedShader = new() { Code = ShaderCode };

    public static ShaderMaterial Create() => new() { Shader = SharedShader };

    private static float DynamicSunIntensity(EnvironmentSample sample) =>
        // Cry applies this value as an HDR lighting stop factor: zero is neutral and positive
        // values brighten. The equivalent linear multiplier is 2^factor, not 1 + factor.
        (float)Math.Clamp(Math.Pow(2.0,
            Math.Clamp(sample.HdrDynamicPowerFactor, -8.0, 8.0)), 0.0, 64.0);

    /// <summary>The <see cref="SkyFunctionCode"/> uniform values for a sample, for the dome and every ocean material.</summary>
    public static (string Name, Variant Value)[] Parameters(EnvironmentSample sample, Vector3 sunDirection,
        Color skyColor, Color horizonColor, Color groundColor, Color sunColor) =>
    [
        ("sky_sun_direction", sunDirection),
        ("sky_sun_color", new Vector3(sunColor.R, sunColor.G, sunColor.B)),
        ("sky_sun_intensity", DynamicSunIntensity(sample)),
        ("sky_sun_disc_intensity", (float)Math.Clamp(sample.SkySunIntensity, 0.0, 64.0)),
        ("sky_mie_scattering", (float)Math.Clamp(sample.MieScattering, 0.0, 32.0)),
        ("sky_rayleigh_scattering", (float)Math.Clamp(sample.RayleighScattering, 0.0, 32.0)),
        ("sky_mie_g", (float)Math.Clamp(sample.SkySunAnisotropy, -0.98, 0.98)),
        ("sky_wavelength_nm", new Vector3(
            (float)Math.Clamp(sample.SkyWavelengthR, 380.0, 780.0),
            (float)Math.Clamp(sample.SkyWavelengthG, 380.0, 780.0),
            (float)Math.Clamp(sample.SkyWavelengthB, 380.0, 780.0))),
        ("sky_clear_color", new Vector3(skyColor.R, skyColor.G, skyColor.B)),
        ("sky_horizon_color", new Vector3(horizonColor.R, horizonColor.G, horizonColor.B)),
        ("sky_ground_color", new Vector3(groundColor.R, groundColor.G, groundColor.B)),
        ("sky_night_horizon_color", new Vector3(sample.NightHorizonColor.X,
            sample.NightHorizonColor.Y, sample.NightHorizonColor.Z)),
        ("sky_night_zenith_color", new Vector3(sample.NightZenithColor.X,
            sample.NightZenithColor.Y, sample.NightZenithColor.Z)),
        ("sky_night_zenith_shift", (float)sample.NightZenithShift),
        ("sky_star_intensity", (float)Math.Max(0.0, sample.StarIntensity)),
    ];

    public static void Apply(ShaderMaterial material, (string Name, Variant Value)[] parameters)
    {
        foreach (var (name, value) in parameters)
            material.SetShaderParameter(name, value);
    }
}
