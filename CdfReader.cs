using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text;
using System.Xml;

namespace AAEmu.GodotViewer;

/// <summary>One &lt;Attachment&gt; of a character definition.</summary>
public sealed class CdfAttachment
{
    /// <summary>
    /// Slot name (AName). Player/NPC humanoid CDFs use: body, face, hair, head, chest, waist, legs, hands, feet, arms,
    /// glasses, beard, cosplay, cosplaylooks, race_cosplay, race_cosplaylooks (skins) and mainhand, offhand, ranged,
    /// musical, craft_mainhand, craft_offhand, back, backpack (bones). Mount CDFs add bone_spine_driver/passenger.
    /// </summary>
    public string Name = "";

    /// <summary>"CA_SKIN" (skinned mesh driven by the master skeleton) or "CA_BONE" (rigid mesh on one bone).</summary>
    public string Type = "";

    public bool IsSkin => Type.Equals("CA_SKIN", StringComparison.OrdinalIgnoreCase);
    public bool IsBone => Type.Equals("CA_BONE", StringComparison.OrdinalIgnoreCase);

    /// <summary>Binding exactly as written (file path, empty, or an effect name when <see cref="AttachmentType"/> is "effect").</summary>
    public string Binding = "";

    /// <summary>Normalised pak path of <see cref="Binding"/> ("game/..." lower case), empty when unbound or an effect.</summary>
    public string BindingPath = "";

    /// <summary>Material override (normalised pak path of a .mtl), empty when the model's own material applies.</summary>
    public string MaterialPath = "";

    /// <summary>CA_BONE: the bone the attachment follows (e.g. item_hand_r, item_back_c).</summary>
    public string BoneName = "";

    /// <summary>"effect" for particle attachments (Binding is an effect library name), otherwise empty.</summary>
    public string AttachmentType = "";

    /// <summary>
    /// Flags; bit 0 (value 1) = hidden (CE3 FLAGS_ATTACH_HIDE_ATTACHMENT). The empty equipment slots of a base CDF
    /// are written with Flags="1" and are filled at runtime.
    /// </summary>
    public int Flags;

    public bool IsHidden => (Flags & 1) != 0;

    /// <summary>
    /// CA_BONE default placement in character model space (bind pose), CryEngine axes. Rotation is written "w,x,y,z";
    /// here it is a System.Numerics quaternion (x, y, z, w). The attachment's offset relative to its bone is
    /// <c>AbsoluteTransform * bone.InverseBindWorld</c>; when absent the offset is identity (item bones such as
    /// item_hand_r are already the grip frame).
    /// </summary>
    public Quaternion Rotation = Quaternion.Identity;

    public Vector3 Position;
    public bool HasPlacement;

    /// <summary>LRotation/LPosition as written (27 sampled files, always identity/zero). Kept for completeness.</summary>
    public Quaternion LocalRotation = Quaternion.Identity;

    public Vector3 LocalPosition;

    /// <summary>Uniform scale ("scale" attribute), default 1.</summary>
    public float Scale = 1;

    public string AlignBoneAttachment = "";
    public int HingeIdx = -1;
    public float HingeLimit, HingeDamping;

    /// <summary>Model-space placement matrix (row-vector convention).</summary>
    public Matrix4x4 AbsoluteTransform =>
        Matrix4x4.CreateScale(Scale) * Matrix4x4.CreateFromQuaternion(Rotation) * Matrix4x4.CreateTranslation(Position);
}

/// <summary>A parsed .cdf (CryEngine character definition).</summary>
public sealed class CdfFile
{
    /// <summary>Model File attribute as written.</summary>
    public string ModelFile = "";

    /// <summary>Normalised pak path of the base model (a .chr with the skeleton; ArcheAge always uses .chr).</summary>
    public string ModelPath = "";

    /// <summary>Material override for the base model (normalised .mtl path) or empty.</summary>
    public string ModelMaterialPath = "";

    public List<CdfAttachment> Attachments = [];

    /// <summary>AttachmentList customize attribute (6 on humanoid bases, 9 on mounts); meaning unknown.</summary>
    public int Customize = -1;

    /// <summary>ShapeDeformation COL0..COL7 (all zero in every sampled file).</summary>
    public float[] ShapeDeformation = [];

    public CdfAttachment Find(string name)
    {
        foreach (var a in Attachments)
            if (a.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                return a;
        return null;
    }
}

/// <summary>
/// Reader for ArcheAge .cdf files (plain XML). Verified on 957 sampled files: every Model File is a .chr; 910 have
/// no attachments (a .chr + material pairing), the rest use CA_SKIN / CA_BONE attachments. Paths appear with and
/// without "game/", with backslashes and mixed case; they are normalised like <see cref="MtlReader.TexturePakPath"/>
/// but keep their extension.
/// </summary>
public static class CdfReader
{
    public static CdfFile Parse(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        using var ms = new MemoryStream(data);
        var doc = new XmlDocument();
        doc.Load(ms);
        return Parse(doc);
    }

