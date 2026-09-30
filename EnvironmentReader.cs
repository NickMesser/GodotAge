#nullable enable
using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace AAEmu.GodotViewer;

/// <summary>Reads CryEngine-style ArcheAge environment XML and samples its normalized-day splines.</summary>
public sealed class EnvironmentReader
{
    private readonly Dictionary<string, Curve> _curves;
    private readonly ZoneSettings _zone;
    private readonly WorldSettings _world;
    private readonly IReadOnlyList<CloudLayer> _clouds;
    private readonly bool _todColorsAreSrgb;

    private EnvironmentReader(Dictionary<string, Curve> curves, ZoneSettings zone, WorldSettings world,
        IReadOnlyList<CloudLayer> clouds, bool todColorsAreSrgb)
    { _curves = curves; _zone = zone; _world = world; _clouds = clouds; _todColorsAreSrgb = todColorsAreSrgb; }

    /// <summary>Loads game/worlds/&lt;world&gt; from a folder on disk, using the zone directory key (zones.zone_key).</summary>
    public static EnvironmentReader Load(string worldDirectory, int zoneKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(worldDirectory);
        return Load(p => File.Exists(Path.Combine(worldDirectory, p)) ? File.ReadAllBytes(Path.Combine(worldDirectory, p)) : null, zoneKey)
            ?? throw new FileNotFoundException($"environment files for zone {zoneKey} not found under {worldDirectory}");
    }

    /// <summary>
    /// Loads a world's environment through <paramref name="read"/>, which returns a file's bytes by path relative to the
    /// world folder ("env.xml", "zone/129/time_of_day.xml") or null. Returns null when the zone has no environment files.
    /// </summary>
    public static EnvironmentReader? Load(Func<string, byte[]?> read, int zoneKey)
    {
        XDocument? Xml(string path)
        {
            var bytes = read(path);
            return bytes == null ? null : XDocument.Parse(System.Text.Encoding.UTF8.GetString(bytes).TrimStart((char)0xFEFF));
        }
        var zoneDirectory = $"zone/{zoneKey.ToString(CultureInfo.InvariantCulture)}";
        var worldEnv = Xml("env.xml") ?? new XDocument();
        var zoneEnv = Xml($"{zoneDirectory}/env.xml") ?? new XDocument();
        var time = Xml($"{zoneDirectory}/time_of_day.xml");
        if (time == null)
            return null;
        var clouds = Xml($"{zoneDirectory}/clouds.xml") ?? new XDocument();
        var lighting = Xml("lighting.xml") ?? new XDocument();

        // Primary layer is the normal (non-relic, non-weather) profile. Curve values are linear-interpolated.
        var tod = time.Root?.Elements("TimeOfDay")
            .FirstOrDefault(x => AttrInt(x, "relic_layer") == 0 && AttrInt(x, "weather_layer") == 0)
            ?? time.Root?.Elements("TimeOfDay").FirstOrDefault()
            ?? throw new InvalidDataException("No TimeOfDay profile found.");
        var curves = new Dictionary<string, Curve>(StringComparer.OrdinalIgnoreCase);
        foreach (var variable in tod.Elements("Variable"))
        {
            var name = (string?)variable.Attribute("Name");
            if (string.IsNullOrWhiteSpace(name)) continue;
            var spline = variable.Element("Spline")?.Attribute("Keys")?.Value;
            var curve = spline is null ? null : Curve.Parse(spline);
            if (curve is null || curve.Keys.Count == 0)
            {
                var fallback = ParseValue(variable.Attribute("Value")?.Value ?? variable.Attribute("Color")?.Value);
                if (fallback.Length > 0) curve = new Curve(new[] { new CurveKey(0, fallback), new CurveKey(1, fallback) });
            }
            if (curve is not null) curves[name] = curve;
        }
        return new EnvironmentReader(curves, ZoneSettings.Parse(zoneEnv), WorldSettings.Parse(worldEnv, lighting),
            clouds.Root?.Elements("Cloud").Select(CloudLayer.Parse).ToArray() ?? Array.Empty<CloudLayer>(),
            AttrInt(tod, "sRGB") == 1);
    }

