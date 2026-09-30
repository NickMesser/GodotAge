using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text;
using System.Xml;

namespace AAEmu.GodotViewer;

/// <summary>
/// Keys of one bone in a .caf. Rotation and translation are parent-relative (the same space as
/// <see cref="ChrBone.BindLocal"/>), CryEngine axes, metres. Quaternions are System.Numerics (x, y, z, w) and
/// represent the same rotation as CryEngine's Quat; <c>Matrix4x4.CreateFromQuaternion(q) * CreateTranslation(t)</c>
/// is the row-vector local matrix.
/// </summary>
public sealed class CafTrack
{
    /// <summary>CRC32 of the bone name (<see cref="ChrBone.ControllerId"/>).</summary>
    public uint ControllerId;

    /// <summary>Key times in seconds from the clip start (ascending). Empty = the bone keeps its bind rotation.</summary>
    public float[] RotationTimes = [];

    public Quaternion[] Rotations = [];

    /// <summary>Key times in seconds from the clip start. Empty = the bone keeps its bind translation.</summary>
    public float[] PositionTimes = [];

    public Vector3[] Positions = [];

    /// <summary>Source encoding, e.g. "rot5/t2 pos2/t2" (formats: 1 float quat, 5 48-bit, 8 64-bit; times: 0 f32, 1 u16, 2 u8).</summary>
    public string Encoding = "";

    public Quaternion SampleRotation(float t, Quaternion fallback)
    {
        var n = Rotations.Length;
        if (n == 0) return fallback;
        if (n == 1 || t <= RotationTimes[0]) return Rotations[0];
        if (t >= RotationTimes[n - 1]) return Rotations[n - 1];
        var i = Upper(RotationTimes, t);
        var a = RotationTimes[i - 1];
        var span = RotationTimes[i] - a;
        var u = span > 0 ? (t - a) / span : 0;
        return Quaternion.Slerp(Rotations[i - 1], Rotations[i], u); // Slerp takes the short arc
    }

    public Vector3 SamplePosition(float t, Vector3 fallback)
    {
        var n = Positions.Length;
        if (n == 0) return fallback;
        if (n == 1 || t <= PositionTimes[0]) return Positions[0];
        if (t >= PositionTimes[n - 1]) return Positions[n - 1];
        var i = Upper(PositionTimes, t);
        var a = PositionTimes[i - 1];
        var span = PositionTimes[i] - a;
        return Vector3.Lerp(Positions[i - 1], Positions[i], span > 0 ? (t - a) / span : 0);
    }

    /// <summary>First index with times[i] &gt; t (times ascending, t inside the range).</summary>
    private static int Upper(float[] times, float t)
    {
        int lo = 1, hi = times.Length - 1;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (times[mid] > t) hi = mid;
            else lo = mid + 1;
        }
        return lo;
    }
}

/// <summary>AAFC0002 v0x922 motion parameters (partly decoded: speed, distance, end offset of the root).</summary>
public sealed class CafMotion
{
    /// <summary>Root speed in m/s (5.4 for nuian male fist_mo_normal_run_f, 1.8 walk, 0 idle).</summary>
    public float MoveSpeed;

    /// <summary>Root travel over one cycle in metres (3.6 for the run).</summary>
    public float Distance;

    /// <summary>Asset flags as stored (2 on locomotion cycles and idles, 0 on transitions). Meaning unverified.</summary>
    public uint Flags;

    /// <summary>Root displacement from first to last frame (run_f: 0,3.6,0; run_r: 3.6,0,0), metres. Unreliable on some _b clips.</summary>
    public Vector3 EndTranslation;
}

/// <summary>A decoded .caf clip.</summary>
public sealed class CafAnimation
{
    /// <summary>Always 30 in the sampled data (160 ticks per frame at 4800 ticks per second).</summary>
    public float FramesPerSecond = 30;

    /// <summary>GlobalRange from the timing chunk, in frames. Key times are absolute frames inside it.</summary>
    public int StartFrame, EndFrame;

    public float Duration => Math.Max(0, EndFrame - StartFrame) / FramesPerSecond;

    public List<CafTrack> Tracks = [];

    public CafMotion Motion;

