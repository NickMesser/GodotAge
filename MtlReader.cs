using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Xml;

namespace AAEmu.GodotViewer;

/// <summary>One &lt;Texture&gt; slot of a material.</summary>
public sealed class MtlTexture
{
    /// <summary>Slot name as written: Diffuse, Normalmap, Bumpmap, Specular, Detail, Opacity, Decal, Environment, SubSurface, Custom, "[1] Custom".</summary>
    public string Map = "";

    /// <summary>File attribute exactly as written (mixed case, sometimes without "game/", sometimes .tif).</summary>
    public string File = "";

    /// <summary>Normalised pak path: lower case, forward slashes, "game/" prefix, ".dds" extension. Empty when File is empty.</summary>
    public string PakPath = "";

    /// <summary>TexMod tiling (default 1).</summary>
    public float TileU = 1, TileV = 1;

    /// <summary>TexMod offsets / rotation (default 0; not seen in the sampled files but part of the CE3 format).</summary>
    public float OffsetU, OffsetV, RotateW;

    /// <summary>False when the texture has IsTileU/IsTileV="0" (clamp instead of repeat).</summary>
    public bool RepeatU = true, RepeatV = true;

    /// <summary>True when TexMod has U/V oscillators (animated UVs, e.g. water); static viewers can ignore it.</summary>
    public bool Animated;

    public string TexType = "";
}

/// <summary>One material (a sub-material of a multi-material, or a leaf material file).</summary>
public sealed class MtlMaterial
{
    // CE3 EMaterialFlags bits seen in ArcheAge files.
    public const uint FlagWire = 0x1, FlagTwoSided = 0x2, FlagAdditive = 0x4, FlagDetailDecal = 0x8, FlagNoShadow = 0x20,
        FlagPureChild = 0x80, FlagMultiSubmtl = 0x100, FlagNoDraw = 0x400, FlagHideOnBreak = 0x20000;

    public string Name = "";
    public string Shader = "";
    public string SurfaceType = "";
    public uint Flags;
    public string GenMask = "";

    /// <summary>
    /// Diffuse tint (linear) multiplied with the diffuse texture. Values are low (median 0.31 per channel over 529
    /// textured materials) because CryEngine's HDR lighting compensates; in a plain Godot scene multiplying the albedo
    /// by it makes everything dark, so use it only for untextured materials or normalise it.
    /// </summary>
    public Vector3 DiffuseColor = Vector3.One;
    public Vector3 SpecularColor;
    public Vector3 EmissiveColor;
    public float Shininess;
    public float SpecularLevel = 1;
    public float GlowAmount;

    /// <summary>1 = opaque. Below 1 the engine alpha-blends (glass, water, fades).</summary>
    public float Opacity = 1;

    /// <summary>0 = off. Otherwise alpha-test threshold against the diffuse texture's alpha (DXT5 or DXT1 1-bit alpha).</summary>
    public float AlphaTest;

    /// <summary>Raw sRGB attribute (2 in nearly every file, meaning unverified). Use the texture's own flag, <see cref="CryDdsInfo.IsSrgb"/>.</summary>
    public int SRgb;

    public List<MtlTexture> Textures = [];

    /// <summary>PublicParams attributes (shader-specific tweakables), raw strings.</summary>
    public Dictionary<string, string> PublicParams = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Nothing to draw: Nodraw shader (physics proxies, collision helpers) or the NoDraw flag.</summary>
    public bool IsNoDraw => (Flags & FlagNoDraw) != 0 || Shader.Equals("nodraw", StringComparison.OrdinalIgnoreCase);

    public bool IsTwoSided => (Flags & FlagTwoSided) != 0;
    public bool IsAdditive => (Flags & FlagAdditive) != 0;

    /// <summary>Detail/decal flag: a decal layer pasted over other geometry (draw with depth bias, no shadows).</summary>
    public bool IsDecal => (Flags & FlagDetailDecal) != 0;

    public bool CastsNoShadow => (Flags & FlagNoShadow) != 0;
    public bool IsAlphaBlended => Opacity < 0.999f;
    public bool IsAlphaTested => AlphaTest > 0;