    /// <summary>Samples an hour in [0,24], wrapping other values into a 24-hour day.</summary>
    public EnvironmentSample Sample(double hour) => Sample(hour, false);

    /// <summary>Samples the environment, optionally decoding TimeOfDay colors marked as sRGB for render parity.</summary>
    public EnvironmentSample Sample(double hour, bool renderParity)
    {
        var day = ((hour % 24) + 24) % 24 / 24.0;
        Vector3 C(string key, Vector3 fallback)
        {
            if (!_curves.TryGetValue(key, out var curve))
                return fallback;
            var color = curve.Vector(day, fallback);
            // TimeOfDay sRGB applies to authored colour splines, not their linear intensity multipliers.
            return renderParity && _todColorsAreSrgb ? SrgbToLinear(color) : color;
        }
        double S(string key, double fallback) => _curves.TryGetValue(key, out var c) ? c.Scalar(day, fallback) : fallback;
        var sunColor = C("Sun color", Vector3.One);
        var skyColor = C("Sky color", Vector3.Zero) * (float)(S("Sky color multiplier", 1) * _zone.SkyColorMultiplier);
        var fog = C("Fog color", Vector3.Zero) * (float)S("Fog color multiplier", 1);
        var ambient = C("Ambient ground color", Vector3.Zero) * (float)S("Ambient ground color multiplier", 1);
        var nightHorizon = C("Night sky: Horizon color", Vector3.Zero) * (float)S("Night sky: Horizon color multiplier", 1);
        var nightZenith = C("Night sky: Zenith color", Vector3.Zero) * (float)S("Night sky: Zenith color multiplier", 1);
        var moonColor = C("Night sky: Moon color", Vector3.Zero) * (float)S("Night sky: Moon color multiplier", 1);
        var oceanScatteringSource = _curves.TryGetValue("Ocean fog color", out var oceanCurve)
            ? oceanCurve.Vector(day, Vector3.Zero)
            : Vector3.Zero;
        // The client's water shaders use the stored TOD values as they are (its renderer does not decode sRGB): the ocean
        // fog colour lit by the TOD sun is the in-scatter colour of watervolume.cfx (see OceanOptics).
        var rawSun = _curves.TryGetValue("Sun color", out var sunCurve) ? sunCurve.Vector(day, Vector3.One) : Vector3.One;
        var oceanInScatter = oceanScatteringSource * (float)S("Ocean fog color multiplier", 1) *
                             rawSun * (float)(S("Sun color multiplier", 1) * _zone.SunColorMultiplier);
        var fogDensity = S("Volumetric fog: Global density", S("Fog layer density (bottom)", 0)) * _zone.FogGlobalDensityMultiplier;
        var fogRampStart = S("Volumetric fog: Ramp start", 0);
        var fogRampEnd = S("Volumetric fog: Ramp end", 1000);
        var fogRampInfluence = S("Volumetric fog: Ramp influence", Math.Clamp(fogDensity, 0, 1));
        return new EnvironmentSample(
            hour, day, _world.SunVector, _world.SunRotationDegrees, _world.SunHeightDegrees,
            sunColor, S("Sun color multiplier", 1) * _zone.SunColorMultiplier,
            S("Sun specular multiplier", 1), skyColor, _zone.SkyboxMaterial,
            S("Skybox multiplier", 1),
            fog, fogDensity,
            S("Fog height (bottom)", 0), S("Fog height (top)", 0),
            ambient, _zone.OceanFogColor, S("Ocean fog density", _zone.OceanFogDensity),
            C("Sky light: Sun intensity", sunColor), S("Sky light: Sun intensity multiplier", 1), S("Sky light: Mie scattering", 0),
            S("Sky light: Rayleigh scattering", 0), _clouds.Select(c => new CloudLayerInfo(c.Id,c.BearingDegrees,c.Distance,c.Height,c.Size,c.Speed,c.Material)).ToArray(), _zone.FadeInOutSeconds,
            _world.MoonTexture, _world.MoonLatitude, _world.MoonLongitude, _world.MoonSize,
            C("Fog color (top)", Vector3.Zero) * (float)S("Fog color (top) multiplier", 1), S("Fog layer density (bottom)", 0),
            S("Fog layer density (top)", 0), _world.Ocean, _zone.SkyboxAngle,
            S("HDR dynamic power factor", 1), S("Sky brightening (terrain occlusion)", 1), _world.BloomMultiplier,
            S("SSDO Amount", 1), S("SSDO AmbientAmount", 1), S("Film curve whitepoint", 1),
            S("Color: saturation", 1), S("Color: contrast", 1), S("Color: brightness", 1),
            _world.BloomColor, S("Saturation", 1), S("Shadow Intensity", 1),
            nightHorizon, nightZenith, S("Night sky: Zenith shift", 0.5),
            S("Night sky: Star intensity", 0), moonColor,
            S("Sky light: Sun anisotropy factor", S("Sky light: Mie anisotropy", -0.999)),
            S("Sky light: Wavelength (R)", 650), S("Sky light: Wavelength (G)", 570),
            S("Sky light: Wavelength (B)", 475))
        {
            FogRampStart = fogRampStart,
            FogRampEnd = fogRampEnd,
            FogRampInfluence = fogRampInfluence,
            OceanFogDensityUnderWater = S("Ocean fog density under water", 0.05),
            OceanFogDensityIntoWater = S("Ocean fog density into water", 0.04),
            OceanInScatterColor = oceanInScatter,
            OceanScatterUnderWater = S("Ocean fog under water Scatter", 1),
            OceanScatterIntoWater = S("Ocean fog into water Scatter", 1),
            FilmCurveShoulderScale = S("Film curve shoulder scale", 1),
            FilmCurveMidtonesScale = S("Film curve midtones scale", 1),
            FilmCurveToeScale = S("Film curve toe scale", 1),
        };
    }

