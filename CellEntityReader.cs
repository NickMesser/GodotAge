#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Xml.Linq;

namespace AAEmu.GodotViewer;

// entities.xml of a cell: <Mission><Objects> holding <Entity> (game entities) and <Object Type=...> (editor objects:
// VisArea, Portal, OccluderArea, VoxelObject, DistanceCloud, Group, ...).
//
// Conventions (verified): Pos is cell-local metres ("x,y,z"); Rotate is a quaternion written w,x,y,z (yaw-only
// rotations appear as "w,0,0,z"; DistanceCloud objects carry the same w first while object.dat stores x,y,z,w);
// Scale is "x,y,z". Missing Rotate / Scale mean identity. An entity with ParentId is placed relative to that
// entity (566 in the client, all small offsets); WorldTransform resolves the chain.
//
// Visible geometry: AnimObject / AnimObject_Prologue / BasicEntity / RigidBodyEx (Properties object_Model: .cgf,
// .cga, .chr or .cdf), GeomEntity (Geometry attribute). Lights: Light, SimpleLight, IndirectLight,
// SimpleIndirectLight. Boids (Birds, Fish, Chickens, Crabs, Frogs) spawn animated creatures around the point.
// Everything else (ParticleEffect, sound volumes, AreaShape, FogVolume, ...) has no mesh.

public enum CellModelKind
{
    None,
    /// <summary>.cgf static mesh.</summary>
    Static,
    /// <summary>.cga animated hierarchy (doors, windows, windmills): draw its default pose.</summary>
    Animated,
    /// <summary>.chr skinned model.</summary>
    Character,
    /// <summary>.cdf character definition (attachments list).</summary>
    CharacterDefinition,
}

public sealed class CellEntity
{
    public string Name { get; internal set; } = "";
    public string EntityClass { get; internal set; } = "";
    public int EntityId { get; internal set; }
    public string? Guid { get; internal set; }
    public int? ParentId { get; internal set; }
    public CellEntity? Parent { get; internal set; }
    public string? Layer { get; internal set; }
    public string? Archetype { get; internal set; }
    public Vector3 Position { get; internal set; }
    /// <summary>Parsed from "w,x,y,z".</summary>
    public Quaternion Rotation { get; internal set; } = Quaternion.Identity;
    public Vector3 Scale { get; internal set; } = Vector3.One;
    /// <summary>Relative to the parent entity when <see cref="ParentId"/> is set, else cell-local.</summary>
    public Matrix4x4 LocalTransform =>
        Matrix4x4.CreateScale(Scale) * Matrix4x4.CreateFromQuaternion(Rotation) * Matrix4x4.CreateTranslation(Position);
    /// <summary>Cell-local transform (parent chain applied).</summary>
    public Matrix4x4 WorldTransform { get; internal set; } = Matrix4x4.Identity;
    /// <summary>Model path (object_Model property or Geometry attribute), normalised, or null.</summary>
    public string? ModelPath { get; internal set; }
    public CellModelKind ModelKind { get; internal set; }
    /// <summary>Material override (Material attribute), or null.</summary>
    public string? MaterialPath { get; internal set; }
    public bool HiddenInGame { get; internal set; }
    public int MinSpec { get; internal set; }
    public int ViewDistRatio { get; internal set; } = 100;
    public bool CastShadow { get; internal set; }
    public CellLight? Light { get; internal set; }
    /// <summary>All Properties attributes, flattened: "Radius", "Color.clrDiffuse", "Physics.bRigidBody", ...</summary>
    public Dictionary<string, string> Properties { get; } = [];
    public bool HasGeometry => ModelPath != null;
}

public sealed class CellLight
{
    public float Radius { get; internal set; }
    /// <summary>Linear RGB 0..1 (clrDiffuse, or clrColor for SimpleLight).</summary>
    public Vector3 Color { get; internal set; } = Vector3.One;
    public float DiffuseMultiplier { get; internal set; } = 1;
    public float SpecularMultiplier { get; internal set; } = 1;
    public float HdrDynamic { get; internal set; }
    public bool Active { get; internal set; } = true;
    public bool CastShadow { get; internal set; }
    public bool AmbientOnly { get; internal set; }
    public bool Fake { get; internal set; }
    public bool Negative { get; internal set; }
    public bool AffectsThisAreaOnly { get; internal set; }
    /// <summary>Projector texture (spot / projected light), or null for an omni light.</summary>
    public string? ProjectorTexture { get; internal set; }
    public float ProjectorFov { get; internal set; } = 90;
    public int LightStyle { get; internal set; }
}

