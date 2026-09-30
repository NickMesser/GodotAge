#nullable enable
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace AAEmu.GodotViewer;

/// <summary>A renderable model instance from a CryEngine prefab.</summary>
public sealed record PrefabPart(
    string ModelPath,
    string? MaterialPath,
    Matrix4x4 Transform,
    string SourceType,
    string? Name);

/// <summary>A light instance from a CryEngine prefab.</summary>
public sealed record PrefabLight(
    Matrix4x4 Transform,
    Vector3 Color,
    float Intensity,
    float Radius,
    bool Active,
    float? ProjectorFov,
    string? ProjectorTexture,
    string? Name);

/// <summary>The flattened renderable contents of one named prefab.</summary>
public sealed record PrefabResolution(
    IReadOnlyList<PrefabPart> Parts,
    IReadOnlyList<PrefabLight> Lights,
    IReadOnlyList<string> Diagnostics);

/// <summary>
/// Reads ArcheAge's CryEngine <c>PrefabsLibrary</c> XML without Godot dependencies.
/// Coordinates remain in CryEngine axes: +X east, +Y north, +Z up.
/// </summary>
public static class PrefabReader
{
    private const int MaxReferenceDepth = 32;

    /// <summary>
    /// Resolves a prefab contained in <paramref name="libraryXml"/>. Nested prefab references are
    /// reported in <see cref="PrefabResolution.Diagnostics"/> because no library reader was supplied.
    /// </summary>
    public static PrefabResolution Resolve(byte[] libraryXml, string memberName)
        => ResolveCore(libraryXml, memberName, null, null);

    /// <summary>
    /// Resolves a prefab and follows nested <c>Type="Prefab"</c> instances. The delegate receives a
    /// normalized pak path such as <c>game/prefabs/common.xml</c> and returns its bytes, or null if absent.
    /// <paramref name="libraryPath"/> should be the normalized path of <paramref name="libraryXml"/>.
    /// </summary>
    public static PrefabResolution ResolveWithReferences(
        byte[] libraryXml,
        string memberName,
        Func<string, byte[]?> readLibrary,
        string? libraryPath = null)
    {
        ArgumentNullException.ThrowIfNull(readLibrary);
        return ResolveCore(libraryXml, memberName, readLibrary, libraryPath);
    }