    private static Vector3 SrgbToLinear(Vector3 color) => new(ToLinear(color.X), ToLinear(color.Y), ToLinear(color.Z));
    private static float ToLinear(float value)
    {
        value = Math.Max(0f, value);
        return value <= 0.04045f ? value / 12.92f : MathF.Pow((value + 0.055f) / 1.055f, 2.4f);
    }

    private static int AttrInt(XElement e, string key) => int.TryParse((string?)e.Attribute(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : -1;
    private static Vector3 ParseVector(string? value, Vector3 fallback)
    {
        var values = ParseValue(value); return values.Length >= 3 ? new Vector3((float)values[0], (float)values[1], (float)values[2]) : fallback;
    }
    private static double[] ParseValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return Array.Empty<double>();
        var trimmed=value.Trim();
        var delimiter = trimmed.StartsWith('(') && trimmed.EndsWith(')') ? ':' : ',';
        var s = trimmed.Trim('(', ')');
        return s.Split(delimiter, StringSplitOptions.TrimEntries).Select(x => double.TryParse(x, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? (double?)n : null)
            .Where(x => x.HasValue).Select(x => x!.Value).ToArray();
    }
    private static double GetDouble(XElement? e, string name, double fallback = 0) =>
        double.TryParse((string?)e?.Attribute(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;
    private static Vector3 GetRgb(XElement? e, string name, Vector3 fallback)
    {
        var v = ParseVector((string?)e?.Attribute(name), fallback); return v;
    }
    private static string GetAttr(XElement? e, string name, string fallback = "") => (string?)e?.Attribute(name) ?? fallback;

    private sealed record ZoneSettings(string SkyboxMaterial, double SkyboxAngle, double SkyColorMultiplier, double SunColorMultiplier, double FogGlobalDensityMultiplier, double FadeInOutSeconds,
        Vector3 OceanFogColor, double OceanFogDensity)
    {
        public static ZoneSettings Parse(XDocument x)
        {
            var z = x.Root?.Element("Zone"); var ldr = z?.Element("LDR"); var ocean = z?.Element("Ocean");
            var rgb = ParseValue((string?)ocean?.Attribute("FogColor"));
            var color = rgb.Length >= 3 ? new Vector3((float)(rgb[0] / 255), (float)(rgb[1] / 255), (float)(rgb[2] / 255)) : new Vector3(32f/255,112f/255,208f/255);
            color = SrgbToLinear(color);
            return new ZoneSettings(GetAttr(z,"SkyBoxMaterial"), GetDouble(z,"Angle",0), GetDouble(ldr,"SkyColorMultiplier",1), GetDouble(ldr,"SunColorMultiplier",1), GetDouble(ldr,"FogGlobalDensityMultiplier",1),
                GetDouble(z,"FadeInOutTime",0), color, GetDouble(ocean,"FogDensity",0));
        }
    }
    private sealed record WorldSettings(Vector3 SunVector, double SunRotationDegrees, double SunHeightDegrees,
        string MoonTexture, double MoonLatitude, double MoonLongitude, double MoonSize, OceanSettings Ocean,
        double BloomMultiplier, Vector3 BloomColor)
    {
        public static WorldSettings Parse(XDocument env, XDocument lighting)
        {
            var sun = lighting.Root?.Element("Lighting"); var moon = env.Root?.Element("Moon");
            var ocean = env.Root?.Element("Ocean"); var animation = env.Root?.Element("OceanAnimation");
            var hdr = env.Root?.Element("HDRSetup");
            return new WorldSettings(ParseVector(GetAttr(sun,"SunVector"),Vector3.Zero), GetDouble(sun,"SunRotation"), GetDouble(sun,"SunHeight"),
                GetAttr(moon,"Texture"),GetDouble(moon,"Latitude"),GetDouble(moon,"Longitude"),GetDouble(moon,"Size"),
                new OceanSettings(GetAttr(ocean,"Material"),GetDouble(ocean,"CausticDepth"),GetDouble(ocean,"CausticIntensity"),GetDouble(ocean,"CausticsTilling"),
                    GetDouble(animation,"WindDirection"),GetDouble(animation,"WindSpeed"),GetDouble(animation,"WavesSpeed"),GetDouble(animation,"WavesAmount"),GetDouble(animation,"WavesSize")),
                GetDouble(hdr,"BloomMul",1), GetRgb(hdr,"BloomColor",new Vector3(255,255,255)) / 255f);
        }
    }
    private sealed class Curve
    {
        public IReadOnlyList<CurveKey> Keys { get; }
        internal Curve(IEnumerable<CurveKey> keys) => Keys = keys.OrderBy(k => k.Time).ToArray();
        private static readonly Regex KeyPattern = new(@"(?<t>[-+0-9.eE]+):(?<v>\([^)]*\)|[^,:]+):(?<f>\d+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        public static Curve? Parse(string text)
        {
            var keys = new List<CurveKey>();
            foreach (Match m in KeyPattern.Matches(text))
            {
                if (!double.TryParse(m.Groups["t"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var t)) continue;
                var value = ParseValue(m.Groups["v"].Value); if (value.Length > 0) keys.Add(new CurveKey(t, value));
            }
            return keys.Count == 0 ? null : new Curve(keys);
        }
        public double[] At(double t)
        {
            if (Keys.Count == 1 || t <= Keys[0].Time) return Keys[0].Value;
            if (t >= Keys[^1].Time) return Keys[^1].Value;
            var hi = 1; while (hi < Keys.Count && Keys[hi].Time < t) hi++;
            var a = Keys[hi - 1]; var b = Keys[hi]; var f = (t - a.Time) / Math.Max(1e-12, b.Time - a.Time);
            var count = Math.Min(a.Value.Length, b.Value.Length); var v = new double[count];
            for (var i=0;i<count;i++) v[i] = a.Value[i] + (b.Value[i]-a.Value[i])*f;
            return v;
        }
        public double Scalar(double t,double fallback) { var v=At(t); return v.Length>0?v[0]:fallback; }
        public Vector3 Vector(double t,Vector3 fallback) { var v=At(t); return v.Length>=3?new Vector3((float)v[0],(float)v[1],(float)v[2]):fallback; }
    }
    private sealed record CurveKey(double Time, double[] Value);
    private sealed record CloudLayer(int Id, double BearingDegrees, double Distance, double Height, double Size, double Speed, string Material)
    {
        public static CloudLayer Parse(XElement e) => new(AttrInt(e,"id"), GetDouble(e,"bearing"), GetDouble(e,"distance"), GetDouble(e,"height"), GetDouble(e,"size"), GetDouble(e,"speed"), GetAttr(e,"material"));
    }
}

/// <summary>Interpolated environment values. In parity samples, TOD colors marked sRGB are converted to linear.</summary>
public sealed record CloudLayerInfo(int Id, double BearingDegrees, double Distance, double Height, double Size, double Speed, string Material);

public sealed record EnvironmentSample(double Hour, double NormalizedDay, Vector3 SunVector, double SunRotationDegrees,
    double SunHeightDegrees, Vector3 SunColor, double SunIntensity, double SunSpecularMultiplier,
    Vector3 SkyColor, string SkyboxMaterial,
    double SkyboxMultiplier,
    Vector3 FogColor, double FogDensity, double FogBottomHeight, double FogTopHeight, Vector3 AmbientColor,
    Vector3 OceanFogColor, double OceanFogDensity, Vector3 SkySunColor, double SkySunIntensity,
    double MieScattering, double RayleighScattering,
    IReadOnlyList<CloudLayerInfo> CloudLayers, double SkyFadeSeconds, string MoonTexture, double MoonLatitude, double MoonLongitude, double MoonSize,
    Vector3 FogTopColor, double FogBottomLayerDensity, double FogTopLayerDensity, OceanSettings Ocean, double SkyboxAngle,
    double HdrDynamicPowerFactor, double SkyBrightening, double BloomMultiplier,
    double SsdoAmount, double SsdoAmbientAmount, double FilmCurveWhitepoint,
    double ColorSaturation, double ColorContrast, double ColorBrightness,
    Vector3 BloomColor, double HdrCurveSaturation, double ShadowIntensity,
    Vector3 NightHorizonColor, Vector3 NightZenithColor, double NightZenithShift,
    double StarIntensity, Vector3 MoonColor, double SkySunAnisotropy,
    double SkyWavelengthR, double SkyWavelengthG, double SkyWavelengthB)
{
    public double FogRampStart { get; init; }
    public double FogRampEnd { get; init; }
    public double FogRampInfluence { get; init; }
    public double OceanFogDensityUnderWater { get; init; }
    public double OceanFogDensityIntoWater { get; init; }
    /// <summary>
    /// The client's ocean in-scatter colour: stored TOD "Ocean fog color" x multiplier, lit by the stored TOD "Sun color" x
    /// "Sun color multiplier" (client working values, no sRGB decode).
    /// </summary>
    public Vector3 OceanInScatterColor { get; init; }
    public double OceanScatterUnderWater { get; init; } = 1;
    public double OceanScatterIntoWater { get; init; } = 1;
    /// <summary>TOD "Film curve shoulder/midtones/toe scale" of the client's filmic display curve (1 = neutral).</summary>
    public double FilmCurveShoulderScale { get; init; } = 1;
    public double FilmCurveMidtonesScale { get; init; } = 1;
    public double FilmCurveToeScale { get; init; } = 1;
}

public sealed record OceanSettings(string Material, double CausticDepth, double CausticIntensity, double CausticsTiling,
    double WindDirection, double WindSpeed, double WavesSpeed, double WavesAmount, double WavesSize);