/// <summary>An editor object (&lt;Object Type=...&gt;). Culling helpers and editor copies of object.dat data; no meshes.</summary>
public sealed class CellEditorObject
{
    public string Type { get; internal set; } = "";
    public string Name { get; internal set; } = "";
    public Vector3 Position { get; internal set; }
    public Quaternion Rotation { get; internal set; } = Quaternion.Identity;
    public Vector3 Scale { get; internal set; } = Vector3.One;
    public string? MaterialPath { get; internal set; }
    /// <summary>Outline points (VisArea, Portal, OccluderArea), relative to Position.</summary>
    public List<Vector3> Points { get; } = [];
    public Dictionary<string, string> Attributes { get; } = [];
}

public sealed class CellEntities
{
    public List<CellEntity> Entities { get; } = [];
    public List<CellEditorObject> EditorObjects { get; } = [];
    /// <summary>Entities whose ParentId matched no entity of the file (none in the client); they are placed as roots.</summary>
    public int UnresolvedParents { get; internal set; }
    public IEnumerable<CellEntity> ModelEntities => Entities.Where(e => e.HasGeometry);
    public IEnumerable<CellEntity> Lights => Entities.Where(e => e.Light != null);
}

public static class CellEntityReader
{
    private static readonly HashSet<string> LightClasses = ["Light", "SimpleLight", "IndirectLight", "SimpleIndirectLight"];

    public static CellEntities Read(byte[] xml)
    {
        var result = new CellEntities();
        var doc = XDocument.Parse(CellBin.XmlText(xml));
        var objects = doc.Root?.Element("Objects");
        if (objects == null)
            return result;
        foreach (var e in objects.Elements())
        {
            if (e.Name.LocalName == "Entity")
                result.Entities.Add(ReadEntity(e));
            else if (e.Name.LocalName == "Object")
                result.EditorObjects.Add(ReadObject(e));
        }

        var byId = new Dictionary<int, CellEntity>();
        foreach (var en in result.Entities)
            byId.TryAdd(en.EntityId, en);
        foreach (var en in result.Entities)
            if (en.ParentId is { } pid)
            {
                if (byId.TryGetValue(pid, out var p) && p != en)
                    en.Parent = p;
                else
                    result.UnresolvedParents++;
            }
        foreach (var en in result.Entities)
            en.WorldTransform = World(en, 0);
        return result;
    }

    private static Matrix4x4 World(CellEntity e, int depth) =>
        e.Parent == null || depth > 32 ? e.LocalTransform : e.LocalTransform * World(e.Parent, depth + 1);

    private static CellEntity ReadEntity(XElement e)
    {
        var en = new CellEntity
        {
            Name = (string?)e.Attribute("Name") ?? "",
            EntityClass = (string?)e.Attribute("EntityClass") ?? "",
            EntityId = CellXml.Int(e, "EntityId"),
            Guid = (string?)e.Attribute("EntityGuid"),
            ParentId = e.Attribute("ParentId") != null ? CellXml.Int(e, "ParentId") : null,
            Layer = (string?)e.Attribute("Layer"),
            Archetype = (string?)e.Attribute("Archetype"),
            Position = CellXml.Vec3(e, "Pos", Vector3.Zero),
            Rotation = CellXml.QuatWxyz(e, "Rotate"),
            Scale = CellXml.Vec3(e, "Scale", Vector3.One),
            MaterialPath = CellPaths.Normalize((string?)e.Attribute("Material")),
            HiddenInGame = CellXml.Bool(e, "HiddenInGame"),
            MinSpec = CellXml.Int(e, "MinSpec"),
            ViewDistRatio = e.Attribute("ViewDistRatio") != null ? CellXml.Int(e, "ViewDistRatio") : 100,
            CastShadow = CellXml.Bool(e, "CastShadow"),
        };
        var props = e.Element("Properties");
        if (props != null)
            Flatten(props, "", en.Properties);

        var model = (string?)e.Attribute("Geometry");
        if (string.IsNullOrWhiteSpace(model))
            en.Properties.TryGetValue("object_Model", out model);
        en.ModelPath = CellPaths.Normalize(model);
        en.ModelKind = KindOf(en.ModelPath);
        if (en.ModelKind == CellModelKind.None)
            en.ModelPath = null;

        if (LightClasses.Contains(en.EntityClass))
            en.Light = ReadLight(en.Properties);
        return en;
    }