    public static CdfFile Parse(string xml)
    {
        var doc = new XmlDocument();
        doc.LoadXml(xml);
        return Parse(doc);
    }

    private static CdfFile Parse(XmlDocument doc)
    {
        var root = doc.DocumentElement;
        if (root == null || root.Name != "CharacterDefinition")
            throw new InvalidDataException("root element is not <CharacterDefinition>");
        var cdf = new CdfFile();
        if (root["Model"] is { } model)
        {
            cdf.ModelFile = model.GetAttribute("File");
            cdf.ModelPath = PakPath(cdf.ModelFile);
            cdf.ModelMaterialPath = PakPath(model.GetAttribute("Material"));
        }
        if (root["AttachmentList"] is { } list)
        {
            if (int.TryParse(list.GetAttribute("customize"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var cz))
                cdf.Customize = cz;
            foreach (XmlNode n in list.ChildNodes)
            {
                if (n is not XmlElement e || e.Name != "Attachment")
                    continue;
                var a = new CdfAttachment
                {
                    Name = e.GetAttribute("AName"),
                    Type = e.GetAttribute("Type"),
                    Binding = e.GetAttribute("Binding"),
                    BoneName = e.GetAttribute("BoneName"),
                    AttachmentType = e.GetAttribute("AttachmentType"),
                    AlignBoneAttachment = e.GetAttribute("AlignBoneAttachment"),
                    Flags = (int)ParseFloat(e.GetAttribute("Flags"), 0),
                    Scale = ParseFloat(e.GetAttribute("scale"), 1),
                    HingeIdx = (int)ParseFloat(e.GetAttribute("HingeIdx"), -1),
                    HingeLimit = ParseFloat(e.GetAttribute("HingeLimit"), 0),
                    HingeDamping = ParseFloat(e.GetAttribute("HingeDamping"), 0),
                };
                if (!a.AttachmentType.Equals("effect", StringComparison.OrdinalIgnoreCase))
                    a.BindingPath = PakPath(a.Binding);
                a.MaterialPath = PakPath(e.GetAttribute("Material"));
                if (e.HasAttribute("Rotation") || e.HasAttribute("Position"))
                {
                    a.HasPlacement = true;
                    a.Rotation = ParseQuatWxyz(e.GetAttribute("Rotation"));
                    a.Position = ParseVec3(e.GetAttribute("Position"));
                }
                a.LocalRotation = ParseQuatWxyz(e.GetAttribute("LRotation"));
                a.LocalPosition = ParseVec3(e.GetAttribute("LPosition"));
                cdf.Attachments.Add(a);
            }
        }
        if (root["ShapeDeformation"] is { } shape)
        {
            var cols = new List<float>();
            for (var i = 0; shape.HasAttribute("COL" + i); i++)
                cols.Add(ParseFloat(shape.GetAttribute("COL" + i), 0));
            cdf.ShapeDeformation = cols.ToArray();
        }
        return cdf;
    }

    /// <summary>
    /// Pak path for a model/material reference in a .cdf, a .cal or the database: lower case, forward slashes,
    /// "game/" prefix; the extension is kept. Empty input gives "".
    /// </summary>
    public static string PakPath(string file)
    {
        if (string.IsNullOrWhiteSpace(file))
            return "";
        var p = file.Trim().Replace('\\', '/').TrimStart('/').ToLowerInvariant();
        while (p.Contains("//", StringComparison.Ordinal))
            p = p.Replace("//", "/", StringComparison.Ordinal);
        if (p.StartsWith("./", StringComparison.Ordinal))
            p = p[2..];
        return p.StartsWith("game/", StringComparison.Ordinal) ? p : "game/" + p;
    }

    private static float ParseFloat(string s, float fallback) =>
        float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    private static Vector3 ParseVec3(string s)
    {
        if (string.IsNullOrEmpty(s))
            return Vector3.Zero;
        var p = s.Split(',');
        return p.Length < 3 ? Vector3.Zero : new Vector3(ParseFloat(p[0], 0), ParseFloat(p[1], 0), ParseFloat(p[2], 0));
    }

    /// <summary>CryEngine writes quaternions as "w,x,y,z".</summary>
    private static Quaternion ParseQuatWxyz(string s)
    {
        if (string.IsNullOrEmpty(s))
            return Quaternion.Identity;
        var p = s.Split(',');
        if (p.Length < 4)
            return Quaternion.Identity;
        var q = new Quaternion(ParseFloat(p[1], 0), ParseFloat(p[2], 0), ParseFloat(p[3], 0), ParseFloat(p[0], 1));
        return q.LengthSquared() > 1e-12f ? Quaternion.Normalize(q) : Quaternion.Identity;
    }
}