    public uint FileVersion;

    public List<string> Warnings = [];

    public CafTrack FindTrack(uint controllerId)
    {
        foreach (var t in Tracks)
            if (t.ControllerId == controllerId)
                return t;
        return null;
    }
}

/// <summary>
/// Reader for .caf animation clips (CryEngine chunk files, type 0xFFFF0001, tables 0x745 or 0x744).
/// <para>
/// Controller chunk CCCC000D v0x829 (103,125 of 103,777 sampled controllers): a 16-byte header {uint32 controllerId,
/// uint16 numRotKeys, uint16 numPosKeys, uint8 rotFormat, rotTimeFormat, posFormat, posKeysInfo, posTimeFormat,
/// tracksAligned, 2 pad}, then rotation keys, rotation times, position keys, and position times only when
/// posKeysInfo = 1 (otherwise positions share the rotation times). tracksAligned was 0 everywhere (no padding).
/// Rotation formats seen: 1 = 4 floats (x,y,z,w); 5 = SmallTree48Bit (3 x uint16 read as one 48-bit little-endian
/// value: components in bits 0-14, 15-29, 30-44, dropped-component index in bits 46-47); 8 = SmallTree64BitExt
/// (uint64: 21, 21, 20 bit components from bit 0, index in bits 62-63). In both packed formats the three stored
/// components fill x, y, z, w in order skipping the dropped index, each decoded as (raw - half) / half * (1/sqrt 2)
/// with half = 2^(bits-1); the dropped one is +sqrt(1 - sum of squares). Position format: 2 = 3 floats (metres).
/// Time formats: 0 float, 1 uint16, 2 uint8, all in frames (absolute, inside the timing chunk's GlobalRange).
/// </para>
/// <para>
/// Old controllers CCCC000D v0x827 (4 machinima files per 1457): uint32 numKeys, uint32 controllerId, then
/// {int32 time in ticks, float3 position in centimetres, float3 log-quaternion} keys. Decoded per CE2
/// (exp map, unverified against a rendered result). v0x828 (a few env props) is not supported.
/// </para>
/// </summary>
public static class CafReader
{
    public const uint ChunkController = 0xCCCC000D, ChunkTiming = 0xCCCC000E, ChunkMotionParams = 0xAAFC0002;

    private const float InvSqrt2 = 0.70710678f;

    public static CafAnimation Read(byte[] file)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (file.Length < 20 || System.Text.Encoding.ASCII.GetString(file, 0, 6) != "CryTek")
            throw new InvalidDataException("not a CryTek chunk file");
        var fileType = CgfModelReader.U32(file, 8);
        var version = CgfModelReader.U32(file, 12);
        if (fileType != 0xFFFF0001)
            throw new InvalidDataException($"not an animation file (type 0x{fileType:X8})");
        var chunks = CgfModelReader.ReadChunkTable(file, version, CgfModelReader.I32(file, 16));
        var anim = new CafAnimation { FileVersion = version };
        var ticksPerFrame = 160;

        foreach (var c in chunks)
        {
            var p = CgfModelReader.Payload(file, c, out var size);
            if (c.Type == ChunkTiming && size >= 48)
            {
                var secsPerTick = CgfModelReader.F32(file, p);
                ticksPerFrame = Math.Max(1, CgfModelReader.I32(file, p + 4));
                if (secsPerTick > 0)
                    anim.FramesPerSecond = 1f / (secsPerTick * ticksPerFrame);
                anim.StartFrame = CgfModelReader.I32(file, p + 40);
                anim.EndFrame = CgfModelReader.I32(file, p + 44);
            }
            else if (c.Type == ChunkMotionParams && c.Version == 0x922 && size >= 28)
                anim.Motion = new CafMotion
                {
                    MoveSpeed = CgfModelReader.F32(file, p),
                    Distance = CgfModelReader.F32(file, p + 4),
                    Flags = CgfModelReader.U32(file, p + 12),
                    EndTranslation = new Vector3(CgfModelReader.F32(file, p + 16), CgfModelReader.F32(file, p + 20), CgfModelReader.F32(file, p + 24)),
                };
        }