    private static CellLight ReadLight(Dictionary<string, string> p)
    {
        string? S(string k) => p.TryGetValue(k, out var v) ? v : null;
        float Fl(string k, float d) => float.TryParse(S(k), NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : d;
        bool B(string k) => S(k) == "1";
        var color = S("Color.clrDiffuse") ?? S("Color.clrColor");
        return new CellLight
        {
            Radius = Fl("Radius", 10),
            Color = color != null ? CellXml.ParseVec3(color, Vector3.One) : Vector3.One,
            DiffuseMultiplier = Fl("Color.fDiffuseMultiplier", Fl("Color.fColorMultiplier", 1)),
            SpecularMultiplier = Fl("Color.fSpecularMultiplier", Fl("Color.fSpecularPercentage", 100) / 100f),
            HdrDynamic = Fl("Color.fHDRDynamic", 0),
            Active = S("bActive") != "0",
            CastShadow = B("Options.bCastShadow"),
            AmbientOnly = B("Options.bAmbientLight"),
            Fake = B("Options.bFakeLight"),
            Negative = B("Test.bNegativeLight"),
            AffectsThisAreaOnly = B("Options.bAffectsThisAreaOnly"),
            ProjectorTexture = CellPaths.Normalize(S("Projector.texture_Texture")),
            ProjectorFov = Fl("Projector.fProjectorFov", 90),
            LightStyle = (int)Fl("Style.nLightStyle", 0),
        };
    }

    private static CellEditorObject ReadObject(XElement e)
    {
        var o = new CellEditorObject
        {
            Type = (string?)e.Attribute("Type") ?? "",
            Name = (string?)e.Attribute("Name") ?? "",
            Position = CellXml.Vec3(e, "Pos", Vector3.Zero),
            Rotation = CellXml.QuatWxyz(e, "Rotate"),
            Scale = CellXml.Vec3(e, "Scale", Vector3.One),
            MaterialPath = CellPaths.Normalize((string?)e.Attribute("Material")),
        };
        foreach (var a in e.Attributes())
            o.Attributes[a.Name.LocalName] = a.Value;
        foreach (var p in e.Element("Points")?.Elements("Point") ?? [])
            o.Points.Add(CellXml.Vec3(p, "Pos", Vector3.Zero));
        return o;
    }

    private static void Flatten(XElement e, string prefix, Dictionary<string, string> into)
    {
        foreach (var a in e.Attributes())
            into[prefix + a.Name.LocalName] = a.Value;
        foreach (var c in e.Elements())
            Flatten(c, prefix + c.Name.LocalName + ".", into);
    }

    private static CellModelKind KindOf(string? path)
    {
        if (path == null)
            return CellModelKind.None;
        if (path.EndsWith(".cgf", StringComparison.Ordinal)) return CellModelKind.Static;
        if (path.EndsWith(".cga", StringComparison.Ordinal)) return CellModelKind.Animated;
        if (path.EndsWith(".chr", StringComparison.Ordinal)) return CellModelKind.Character;
        if (path.EndsWith(".cdf", StringComparison.Ordinal)) return CellModelKind.CharacterDefinition;
        return CellModelKind.None;
    }
}

internal static class CellXml
{
    public static int Int(XElement e, string name) =>
        int.TryParse((string?)e.Attribute(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;

    public static float Float(XElement e, string name, float def) =>
        float.TryParse((string?)e.Attribute(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : def;

    public static bool Bool(XElement e, string name) => (string?)e.Attribute(name) is "1" or "true";

    public static Vector3 Vec3(XElement e, string name, Vector3 def)
    {
        var s = (string?)e.Attribute(name);
        return s == null ? def : ParseVec3(s, def);
    }

    public static Vector3 ParseVec3(string s, Vector3 def)
    {
        var f = Floats(s);
        return f.Length >= 3 ? new Vector3(f[0], f[1], f[2]) : def;
    }

    /// <summary>CryEngine writes quaternions as "w,x,y,z".</summary>
    public static Quaternion QuatWxyz(XElement e, string name)
    {
        var s = (string?)e.Attribute(name);
        if (s == null)
            return Quaternion.Identity;
        var f = Floats(s);
        return f.Length >= 4 ? Quaternion.Normalize(new Quaternion(f[1], f[2], f[3], f[0])) : Quaternion.Identity;
    }

    private static float[] Floats(string s) =>
        s.Split(',').Select(x => float.TryParse(x.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0f).ToArray();
}
