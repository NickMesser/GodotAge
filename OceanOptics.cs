#nullable enable
using System.Numerics;

namespace AAEmu.GodotViewer;

/// <summary>
/// The display curve of the original client: <c>FilmicMapping</c> in game/shaders/hwscripts/cryfx/postprocess.cfx with the
/// zone's "Film curve shoulder/midtones/toe scale" and "Film curve whitepoint" time-of-day values. Its output is the
/// displayed (gamma-encoded) value; the client applies no further sRGB encoding (r_HDRRendering=3).
/// </summary>
public readonly record struct CeFilmCurve(float Shoulder, float Midtones, float Toe, float WhitePoint)
{
    /// <summary>
    /// Luminance-proportional toe offset (HDRParamLum.w). The shader's header documents HDRParamLum.w as
    /// r_HDRContrastLuminanceBlend, not r_HDRFilmicToe (0.1 in the cvar dump), and the original underwater capture
    /// (22,62,105) is reproduced without an offset (with 0.1 its red channel would display 13), so it is zero here.
    /// </summary>
    public const float FilmicToe = 0f;

    public static CeFilmCurve From(EnvironmentSample sample) => new(
        (float)sample.FilmCurveShoulderScale, (float)sample.FilmCurveMidtonesScale,
        (float)sample.FilmCurveToeScale, (float)Math.Max(sample.FilmCurveWhitepoint, 0.05));

    /// <summary>Displayed value (0..1) of a pre-curve colour, per channel, as the client's final HDR pass writes it.</summary>
    public Vector3 Map(Vector3 color)
    {
        var luminance = Math.Max(0f, Vector3.Dot(color, new Vector3(0.2126f, 0.7152f, 0.0722f)));
        var toe = Math.Clamp(Math.Min(luminance * FilmicToe, 0.004f), 0f, 1f);
        var curve = this;
        var white = curve.Curve(WhitePoint);
        float Channel(float x) => Math.Clamp(curve.Curve(Math.Max(x - toe, 0f)) / white, 0f, 1f);
        return new Vector3(Channel(color.X), Channel(color.Y), Channel(color.Z));
    }

    private float Curve(float x) =>
        x * (Shoulder * 6.2f * x + Midtones * 0.5f) / (x * (Shoulder * 6.2f * x + 1.7f) + Toe * 0.06f);
}

/// <summary>
/// Godot's own display transform for the world environment (Filmic tonemap with the environment white point, sRGB
/// encoding, then the saturation adjustment). <see cref="ToLinear"/> inverts it, which turns a colour the original
/// client displays into the scene-linear value Godot needs to display the same colour.
/// </summary>
public readonly record struct GodotDisplay(float White, float Saturation)
{
    public Vector3 ToDisplay(Vector3 linear)
    {
        var white = Filmic(Math.Max(White, 1f));
        var s = new Vector3(Encode(Filmic(linear.X) / white), Encode(Filmic(linear.Y) / white), Encode(Filmic(linear.Z) / white));
        var mean = (s.X + s.Y + s.Z) / 3f;
        return Vector3.Clamp(new Vector3(mean) + (s - new Vector3(mean)) * Saturation, Vector3.Zero, Vector3.One);
    }

    public Vector3 ToLinear(Vector3 display)
    {
        display = Vector3.Clamp(display, Vector3.Zero, Vector3.One);
        var mean = (display.X + display.Y + display.Z) / 3f;
        var s = Saturation > 1e-3f ? new Vector3(mean) + (display - new Vector3(mean)) / Saturation : display;
        s = Vector3.Clamp(s, Vector3.Zero, new Vector3(0.999f));
        var white = Filmic(Math.Max(White, 1f));
        return new Vector3(Invert(Decode(s.X) * white), Invert(Decode(s.Y) * white), Invert(Decode(s.Z) * white));
    }

    // Godot 4 tonemap.glsl tonemap_filmic: Hable curve with exposure bias 2 baked into A and B.
    private static float Filmic(float x)
    {
        x = Math.Max(x, 0f);
        const float a = 0.22f * 4f, b = 0.30f * 2f, c = 0.10f, d = 0.20f, e = 0.01f, f = 0.30f;
        return (x * (a * x + c * b) + d * e) / (x * (a * x + b) + d * f) - e / f;
    }

    private static float Invert(float target)
    {
        if (target <= 0f)
            return 0f;
        float lo = 0f, hi = 4096f;
        for (var i = 0; i < 60; i++)
        {
            var mid = (lo + hi) * 0.5f;
            if (Filmic(mid) < target) lo = mid; else hi = mid;
        }
        return (lo + hi) * 0.5f;
    }

    private static float Encode(float x)
    {
        x = Math.Clamp(x, 0f, 1f);
        return x <= 0.0031308f ? x * 12.92f : 1.055f * MathF.Pow(x, 1f / 2.4f) - 0.055f;
    }

    private static float Decode(float x) => x <= 0.04045f ? x / 12.92f : MathF.Pow((x + 0.055f) / 1.055f, 2.4f);
}

/// <summary>
/// CryEngine 3 ocean optics as the original client's water shaders compute them (game/shaders/hwscripts/cryfx,
/// watervolume.cfx OceanIntoPS / OceanOutofPS and water.cfx). The visible water colour is light in-scattered by the water
/// column, not a lit surface albedo:
/// <c>col = fogColor * exp2(-d * depthBelowSurface) * (exp2(t * V) - 1) / t * scatter</c>, with <c>d = log2(e) * density</c>
/// and <c>t = d * (view.z - 1)</c>. For a deep column (<c>V</c> to infinity) that is
/// <c>fogColor * scatter * exp2(-d * depth) / (d * (1 - view.z))</c>.
/// fogColor is the time-of-day "Ocean fog color" x "Ocean fog color multiplier" lit by the time-of-day sun
/// ("Sun color" x "Sun color multiplier"), all in the client's (non-sRGB) working values.
/// </summary>
public static class OceanOptics
{
    public const float Log2E = 1.44269502f;

    /// <summary>
    /// Deep-column in-scattered radiance (client working space) seen along a horizontal ray from <paramref name="depth"/>
    /// metres below the surface. Multiply by <c>1 / (1 - view.z)</c> for other view directions.
    /// </summary>
    public static Vector3 InScatter(Vector3 litFogColor, double density, double scatter, float depth = 0f)
    {
        var d = Log2E * (float)Math.Max(density, 1e-4);
        return litFogColor * (float)Math.Max(scatter, 0.0) * MathF.Pow(2f, -d * Math.Max(depth, 0f)) / d;
    }

    /// <summary>Scene-linear Godot colour that the world environment displays as the client would display <paramref name="ceColor"/>.</summary>
    public static Vector3 ToGodotLinear(Vector3 ceColor, CeFilmCurve film, GodotDisplay display) =>
        display.ToLinear(film.Map(ceColor));
}