        foreach (var c in chunks)
        {
            if (c.Type != ChunkController)
                continue;
            var p = CgfModelReader.Payload(file, c, out var size);
            CafTrack track = c.Version switch
            {
                0x829 => ReadController829(file, p, size, anim),
                0x827 => ReadController827(file, p, size, anim, ticksPerFrame),
                _ => null,
            };
            if (track == null)
            {
                if (c.Version != 0x829 && c.Version != 0x827)
                    anim.Warnings.Add($"controller chunk {c.Id} version 0x{c.Version:X} not supported");
                continue;
            }
            anim.Tracks.Add(track);
        }
        return anim;
    }

    private static CafTrack ReadController829(byte[] f, int p, int size, CafAnimation anim)
    {
        if (size < 16)
            return null;
        var end = p + size;
        var track = new CafTrack { ControllerId = CgfModelReader.U32(f, p) };
        int numRot = BitConverter.ToUInt16(f, p + 4), numPos = BitConverter.ToUInt16(f, p + 6);
        int rotFmt = f[p + 8], rotTimeFmt = f[p + 9], posFmt = f[p + 10], posKeysInfo = f[p + 11], posTimeFmt = f[p + 12];
        track.Encoding = $"rot{rotFmt}/t{rotTimeFmt} pos{posFmt}/t{(posKeysInfo == 1 ? posTimeFmt : rotTimeFmt)}";
        if (f[p + 13] != 0)
        {
            anim.Warnings.Add($"controller {track.ControllerId:X8}: aligned tracks not supported");
            return null;
        }
        var q = p + 16;
        int rotSize = numRot == 0 ? 0 : rotFmt switch { 1 => 16, 5 => 6, 8 => 8, _ => -1 };
        if (rotSize < 0)
        {
            anim.Warnings.Add($"controller {track.ControllerId:X8}: rotation format {rotFmt} not supported");
            return null;
        }
        if (q + (long)numRot * rotSize > end)
            return Truncated(track, anim);
        track.Rotations = new Quaternion[numRot];
        for (var i = 0; i < numRot; i++, q += rotSize)
            track.Rotations[i] = DecodeRotation(f, q, rotFmt);
        MakeContinuous(track.Rotations);
        float[] rotTimes = [];
        if (numRot > 0 && !ReadTimes(f, ref q, end, numRot, rotTimeFmt, anim, out rotTimes))
            return Truncated(track, anim);
        track.RotationTimes = rotTimes;

        if (numPos > 0)
        {
            if (posFmt != 2)
            {
                anim.Warnings.Add($"controller {track.ControllerId:X8}: position format {posFmt} not supported");
                numPos = 0;
            }
            else
            {
                if (q + numPos * 12L > end)
                    return Truncated(track, anim);
                track.Positions = CgfModelReader.ReadVec3(f, q, numPos, 12);
                q += numPos * 12;
                if (posKeysInfo == 1)
                {
                    if (!ReadTimes(f, ref q, end, numPos, posTimeFmt, anim, out var posTimes))
                        return Truncated(track, anim);
                    track.PositionTimes = posTimes;
                }
                else if (rotTimes.Length == numPos)
                    track.PositionTimes = rotTimes;
                else
                {
                    anim.Warnings.Add($"controller {track.ControllerId:X8}: {numPos} positions share {rotTimes.Length} rotation times");
                    track.Positions = [];
                }
            }
        }
        ToSeconds(track.RotationTimes, anim);
        if (!ReferenceEquals(track.PositionTimes, track.RotationTimes))
            ToSeconds(track.PositionTimes, anim);
        return track;
    }

    private static CafTrack ReadController827(byte[] f, int p, int size, CafAnimation anim, int ticksPerFrame)
    {
        if (size < 8)
            return null;
        var n = CgfModelReader.I32(f, p);
        var track = new CafTrack { ControllerId = CgfModelReader.U32(f, p + 4), Encoding = "pqlog827" };
        if (n < 0 || 8 + n * 28L > size)
            return Truncated(track, anim);
        track.RotationTimes = new float[n];
        track.Rotations = new Quaternion[n];
        track.Positions = new Vector3[n];
        for (var i = 0; i < n; i++)
        {
            var k = p + 8 + i * 28;
            track.RotationTimes[i] = CgfModelReader.I32(f, k) / (float)ticksPerFrame;
            track.Positions[i] = new Vector3(CgfModelReader.F32(f, k + 4), CgfModelReader.F32(f, k + 8), CgfModelReader.F32(f, k + 12)) * 0.01f;
            var v = new Vector3(CgfModelReader.F32(f, k + 16), CgfModelReader.F32(f, k + 20), CgfModelReader.F32(f, k + 24));
            var len = v.Length();
            track.Rotations[i] = len > 1e-9f ? new Quaternion(v * (MathF.Sin(len) / len), MathF.Cos(len)) : Quaternion.Identity;
        }
        MakeContinuous(track.Rotations);
        track.PositionTimes = track.RotationTimes;
        ToSeconds(track.RotationTimes, anim);
        return track;
    }

    private static CafTrack Truncated(CafTrack track, CafAnimation anim)
    {
        anim.Warnings.Add($"controller {track.ControllerId:X8}: data truncated");
        return null;
    }

    private static bool ReadTimes(byte[] f, ref int q, int end, int n, int fmt, CafAnimation anim, out float[] times)
    {
        times = new float[n];
        var es = fmt switch { 0 => 4, 1 => 2, 2 => 1, _ => -1 };
        if (es < 0)
        {
            anim.Warnings.Add($"key time format {fmt} not supported");
            return false;
        }
        if (q + (long)n * es > end)
            return false;
        for (var i = 0; i < n; i++, q += es)
            times[i] = fmt switch
            {
                0 => CgfModelReader.F32(f, q),
                1 => BitConverter.ToUInt16(f, q),
                _ => f[q],
            };
        return true;
    }

    /// <summary>Frames (absolute) to seconds from the clip start.</summary>
    private static void ToSeconds(float[] times, CafAnimation anim)
    {
        for (var i = 0; i < times.Length; i++)
            times[i] = (times[i] - anim.StartFrame) / anim.FramesPerSecond;
    }

    /// <summary>Decodes one rotation key (formats 1, 5, 8; see class remarks).</summary>
    public static Quaternion DecodeRotation(byte[] f, int p, int format)
    {
        switch (format)
        {
            case 1:
                return Quaternion.Normalize(new Quaternion(CgfModelReader.F32(f, p), CgfModelReader.F32(f, p + 4),
                    CgfModelReader.F32(f, p + 8), CgfModelReader.F32(f, p + 12)));
            case 5:
            {
                ulong v = (ulong)BitConverter.ToUInt16(f, p) | ((ulong)BitConverter.ToUInt16(f, p + 2) << 16) |
                          ((ulong)BitConverter.ToUInt16(f, p + 4) << 32);
                return SmallestThree((int)(v >> 46) & 3,
                    Unpack(v & 0x7FFF, 15), Unpack((v >> 15) & 0x7FFF, 15), Unpack((v >> 30) & 0x7FFF, 15));
            }
            case 8:
            {
                var v = BitConverter.ToUInt64(f, p);
                return SmallestThree((int)(v >> 62),
                    Unpack(v & 0x1FFFFF, 21), Unpack((v >> 21) & 0x1FFFFF, 21), Unpack((v >> 42) & 0xFFFFF, 20));
            }
            default:
                throw new NotSupportedException($"rotation format {format}");
        }
    }

    private static float Unpack(ulong raw, int bits)
    {
        var half = (float)(1UL << (bits - 1));
        return (raw - half) / half * InvSqrt2;
    }

    private static Quaternion SmallestThree(int dropped, float a, float b, float c)
    {
        Span<float> q = stackalloc float[4];
        Span<float> s = [a, b, c];
        var k = 0;
        for (var i = 0; i < 4; i++)
            if (i != dropped)
                q[i] = s[k++];
        q[dropped] = MathF.Sqrt(MathF.Max(0, 1 - (a * a + b * b + c * c)));
        return Quaternion.Normalize(new Quaternion(q[0], q[1], q[2], q[3]));
    }

    /// <summary>Flips signs so consecutive keys lie in the same hemisphere (cleaner linear blends).</summary>
    private static void MakeContinuous(Quaternion[] keys)
    {
        for (var i = 1; i < keys.Length; i++)
            if (Quaternion.Dot(keys[i - 1], keys[i]) < 0)
                keys[i] = Quaternion.Negate(keys[i]);
    }
}