    /// <summary>Water-like shaders that a plain PBR material won't reproduce.</summary>
    public bool IsSpecialShader => Shader.Equals("Liquid", StringComparison.OrdinalIgnoreCase)
                                   || Shader.Equals("WaterSurface", StringComparison.OrdinalIgnoreCase)
                                   || Shader.Equals("Glass", StringComparison.OrdinalIgnoreCase);

    public MtlTexture Texture(string map)
    {
        foreach (var t in Textures)
            if (t.Map.Equals(map, StringComparison.OrdinalIgnoreCase))
                return t;
        return null;
    }

    public MtlTexture DiffuseMap => Texture("Diffuse");

    /// <summary>Normal map: "Normalmap", or the older "Bumpmap" slot.</summary>
    public MtlTexture NormalMap => Texture("Normalmap") ?? Texture("Bumpmap");

    public MtlTexture SpecularMap => Texture("Specular");
    public MtlTexture DetailMap => Texture("Detail");
    public MtlTexture OpacityMap => Texture("Opacity");
    public MtlTexture DecalMap => Texture("Decal");
    public MtlTexture EmittanceMap => Texture("Emittance");
}

/// <summary>A parsed .mtl file.</summary>
public sealed class MtlFile
{
    /// <summary>
    /// Sub-materials in file order. A mesh subset's MaterialIndex is a position in this list (the names in the .cgf
    /// often differ, e.g. "Material #206", and must be ignored). A leaf .mtl (no SubMaterials) has one entry that
    /// every subset uses.
    /// </summary>
    public List<MtlMaterial> SubMaterials = [];

    public bool IsMulti;

    /// <summary>
    /// Material for a subset. Leaf file: always the single material. Multi file: the indexed slot, or null when the
    /// index is out of range (the engine then shows its default grey material).
    /// </summary>
    public MtlMaterial ForSubset(int materialIndex)
    {
        if (!IsMulti)
            return SubMaterials.Count > 0 ? SubMaterials[0] : null;
        return materialIndex >= 0 && materialIndex < SubMaterials.Count ? SubMaterials[materialIndex] : null;
    }
}