    private static PrefabResolution ResolveCore(
        byte[] libraryXml,
        string memberName,
        Func<string, byte[]?>? readLibrary,
        string? libraryPath)
    {
        ArgumentNullException.ThrowIfNull(libraryXml);
        memberName ??= string.Empty;

        var result = new Builder(readLibrary);
        var document = Load(libraryXml);
        var normalizedPath = libraryPath is null ? null : NormalizePakPath(libraryPath);
        result.AddLibrary(document, memberName, Matrix4x4.Identity, normalizedPath, 0, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        return new PrefabResolution(result.Parts, result.Lights, result.Diagnostics);
    }

    private sealed class Builder(Func<string, byte[]?>? readLibrary)
    {
        private readonly Func<string, byte[]?>? _readLibrary = readLibrary;
        private readonly Dictionary<string, XDocument> _documents = new(StringComparer.OrdinalIgnoreCase);

        public List<PrefabPart> Parts { get; } = [];
        public List<PrefabLight> Lights { get; } = [];
        public List<string> Diagnostics { get; } = [];

        public void AddLibrary(
            XDocument document,
            string memberName,
            Matrix4x4 rootTransform,
            string? libraryPath,
            int depth,
            HashSet<string> referenceStack)
        {
            if (depth > MaxReferenceDepth)
                throw new InvalidDataException($"Prefab reference depth exceeded {MaxReferenceDepth}.");

            var member = SelectMember(document, memberName);
            var objects = member.Element("Objects");
            if (objects is null)
                return;

            var allObjects = objects.DescendantsAndSelf()
                .Where(e => e.Name.LocalName == "Object")
                .ToArray();
            var byId = allObjects
                .Select(e => (Id: Attr(e, "Id"), Element: e))
                .Where(x => !string.IsNullOrWhiteSpace(x.Id))
                .GroupBy(x => x.Id!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Element, StringComparer.OrdinalIgnoreCase);
            var transformCache = new Dictionary<XElement, Matrix4x4>();

            Matrix4x4 ObjectTransform(XElement element, HashSet<XElement>? active = null)
            {
                if (transformCache.TryGetValue(element, out var cached))
                    return cached;
                active ??= [];
                if (!active.Add(element))
                    throw new InvalidDataException("Cyclic prefab object Parent relationship.");

                var transform = ReadTransform(element);
                var parentId = Attr(element, "Parent");
                if (parentId is not null && byId.TryGetValue(parentId, out var parent))
                    transform *= ObjectTransform(parent, active);
                else if (element.Parent?.Name.LocalName == "Objects" &&
                         element.Parent.Parent?.Name.LocalName == "Object")
                    transform *= ObjectTransform(element.Parent.Parent, active);

                active.Remove(element);
                transformCache[element] = transform;
                return transform;
            }

            foreach (var element in allObjects)
            {
                var type = Attr(element, "Type") ?? string.Empty;
                var transform = ObjectTransform(element) * rootTransform;
                var model = ModelPath(element, type);
                if (model is not null)
                {
                    Parts.Add(new PrefabPart(
                        NormalizeAssetPath(model),
                        NormalizeMaterialPath(Attr(element, "Material")),
                        transform,
                        SourceType(element, type),
                        Attr(element, "Name")));
                }

                if (IsLight(element, type))
                    Lights.Add(ReadLight(element, transform));

                if (type.Equals("Prefab", StringComparison.OrdinalIgnoreCase))
                    AddReference(document, element, transform, libraryPath, depth, referenceStack);
            }
        }

        private void AddReference(
            XDocument currentDocument,
            XElement element,
            Matrix4x4 transform,
            string? currentPath,
            int depth,
            HashSet<string> referenceStack)
        {
            var prefabName = Attr(element, "PrefabName")?.Trim();
            if (string.IsNullOrEmpty(prefabName))
            {
                Diagnostics.Add("Nested Prefab object has no PrefabName.");
                return;
            }

            var local = FindMember(currentDocument, prefabName);
            XDocument targetDocument;
            string? targetPath = currentPath;
            if (local is not null)
            {
                targetDocument = currentDocument;
            }
            else
            {
                var libraryName = prefabName.Split('.', 2)[0];
                targetPath = $"game/prefabs/{libraryName.ToLowerInvariant()}.xml";
                if (_readLibrary is null)
                {
                    Diagnostics.Add($"Unresolved nested prefab '{prefabName}' (requires '{targetPath}').");
                    return;
                }

                if (!_documents.TryGetValue(targetPath, out targetDocument!))
                {
                    var bytes = _readLibrary(targetPath);
                    if (bytes is null)
                    {
                        Diagnostics.Add($"Missing nested prefab library '{targetPath}' for '{prefabName}'.");
                        return;
                    }
                    targetDocument = Load(bytes);
                    _documents[targetPath] = targetDocument;
                }
            }

            // Name plus path identifies the logical reference; using a stack permits repeated siblings.
            var key = $"{targetPath ?? "<root>"}|{prefabName}";
            if (!referenceStack.Add(key))
            {
                Diagnostics.Add($"Cyclic nested prefab reference '{prefabName}'.");
                return;
            }
            try
            {
                AddLibrary(targetDocument, prefabName, transform, targetPath, depth + 1, referenceStack);
            }
            finally
            {
                referenceStack.Remove(key);
            }
        }
    }

    private static XDocument Load(byte[] bytes)
    {
        try
        {
            return LoadXml(new MemoryStream(bytes, writable: false));
        }
        catch (XmlException exception) when (exception.Message.Contains("undeclared prefix", StringComparison.OrdinalIgnoreCase))
        {
            // One shipped library (posture.xml) embeds legacy FlowGraph element names such as
            // <Time:RandomDelay> without namespace declarations. They are outside the prefab data;
            // make only element-name colons XML-safe and retry without changing attribute values.
            var text = Encoding.UTF8.GetString(bytes);
            text = Regex.Replace(text, @"<(\/?)(([A-Za-z_][\w.-]*):([A-Za-z_][\w.-]*))", "<$1$3_$4");
            return LoadXml(new MemoryStream(Encoding.UTF8.GetBytes(text), writable: false));
        }
    }

    private static XDocument LoadXml(Stream stream)
    {
        using (stream)
        using (var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true,
        }))
            return XDocument.Load(reader, LoadOptions.None);
    }

    private static XElement SelectMember(XDocument document, string memberName)
    {
        var members = document.Root?.Elements().Where(e => e.Name.LocalName == "Prefab").ToArray() ?? [];
        if (!string.IsNullOrWhiteSpace(memberName))
            return FindMember(document, memberName) ??
                   throw new KeyNotFoundException($"Prefab member '{memberName}' was not found.");

        if (members.Length == 1)
            return members[0];

        var libraryName = Attr(document.Root, "Name");
        var conventional = libraryName is null ? null :
            members.FirstOrDefault(e => string.Equals(Attr(e, "Name"), libraryName, StringComparison.OrdinalIgnoreCase));
        return conventional ?? throw new InvalidDataException(
            $"Library has {members.Length} prefab members; memberName is required.");
    }

    private static XElement? FindMember(XDocument document, string name)
    {
        var normalized = name.Trim().Replace('/', '.').Replace('\\', '.');
        var members = document.Root?.Elements().Where(e => e.Name.LocalName == "Prefab").ToArray() ?? [];
        var exact = members
            .FirstOrDefault(e => e.Name.LocalName == "Prefab" &&
                string.Equals(Attr(e, "Name"), normalized, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
            return exact;

        // Nested PrefabName values are often globally qualified ("common.interior.chair"), while
        // members inside common.xml are named "interior.chair".
        var libraryName = Attr(document.Root, "Name");
        var prefix = libraryName + ".";
        if (!string.IsNullOrEmpty(libraryName) && normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            var unqualified = normalized[prefix.Length..];
            return members.FirstOrDefault(e =>
                string.Equals(Attr(e, "Name"), unqualified, StringComparison.OrdinalIgnoreCase));
        }
        return null;
    }

    /// <summary>
    /// CryEngine XML stores quaternions as <c>w,x,y,z</c>. System.Numerics expects <c>x,y,z,w</c>.
    /// With its row-vector convention, the local matrix is <c>Scale * Rotation * Translation</c> and
    /// hierarchy composition is <c>local * parent</c>; use <see cref="Vector3.Transform(Vector3, Matrix4x4)"/>.
    /// </summary>
    public static Matrix4x4 ReadTransform(XElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        var position = ReadVector(Attr(element, "Pos"), Vector3.Zero);
        var scale = ReadVector(Attr(element, "Scale"), Vector3.One);
        var values = ReadFloats(Attr(element, "Rotate"), 4);
        var rotation = values is null
            ? Quaternion.Identity
            : Quaternion.Normalize(new Quaternion(values[1], values[2], values[3], values[0]));
        return Matrix4x4.CreateScale(scale) *
               Matrix4x4.CreateFromQuaternion(rotation) *
               Matrix4x4.CreateTranslation(position);
    }

    private static string? ModelPath(XElement element, string type)
    {
        // The shipped libraries also use Entity2 for a small number of CGF model instances
        // (for example ship.xml / caravel.body), with the same Prefab attribute as Brush.
        if (type.Equals("Brush", StringComparison.OrdinalIgnoreCase) ||
            type.Equals("Entity2", StringComparison.OrdinalIgnoreCase))
            return NonBlank(Attr(element, "Prefab"));
        if (type.Equals("GeomEntity", StringComparison.OrdinalIgnoreCase))
            return NonBlank(Attr(element, "Geometry"));
        if (!type.Equals("Entity", StringComparison.OrdinalIgnoreCase) &&
            Attr(element, "EntityClass") is null)
            return null;

        var geometry = NonBlank(Attr(element, "Geometry"));
        if (geometry is not null)
            return geometry;
        var properties = element.Descendants().FirstOrDefault(e => e.Name.LocalName == "Properties");
        return properties is null ? null :
            NonBlank(Attr(properties, "object_Model")) ??
            NonBlank(Attr(properties, "objModel")) ??
            NonBlank(Attr(properties, "Model"));
    }

    private static string SourceType(XElement element, string type)
        => Attr(element, "EntityClass") is { Length: > 0 } entityClass ? $"{type}:{entityClass}" : type;

    private static bool IsLight(XElement element, string type)
    {
        var entityClass = Attr(element, "EntityClass");
        return type.Equals("Light", StringComparison.OrdinalIgnoreCase) ||
               entityClass is not null && entityClass.Contains("Light", StringComparison.OrdinalIgnoreCase);
    }

    private static PrefabLight ReadLight(XElement element, Matrix4x4 transform)
    {
        var properties = element.Descendants().FirstOrDefault(e => e.Name.LocalName == "Properties");
        var colorNode = properties?.Descendants().FirstOrDefault(e => e.Name.LocalName == "Color");
        var projector = properties?.Descendants().FirstOrDefault(e => e.Name.LocalName == "Projector");
        var color = ReadVector(
            Attr(colorNode, "clrDiffuse") ?? Attr(properties, "clrDiffuse") ?? Attr(properties, "vColor"),
            Vector3.One);
        return new PrefabLight(
            transform,
            color,
            ReadFloat(Attr(colorNode, "fDiffuseMultiplier") ?? Attr(properties, "fDiffuseMultiplier"), 1f),
            ReadFloat(Attr(properties, "Radius") ?? Attr(properties, "radius"), 10f),
            ReadBool(Attr(properties, "bActive"), true),
            ReadNullableFloat(Attr(projector, "fProjectorFov")),
            NormalizeOptionalAssetPath(Attr(projector, "texture_Texture")),
            Attr(element, "Name"));
    }

    private static string NormalizeAssetPath(string path)
    {
        path = path.Trim().Replace('\\', '/').TrimStart('/');
        while (path.Contains("//", StringComparison.Ordinal))
            path = path.Replace("//", "/", StringComparison.Ordinal);
        if (!path.StartsWith("game/", StringComparison.OrdinalIgnoreCase))
            path = "game/" + path;
        return path.ToLowerInvariant();
    }

    private static string NormalizePakPath(string path) => NormalizeAssetPath(path);

    private static string? NormalizeMaterialPath(string? path)
    {
        path = NonBlank(path);
        if (path is null)
            return null;
        path = NormalizeAssetPath(path);
        return Path.HasExtension(path) ? path : path + ".mtl";
    }

    private static string? NormalizeOptionalAssetPath(string? path)
        => NonBlank(path) is { } value ? NormalizeAssetPath(value) : null;

    private static string? NonBlank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? Attr(XElement? element, string name)
        => element?.Attributes().FirstOrDefault(a =>
            a.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value;

    private static Vector3 ReadVector(string? text, Vector3 fallback)
    {
        var values = ReadFloats(text, 3);
        return values is null ? fallback : new Vector3(values[0], values[1], values[2]);
    }

    private static float[]? ReadFloats(string? text, int count)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var fields = text.Split(',', StringSplitOptions.TrimEntries);
        if (fields.Length != count)
            throw new InvalidDataException($"Expected {count} comma-separated floats, got '{text}'.");
        var result = new float[count];
        for (var i = 0; i < count; i++)
            if (!float.TryParse(fields[i], NumberStyles.Float, CultureInfo.InvariantCulture, out result[i]) ||
                !float.IsFinite(result[i]))
                throw new InvalidDataException($"Invalid finite float '{fields[i]}' in '{text}'.");
        return result;
    }

    private static float ReadFloat(string? text, float fallback)
        => float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && float.IsFinite(value)
            ? value : fallback;

    private static float? ReadNullableFloat(string? text)
        => float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && float.IsFinite(value)
            ? value : null;

    private static bool ReadBool(string? text, bool fallback)
        => text?.Trim() switch { "1" => true, "0" => false, _ => bool.TryParse(text, out var value) ? value : fallback };
}