/// <summary>Plays a <see cref="CafAnimation"/> on a <see cref="ChrSkeleton"/>.</summary>
public sealed class AnimationSampler
{
    public readonly ChrSkeleton Skeleton;
    public readonly CafAnimation Clip;

    /// <summary>Per bone of <see cref="Skeleton"/>: its track, or null (the bone keeps its bind-local transform).</summary>
    public readonly CafTrack[] BoneTracks;

    /// <summary>Tracks whose controller id matches no bone (other skeleton, helper bones).</summary>
    public readonly int UnmatchedTracks;

    public AnimationSampler(ChrSkeleton skeleton, CafAnimation clip)
    {
        Skeleton = skeleton;
        Clip = clip;
        BoneTracks = new CafTrack[skeleton.Count];
        foreach (var t in clip.Tracks)
        {
            var i = skeleton.IndexOfController(t.ControllerId);
            if (i >= 0)
                BoneTracks[i] = t;
            else
                UnmatchedTracks++;
        }
    }

    /// <summary>
    /// Parent-relative bone transforms at <paramref name="time"/> seconds (wrapped when <paramref name="loop"/>).
    /// Animated bones take the track's rotation and translation (a missing channel falls back to the bind-local one).
    /// With <paramref name="inPlace"/> the root bone's horizontal (X, Y) travel is removed: locomotion clips move
    /// Bip01 along the ground (the nuian run covers 3.6 m per cycle) and keep the pelvis height on Bip01 Pelvis.
    /// </summary>
    public Matrix4x4[] SampleLocal(float time, bool loop = true, bool inPlace = true)
    {
        var d = Clip.Duration;
        if (loop && d > 0)
        {
            time %= d;
            if (time < 0) time += d;
        }
        var local = new Matrix4x4[Skeleton.Count];
        for (var i = 0; i < local.Length; i++)
        {
            var bone = Skeleton.Bones[i];
            var track = BoneTracks[i];
            if (track == null)
            {
                local[i] = bone.BindLocal;
                continue;
            }
            Matrix4x4.Decompose(bone.BindLocal, out _, out var bindRot, out var bindPos);
            var r = track.SampleRotation(time, bindRot);
            var t = track.SamplePosition(time, bindPos);
            if (inPlace && bone.ParentIndex < 0)
                t = new Vector3(0, 0, t.Z);
            local[i] = Matrix4x4.CreateFromQuaternion(r) * Matrix4x4.CreateTranslation(t);
        }
        return local;
    }

