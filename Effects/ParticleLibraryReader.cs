using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace AAEmu.GodotViewer.Effects;

public sealed class ParticleLibrary
{
    public string Name { get; init; } = "";
    public string SourcePath { get; init; } = "";
    public List<ParticleEffectDefinition> Roots { get; } = [];
    public Dictionary<string, ParticleEffectDefinition> Effects { get; } = new(StringComparer.OrdinalIgnoreCase);
    public ParticleEffectDefinition Find(string name) => name != null && Effects.TryGetValue(name, out var e) ? e : null;
}

public sealed class ParticleEffectDefinition
{
    public string Name { get; init; } = "Particle";
    public bool Enabled { get; init; } = true;
    public ParticleParameters Parameters { get; init; } = new();
    // Some CE3 exporters put child timing/attachment overrides beside Params.
    public ParticleParameters SpawnParameters { get; init; } = new();
    public List<ParticleEffectDefinition> Children { get; } = [];
    public Dictionary<string, string> RawAttributes { get; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ParticleValue
{
    public float Base { get; init; }
    public float Random { get; init; }
    public bool HasBase { get; init; } = true;
    /// <summary>Curve driven by the emitter's normalized strength (the third Cry variable-parameter field).</summary>
    public List<ParticleCurveKey> EmitterCurve { get; init; } = [];
    /// <summary>Curve over normalized particle age (the fourth Cry variable-parameter field).</summary>
    public List<ParticleCurveKey> Curve { get; init; } = [];
    // Cry scalar Random is a one-sided relative reduction: [Base * (1 - Random), Base].
    private float Reduced => Base * (1f - Math.Clamp(Math.Abs(Random), 0f, 1f));
    public float Minimum => Math.Min(Base, Reduced);
    public float Maximum => Math.Max(Base, Reduced);
}

public readonly record struct ParticleCurveKey(float Time, float Value);

public sealed class ParticleParameters
{
    public Dictionary<string, string> Raw { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, ParticleValue> Values { get; } = new(StringComparer.OrdinalIgnoreCase);
    public ParticleValue LifeTime => Value(["ParticleLifeTime", "LifeTime"], 1f);
    public ParticleValue Count => Value(["Count", "ParticleCount"], 1f);
    public ParticleValue Speed => Value("Speed", "EmitSpeed");
    public ParticleValue Size => Value(["Size", "ParticleSize"], 1f);
    public ParticleValue SizeX => Value(["SizeX"], 1f);
    public ParticleValue SizeY => Value(["SizeY"], 1f);
    public ParticleValue SizeZ => Value(["SizeZ"], 1f);
    public ParticleValue EmitterScale => Value(["EmitterScale", "Scale"], 1f);
    public ParticleValue Stretch => Value("Stretch");
    public ParticleValue SpawnDelay => Value("SpawnDelay", "EmitterDelay");
    public ParticleValue PulsePeriod => Value("PulsePeriod", "PulseTime");
    public ParticleValue EmitterLifeTime => Value("EmitterLifeTime", "EmitterLifetime");
    public ParticleValue EmitAngle => Value("EmitAngle", "EmissionAngle", "SpreadAngle");
    public ParticleValue GravityScale => Value("GravityScale", "Gravity");
    public ParticleValue AirResistance => Value("AirResistance", "Drag");
    public ParticleValue Rotation => Value("Rotation", "InitAngle");
    public ParticleValue RotationRate => Value("RotationRate", "RotationSpeed");
    public ParticleValue Alpha => Value(["Alpha", "Opacity"], 1f);
    public string Texture => Text("Texture", "TextureName", "Diffuse");
    public string Material => Text("Material");
    public string Geometry => Text("Geometry");
    public string Blend => Text("BlendType", "Blend", "BlendMode");
    public string Facing => Text("Facing");
    public string EmitterShape => Text("EmitterShape", "EmitShape", "SpawnShape", "EmitterType");
    public string EmitterSize => Text("EmitterSize", "EmitSize", "SpawnSize");
    public bool Continuous => Boolean(false, "Continuous", "bContinuous");
    public bool FacingVelocity => Boolean(false, "OrientToVelocity", "FacingVelocity");
    public bool LocalSpace => Boolean(false, "MoveRelEmitter", "LocalSpace", "BindToEmitter");
    public bool SoftParticle => Boolean(false, "SoftParticle");
    public float TailLength => Number(0f, "TailLength");
    public int TailSteps => Integer(0, "TailSteps");
    public float Bounciness => Number(0f, "Bounciness");
    public float DynamicFriction => Number(0f, "DynamicFriction");
    public float DiffuseLighting => Number(0f, "DiffuseLighting");
    public float DiffuseBacklighting => Number(0f, "DiffuseBacklighting");
    public float EmissiveLighting => Number(0f, "EmissiveLighting");
    public float EmissiveHdrDynamic => Number(0f, "EmissiveHDRDynamic");
    public float MinPixels => Number(0f, "MinPixels");
    public bool ScaleByDistance => Boolean(false, "ScaleByDistance");
    public int AtlasColumns => Integer(1, "TextureTiling", "AnimTilesX", "AtlasColumns");
    public int AtlasRows => Integer(1, "AnimTilesY", "AtlasRows");
    public float AtlasFrameRate => Number(0f, "FrameRate", "AnimFramerate", "AtlasFrameRate");
    public string Color => Text("Color", "DiffuseColor");
    public string AlphaCurve => Text("AlphaCurve", "Alpha", "AlphaOverLife");

    public ParticleValue Value(params string[] names) => Value(names, 0f);
    public ParticleValue Value(string a, string b) => Value([a, b], 0f);
    public ParticleValue Value(string a, string b, string c) => Value([a, b, c], 0f);
    private ParticleValue Value(string[] names, float fallback)
    {
        foreach (var n in names)
            if (Values.TryGetValue(n, out var v))
                return v.HasBase || fallback == 0 ? v : new ParticleValue { Base = fallback, Random = v.Random, HasBase = false, EmitterCurve = v.EmitterCurve, Curve = v.Curve };
        return new ParticleValue { Base = fallback };
    }
    public string Text(params string[] names)
    {
        foreach (var n in names) if (Raw.TryGetValue(n, out var v) && !string.IsNullOrWhiteSpace(v)) return v.Trim();
        return "";
    }
    public float Number(float fallback, params string[] names) => ParticleLibraryReader.TryFloat(Text(names), out var v) ? v : fallback;
    public int Integer(int fallback, params string[] names) => ParticleLibraryReader.TryFloat(Text(names), out var v) ? Math.Max(0, (int)MathF.Round(v)) : fallback;
    public bool Boolean(bool fallback, params string[] names)
    {
        var s = Text(names); if (s.Length == 0) return fallback;
        return s.Equals("true", StringComparison.OrdinalIgnoreCase) || s.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
               (ParticleLibraryReader.TryFloat(s, out var v) && v != 0);
    }
}

public static partial class ParticleLibraryReader
{
    [GeneratedRegex(@"[-+]?(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][-+]?\d+)?", RegexOptions.CultureInvariant)]
    private static partial Regex NumberRegex();

    public static ParticleLibrary Load(string pakPath)
    {
        var path = NormalizeParticlePath(pakPath);
        var xml = PakFiles.ReadText(path) ?? throw new FileNotFoundException($"Particle library not found in game_pak: {path}", path);
        return Parse(xml, path);
    }
    public static IReadOnlyList<ParticleLibrary> LoadLibraries(IEnumerable<string> paths) => paths.Select(Load).ToArray();
    public static bool TryLoad(string path, out ParticleLibrary library)
    {
        try { library = Load(path); return true; }
        catch (Exception e) { Godot.GD.PrintErr($"Could not read particle library '{path}': {e.Message}"); library = null; return false; }
    }
    public static ParticleLibrary Parse(string xml, string sourcePath = "")
    {
        // ReadText decodes the BOM correctly; TrimStart also tolerates callers which pass a BOM character.
        var doc = XDocument.Parse(xml.TrimStart('\uFEFF', 'ï', '»', '¿'));
        var result = new ParticleLibrary { SourcePath = sourcePath, Name = Attr(doc.Root, "Name") ?? Path.GetFileNameWithoutExtension(sourcePath) };
        if (doc.Root == null) return result;
        foreach (var x in doc.Root.DescendantsAndSelf().Where(IsEffect).Where(x => !x.Ancestors().Any(IsEffect)))
        {
            var effect = ParseEffect(x); result.Roots.Add(effect); Index(effect, "", result.Effects);
        }
        foreach (var pair in result.Effects.ToArray()) result.Effects.TryAdd(result.Name + "." + pair.Key, pair.Value);
        return result;
    }
    private static ParticleEffectDefinition ParseEffect(XElement x)
    {
        var p = ParseParameters(x.Elements().FirstOrDefault(e => IsName(e, "Params", "Parameters")));
        var sp = ParseParameters(x.Elements().FirstOrDefault(e => IsName(e, "SpawnParams", "SpawnParameters")));
        var e = new ParticleEffectDefinition { Name = Attr(x, "Name", "name", "Id") ?? "Particle", Enabled = ParseBool(Attr(x, "Enabled", "Active"), true) && p.Boolean(true, "Enabled", "Active"), Parameters = p, SpawnParameters = sp };
        foreach (var a in x.Attributes()) e.RawAttributes[a.Name.LocalName] = a.Value;
        foreach (var c in x.Elements().SelectMany(n => IsEffect(n) ? [n] : IsName(n, "Childs", "Children") ? n.Elements().Where(IsEffect) : [])) e.Children.Add(ParseEffect(c));
        return e;
    }
    private static ParticleParameters ParseParameters(XElement x)
    {
        var p = new ParticleParameters(); if (x == null) return p;
        foreach (var a in x.Attributes()) { p.Raw[a.Name.LocalName] = a.Value; if (TryParseValue(a.Value, out var v)) p.Values[a.Name.LocalName] = v; }
        foreach (var n in x.Elements()) { var raw = Attr(n, "Value", "value") ?? n.Value; p.Raw[n.Name.LocalName] = raw; if (TryParseValue(raw, out var v)) p.Values[n.Name.LocalName] = v; }
        return p;
    }
    private static bool TryParseValue(string text, out ParticleValue value)
    {
        value = null; if (string.IsNullOrWhiteSpace(text)) return false;
        // Cry variable parameters are four top-level comma-separated fields:
        // base, relative-random, emitter-strength curve, particle-age curve. Curve keys may themselves
        // contain commas, so a normal Split corrupts both the field selection and tangent metadata.
        var fields = SplitTopLevel(text); float b = 0, r = 0;
        var has = fields.Count > 0 && TryFloatExact(fields[0], out b);
        if (fields.Count > 1) TryFloatExact(fields[1], out r);
        var emitterCurve = fields.Count > 2 ? ParseCurve(fields[2]) : [];
        var ageCurve = fields.Count > 3 ? ParseCurve(fields[3]) : [];
        if (!has && r == 0f && emitterCurve.Count == 0 && ageCurve.Count == 0) return false;
        value = new ParticleValue { Base = b, Random = Math.Abs(r), HasBase = has, EmitterCurve = emitterCurve, Curve = ageCurve };
        return true;
    }
    public static List<ParticleCurveKey> ParseCurve(string text)
    {
        var list = new List<ParticleCurveKey>(); if (string.IsNullOrWhiteSpace(text)) return list;
        var open = text.IndexOf('('); var close = text.LastIndexOf(')');
        if (open < 0) return list;
        text = text[(open + 1)..(close > open ? close : text.Length)];
        // A leading empty serialized key means the curve begins at (0, 0), e.g. (;0.2,1;1).
        if (text.TrimStart().StartsWith(';')) list.Add(new(0f, 0f));
        foreach (var group in text.Split([';', '|'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            // Extra serialized numbers are spline tangent/mode data, not additional keys or values.
            var fields = group.Split(',', StringSplitOptions.TrimEntries);
            float time = 0f, curveValue = 0f;
            var hasTime = fields.Length > 0 && TryFloatExact(fields[0], out time);
            var hasValue = fields.Length > 1 && TryFloatExact(fields[1], out curveValue);
            if (hasTime && hasValue) list.Add(new(time, curveValue));
            else if (!hasTime && hasValue) list.Add(new(0f, curveValue));
            else if (hasTime) list.Add(new(time, 0f));
        }
        return list.OrderBy(k => k.Time).ToList();
    }
    private static List<string> SplitTopLevel(string text)
    {
        var result = new List<string>(); var depth = 0; var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '(') depth++;
            else if (text[i] == ')') depth = Math.Max(0, depth - 1);
            else if (text[i] == ',' && depth == 0) { result.Add(text[start..i].Trim()); start = i + 1; }
        }
        result.Add(text[start..].Trim()); return result;
    }
    private static bool TryFloatExact(string text, out float value) =>
        float.TryParse(text?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    internal static bool TryFloat(string text, out float value)
    {
        value = 0; if (string.IsNullOrWhiteSpace(text)) return false; var m = NumberRegex().Match(text);
        return m.Success && float.TryParse(m.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }
    private static float ParseFloat(string s) => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
    private static bool ParseBool(string s, bool fallback) => string.IsNullOrWhiteSpace(s) ? fallback : s.Equals("true", StringComparison.OrdinalIgnoreCase) || s.Equals("yes", StringComparison.OrdinalIgnoreCase) || TryFloat(s, out var v) && v != 0;
    private static string NormalizeParticlePath(string path) { var p = PakFiles.Normalize(path ?? ""); return p.StartsWith("game/", StringComparison.Ordinal) || PakFiles.Exists(p) ? p : "game/" + p; }
    private static void Index(ParticleEffectDefinition e, string parent, Dictionary<string, ParticleEffectDefinition> d) { var q = parent.Length == 0 ? e.Name : parent + "." + e.Name; d.TryAdd(q, e); d.TryAdd(e.Name, e); foreach (var c in e.Children) Index(c, q, d); }
    private static bool IsEffect(XElement x) => IsName(x, "ParticleEffect", "Particles") && (x.Attributes().Any(a => a.Name.LocalName.Equals("Name", StringComparison.OrdinalIgnoreCase)) || x.Elements().Any(e => IsName(e, "Params", "Parameters")));
    private static bool IsName(XElement x, params string[] names) => names.Any(n => x.Name.LocalName.Equals(n, StringComparison.OrdinalIgnoreCase));
    private static string Attr(XElement x, params string[] names) => x?.Attributes().FirstOrDefault(a => names.Any(n => a.Name.LocalName.Equals(n, StringComparison.OrdinalIgnoreCase)))?.Value;
}