/// <summary>
/// Reader for ArcheAge .mtl material files (plain XML, CryEngine 3 layout) and the path rules that connect
/// models, brushes and textures to pak files.
/// <para>
/// Lookup rules (checked against pak_filelist.txt):
/// <list type="bullet">
/// <item>Model default: the .cgf's MtlName chunk holds a bare name ("cliff_rock_b"); the file is
///   <c>&lt;folder of the .cgf&gt;/&lt;name&gt;.mtl</c>, lower-cased: 367/367 models of the three cells, 480/498 of
///   a random pak sample. A few names are game-root paths ("Objects/Characters/..."), models under a locale folder
///   (zh_cn, ja, ru, en_us) use the parent folder's .mtl; see <see cref="ResolveModelMaterial"/>. Effects often
///   carry 3ds Max leftovers ("01 - Default") that resolve nowhere: the game assigns their material in code.</item>
/// <item>Brush override: object.dat's secondTable entry at the brush's MaterialId is a pak path without extension;
///   append ".mtl". 386/386 entries exist. Every brush in the sampled cells has one (4004 equal the model's default,
///   362 differ, e.g. a mossy variant), so prefer it and fall back to the model default when missing.</item>
/// <item>Textures: File attribute lower-cased, backslashes to slashes, leading '/' dropped, "game/" prefixed when
///   missing (312 of 1947 references), .tif and other extensions replaced by .dds (the pak only holds .dds).</item>
/// <item>Pak paths are all lower case; compare case-insensitively everywhere.</item>
/// <item>Not found: use a default grey material (engine behaviour); for a missing texture, keep the material colours.</item>
/// </list>
/// </para>
/// </summary>
public static class MtlReader
{
    public static MtlFile Parse(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length >= 8 && Encoding.ASCII.GetString(data, 0, 7) == "CryXmlB")
            throw new InvalidDataException("binary CryXmlB material (not seen in ArcheAge data) is not supported");
        using var ms = new MemoryStream(data);
        var doc = new XmlDocument();
        doc.Load(ms); // detects BOM / encoding
        return Parse(doc);
    }

    public static MtlFile Parse(string xml)
    {
        var doc = new XmlDocument();
        doc.LoadXml(xml);
        return Parse(doc);
    }

    private static MtlFile Parse(XmlDocument doc)
    {
        var root = doc.DocumentElement;
        if (root == null || root.Name != "Material")
            throw new InvalidDataException("root element is not <Material>");
        var file = new MtlFile();
        var subs = root["SubMaterials"];
        if (subs != null)
        {
            file.IsMulti = true;
            foreach (XmlNode n in subs.ChildNodes)
                if (n is XmlElement e && e.Name == "Material")
                    file.SubMaterials.Add(ParseMaterial(e));
        }
        else
            file.SubMaterials.Add(ParseMaterial(root));
        return file;
    }

    private static MtlMaterial ParseMaterial(XmlElement e)
    {
        var m = new MtlMaterial
        {
            Name = e.GetAttribute("Name"),
            Shader = e.GetAttribute("Shader"),
            SurfaceType = e.GetAttribute("SurfaceType"),
            GenMask = e.GetAttribute("GenMask"),
            Flags = (uint)ParseLong(e.GetAttribute("MtlFlags")),
            DiffuseColor = ParseVec3(e.GetAttribute("Diffuse"), Vector3.One),
            SpecularColor = ParseVec3(e.GetAttribute("Specular"), Vector3.Zero),
            EmissiveColor = ParseVec3(e.GetAttribute("Emissive"), Vector3.Zero),
            Shininess = ParseFloat(e.GetAttribute("Shininess"), 0),
            SpecularLevel = ParseFloat(e.GetAttribute("SpecularLevel"), 1),
            GlowAmount = ParseFloat(e.GetAttribute("GlowAmount"), 0),
            Opacity = ParseFloat(e.GetAttribute("Opacity"), 1),
            AlphaTest = ParseFloat(e.GetAttribute("AlphaTest"), 0),
            SRgb = (int)ParseLong(e.GetAttribute("sRGB")),
        };
        var textures = e["Textures"];
        if (textures != null)
            foreach (XmlNode n in textures.ChildNodes)
            {
                if (n is not XmlElement t || t.Name != "Texture")
                    continue;
                var tex = new MtlTexture
                {
                    Map = t.GetAttribute("Map"),
                    File = t.GetAttribute("File"),
                    TexType = t.GetAttribute("TexType"),
                    RepeatU = t.GetAttribute("IsTileU") != "0",
                    RepeatV = t.GetAttribute("IsTileV") != "0",
                };
                tex.PakPath = TexturePakPath(tex.File);
                if (t["TexMod"] is { } mod)
                {
                    tex.TileU = ParseFloat(mod.GetAttribute("TileU"), 1);
                    tex.TileV = ParseFloat(mod.GetAttribute("TileV"), 1);
                    tex.OffsetU = ParseFloat(mod.GetAttribute("OffsetU"), 0);
                    tex.OffsetV = ParseFloat(mod.GetAttribute("OffsetV"), 0);
                    tex.RotateW = ParseFloat(mod.GetAttribute("RotateW"), 0);
                    foreach (XmlAttribute a in mod.Attributes)
                        if (a.Name.Contains("Oscillator", StringComparison.OrdinalIgnoreCase) && ParseFloat(a.Value, 0) != 0)
                            tex.Animated = true;
                }
                m.Textures.Add(tex);
            }
        if (e["PublicParams"] is { } pp)
            foreach (XmlAttribute a in pp.Attributes)
                m.PublicParams[a.Name] = a.Value;
        return m;
    }

    // ---------------------------------------------------------------- path rules

    /// <summary>Default material of a model: "&lt;cgf folder&gt;/&lt;MtlName&gt;.mtl" (lower case).</summary>
    public static string ModelMaterialPath(string cgfPakPath, string mtlName)
    {
        if (string.IsNullOrEmpty(mtlName))
            return "";
        var name = Normalize(mtlName);
        if (!name.EndsWith(".mtl", StringComparison.Ordinal))
            name += ".mtl";
        if (name.Contains('/'))
            return name.StartsWith("game/", StringComparison.Ordinal) ? name : "game/" + name; // not seen; CE resolves from the game root
        var cgf = Normalize(cgfPakPath);
        var slash = cgf.LastIndexOf('/');
        return slash >= 0 ? cgf[..(slash + 1)] + name : name;
    }

    /// <summary>Brush material override: object.dat secondTable entry (path without extension) + ".mtl".</summary>
    public static string BrushMaterialPath(string secondTableEntry)
    {
        if (string.IsNullOrEmpty(secondTableEntry))
            return "";
        var p = Normalize(secondTableEntry);
        return p.EndsWith(".mtl", StringComparison.Ordinal) ? p : p + ".mtl";
    }

    /// <summary>
    /// Finds a model's default material. Candidates in order: <see cref="ModelMaterialPath"/>; for a path-like name,
    /// its file name next to the .cgf; for a model in a locale folder (".../zh_cn/x.cgf", also ja, ru, en_us) the
    /// parent folder; finally "&lt;cgf name&gt;.mtl". Returns "" when none exists.
    /// </summary>
    public static string ResolveModelMaterial(string cgfPakPath, string mtlName, Func<string, bool> exists)
    {
        var cgf = Normalize(cgfPakPath);
        var slash = cgf.LastIndexOf('/');
        var folder = slash >= 0 ? cgf[..(slash + 1)] : "";
        var candidates = new List<string>();
        if (!string.IsNullOrEmpty(mtlName))
        {
            candidates.Add(ModelMaterialPath(cgfPakPath, mtlName));
            var baseName = Normalize(mtlName);
            baseName = baseName[(baseName.LastIndexOf('/') + 1)..];
            if (!baseName.EndsWith(".mtl", StringComparison.Ordinal))
                baseName += ".mtl";
            candidates.Add(folder + baseName);
            var parent = LocaleParent(folder);
            if (parent != null)
                candidates.Add(parent + baseName);
        }
        if (cgf.EndsWith(".cgf", StringComparison.Ordinal))
            candidates.Add(cgf[..^4] + ".mtl");
        foreach (var c in candidates)
            if (c.Length > 0 && exists(c))
                return c;
        return "";
    }

    /// <summary>
    /// Picks the material file for a brush: the object.dat override when it exists, else the model default
    /// (<see cref="ResolveModelMaterial"/>). Returns "" when nothing exists (use a grey default material then).
    /// </summary>
    public static string ResolveBrushMaterial(string secondTableEntry, string cgfPakPath, string cgfMtlName, Func<string, bool> exists)
    {
        var over = BrushMaterialPath(secondTableEntry);
        if (over.Length > 0 && exists(over))
            return over;
        return ResolveModelMaterial(cgfPakPath, cgfMtlName, exists);
    }

    /// <summary>"a/b/zh_cn/" -> "a/b/" for locale folders (2 letters or ll_cc), else null.</summary>
    private static string LocaleParent(string folder)
    {
        var trimmed = folder.TrimEnd('/');
        var slash = trimmed.LastIndexOf('/');
        if (slash < 0)
            return null;
        var last = trimmed[(slash + 1)..];
        var isLocale = (last.Length == 2 || (last.Length == 5 && last[2] == '_')) && last.Replace("_", "").All(char.IsAsciiLetterLower);
        return isLocale ? trimmed[..(slash + 1)] : null;
    }

    /// <summary>Texture File attribute to pak path (see class remarks).</summary>
    public static string TexturePakPath(string file)
    {
        if (string.IsNullOrWhiteSpace(file))
            return "";
        var p = Normalize(file.Trim());
        if (p.StartsWith("./", StringComparison.Ordinal))
            p = p[2..];
        if (!p.StartsWith("game/", StringComparison.Ordinal))
            p = "game/" + p;
        var dot = p.LastIndexOf('.');
        if (dot > p.LastIndexOf('/'))
            p = p[..dot];
        return p + ".dds";
    }

    private static string Normalize(string path) => path.Replace('\\', '/').TrimStart('/').ToLowerInvariant();

    // ---------------------------------------------------------------- attribute parsing

    private static float ParseFloat(string s, float fallback) =>
        float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    private static long ParseLong(string s) =>
        long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;

    private static Vector3 ParseVec3(string s, Vector3 fallback)
    {
        if (string.IsNullOrEmpty(s))
            return fallback;
        var parts = s.Split(',');
        if (parts.Length < 3)
            return fallback;
        return new Vector3(ParseFloat(parts[0], fallback.X), ParseFloat(parts[1], fallback.Y), ParseFloat(parts[2], fallback.Z));
    }
}