    public Matrix4x4[] SampleWorld(float time, bool loop = true, bool inPlace = true) =>
        Skeleton.LocalToWorld(SampleLocal(time, loop, inPlace));
}

/// <summary>A parsed .cal animation list (with includes resolved).</summary>
public sealed class CalFile
{
    /// <summary>Clip name (lower case) to pak path: .caf, .lmg (names starting with '_' are locomotion groups), .fsq (facial), .anm.</summary>
    public Dictionary<string, string> Animations = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>"#"/"$" directives of the top file and its includes, e.g. "$AnimEventDatabase", "#BaseModelHeight", "$facelib".</summary>
    public Dictionary<string, string> Directives = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Includes = [];
    public List<string> Warnings = [];
    public int Duplicates;

    public string Find(string name) => Animations.TryGetValue(name, out var p) ? p : null;
}

/// <summary>
/// Reader for .cal files: the per-skeleton animation list, stored next to the base .chr with the same name
/// (objects/characters/nuian/male/nude/nu_m_base.chr -> nu_m_base.cal; monster/fox/fox.chr -> fox.cal).
/// Syntax: <c>name = relative\path.caf</c> relative to the current <c>#filepath</c>; <c>$Include = file.cal</c>
/// (game-root path, inherits the current #filepath, may set its own); comments <c>//</c> and lines starting with
/// <c>--</c>; other <c>#key = value</c> / <c>$key = value</c> lines are directives. The first definition of a name wins.
/// </summary>
public static class CalReader
{
    public static CalFile Load(string calPakPath, Func<string, byte[]> read)
    {
        var cal = new CalFile();
        Parse(CdfReader.PakPath(calPakPath), "", read, cal, 0);
        return cal;
    }

    public static CalFile Parse(string text, Func<string, byte[]> read = null)
    {
        var cal = new CalFile();
        ParseText(text, "", read, cal, 0);
        return cal;
    }

    private static void Parse(string path, string filepath, Func<string, byte[]> read, CalFile cal, int depth)
    {
        var data = read(path);
        if (data == null)
        {
            cal.Warnings.Add($"missing {path}");
            return;
        }
        ParseText(System.Text.Encoding.UTF8.GetString(data), filepath, read, cal, depth);
    }

    private static void ParseText(string text, string filepath, Func<string, byte[]> read, CalFile cal, int depth)
    {
        foreach (var raw in text.Split('\n'))
        {
            var line = raw;
            var comment = line.IndexOf("//", StringComparison.Ordinal);
            if (comment >= 0)
                line = line[..comment];
            line = line.Trim();
            if (line.Length == 0 || line.StartsWith("--", StringComparison.Ordinal))
                continue;
            var eq = line.IndexOf('=');
            if (eq <= 0)
                continue;
            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();
            if (key.Equals("#filepath", StringComparison.OrdinalIgnoreCase))
            {
                filepath = value.Replace('\\', '/').Trim('/');
                continue;
            }
            if (key.Equals("$Include", StringComparison.OrdinalIgnoreCase))
            {
                var inc = CdfReader.PakPath(value);
                cal.Includes.Add(inc);
                if (read != null && depth < 8)
                    Parse(inc, filepath, read, cal, depth + 1);
                continue;
            }
            if (key.StartsWith('#') || key.StartsWith('$'))
            {
                cal.Directives.TryAdd(key, value);
                continue;
            }
            var rel = value.Replace('\\', '/').TrimStart('/');
            var full = CdfReader.PakPath(filepath.Length > 0 ? filepath + "/" + rel : rel);
            if (!cal.Animations.TryAdd(key, full))
                cal.Duplicates++;
        }
    }
}

/// <summary>A locomotion group (.lmg): blend-space examples by parameter position.</summary>
public sealed class LmgFile
{
    /// <summary>BLENDTYPE type, e.g. "XL15" (8 directions x speed levels).</summary>
    public string BlendType = "";

    public string Caps = "";

    /// <summary>(clip name, blend position). Position is (strafe X, forward Y, slope/speed Z); (0,1,0) = straight ahead.</summary>
    public List<(string Name, Vector3 Position)> Examples = [];

    /// <summary>First example at (0, 1, 0): the forward cycle (e.g. fist_mo_normal_run_f), or the first example.</summary>
    public string ForwardClip
    {
        get
        {
            foreach (var (name, pos) in Examples)
                if (Vector3.DistanceSquared(pos, new Vector3(0, 1, 0)) < 1e-4f)
                    return name;
            return Examples.Count > 0 ? Examples[0].Name : null;
        }
    }
}

public static class LmgReader
{
    public static LmgFile Parse(byte[] data)
    {
        using var ms = new MemoryStream(data);
        var doc = new XmlDocument();
        doc.Load(ms);
        var root = doc.DocumentElement ?? throw new InvalidDataException("empty .lmg");
        var lmg = new LmgFile();
        if (root["BLENDTYPE"] is { } bt) lmg.BlendType = bt.GetAttribute("type");
        if (root["CAPS"] is { } caps) lmg.Caps = caps.GetAttribute("code");
        if (root["ExampleList"] is { } list)
            foreach (XmlNode n in list.ChildNodes)
                if (n is XmlElement e && e.Name == "Example")
                {
                    var p = e.GetAttribute("Position").Split(',');
                    var v = Vector3.Zero;
                    if (p.Length >= 3)
                        v = new Vector3(F(p[0]), F(p[1]), F(p[2]));
                    lmg.Examples.Add((e.GetAttribute("AName"), v));
                }
        return lmg;
    }

    private static float F(string s) => float.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
}
