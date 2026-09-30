using System;
using System.IO;
using System.Text;

namespace AAEmu.GodotViewer;

/// <summary>Header facts of a CryEngine .dds file.</summary>
public sealed class CryDdsInfo
{
    /// <summary>FourCC ("DXT1", "DXT3", "DXT5", "ATI2", ...) or "RGB32"/"RGBA32"-style for uncompressed data.</summary>
    public string Format = "";

    public int Width, Height;

    /// <summary>Mip levels stored (CryEngine stops at 4x4 for block formats, so usually fewer than a full chain).</summary>
    public int MipCount;

    public int FullMipCount;
    public bool IsCubemap;

    /// <summary>CryEngine flag (0x800 at file offset 0x24): stored in sRGB. Set on diffuse/specular/decal maps.</summary>
    public bool IsSrgb;

    /// <summary>CryEngine flag (0x40 at file offset 0x24): set on most normal maps.</summary>
    public bool IsNormalMapHint;

    /// <summary>"FYRC" marker at 0x7C (every sampled ArcheAge texture but one has it).</summary>
    public bool HasCryMarker;

    /// <summary>Bytes of pixel data (all faces, all stored mips) after the 128-byte header.</summary>
    public int DataSize;

    /// <summary>Extension chunks after the pixel data ("CExt" ... "CEnd": AvgC average colour, AttC attached alpha image, Flgs).</summary>
    public string Extensions = "";

    public bool IsBlockCompressed => CryDds.BlockBytes(Format) > 0;
}

/// <summary>
/// CryEngine .dds quirks and the fix-ups Godot needs before <c>Image.LoadDdsFromBuffer</c>.
/// <list type="bullet">
/// <item>Truncated mip chains: CryEngine stops at the 4x4 level, Godot insists on a full chain down to 1x1 and
///   rejects the file ("Expected Image data size ..."). About two thirds of the sampled textures are affected.
///   <see cref="PrepareForGodot"/> appends the missing 2x2/1x1 levels (copies of the last block).</item>
/// <item>ATI2 (3Dc) normal maps store Y in the first BC4 block and X in the second, the reverse of BC5.
///   <see cref="PrepareForGodot"/> swaps the two halves of every block so R = X, G = Y as Godot expects.</item>
/// <item>Normal maps (DXT1 RGB and ATI2) are DirectX style: green follows +v (down the image). With the tangents
///   from <see cref="CgfModelReader"/> no green flip is needed.</item>
/// <item>Trailing "CExt" chunks are tolerated by Godot but are stripped anyway.</item>
/// </list>
/// </summary>
public static class CryDds
{
    public static CryDdsInfo ReadInfo(byte[] dds)
    {
        if (dds == null || dds.Length < 128 || Encoding.ASCII.GetString(dds, 0, 4) != "DDS ")
            throw new InvalidDataException("not a DDS file");
        var info = new CryDdsInfo
        {
            Height = BitConverter.ToInt32(dds, 12),
            Width = BitConverter.ToInt32(dds, 16),
            MipCount = Math.Max(1, BitConverter.ToInt32(dds, 28)),
        };
        var pfFlags = BitConverter.ToUInt32(dds, 80);
        if ((pfFlags & 4) != 0)
            info.Format = Encoding.ASCII.GetString(dds, 84, 4);
        else
            info.Format = ((pfFlags & 1) != 0 ? "RGBA" : "RGB") + BitConverter.ToInt32(dds, 88);
        var caps2 = BitConverter.ToUInt32(dds, 112);
        info.IsCubemap = (caps2 & 0x200) != 0;
        var cryFlags = BitConverter.ToUInt32(dds, 0x24);
        info.IsSrgb = (cryFlags & 0x800) != 0;
        info.IsNormalMapHint = (cryFlags & 0x40) != 0;
        info.HasCryMarker = Encoding.ASCII.GetString(dds, 0x7C, 4) == "FYRC";
        info.FullMipCount = 1 + (int)Math.Floor(Math.Log2(Math.Max(1, Math.Max(info.Width, info.Height))));
        var faces = info.IsCubemap ? 6 : 1;
        info.DataSize = faces * ChainSize(info.Format, BitConverter.ToInt32(dds, 88), info.Width, info.Height, info.MipCount);
        var p = 128 + info.DataSize;
        if (p + 4 <= dds.Length && Encoding.ASCII.GetString(dds, p, 4) == "CExt")
        {
            var sb = new StringBuilder();
            p += 4;
            while (p + 4 <= dds.Length)
            {
                var tag = Encoding.ASCII.GetString(dds, p, 4);
                if (tag == "CEnd" || p + 8 > dds.Length)
                    break;
                var size = BitConverter.ToInt32(dds, p + 4);
                sb.Append(sb.Length > 0 ? "," : "").Append(tag);
                if (size < 0)
                    break;
                p += 8 + size;
            }
            info.Extensions = sb.ToString();
        }
        return info;
    }

    /// <summary>
    /// Returns a copy that Godot's <c>Image.LoadDdsFromBuffer</c> accepts: full mip chain, ATI2 channels in BC5
    /// order (when <paramref name="swapAti2"/>), no trailing CryEngine chunks. Cubemaps are reduced to their first
    /// face (Image holds one 2D surface). Unknown formats are returned unchanged.
    /// </summary>
    public static byte[] PrepareForGodot(byte[] dds, bool swapAti2 = true)
    {
        var info = ReadInfo(dds);
        var rgbBits = BitConverter.ToInt32(dds, 88);
        var block = BlockBytes(info.Format);
        var pixelBytes = block > 0 ? 0 : (info.Format.StartsWith("RGB", StringComparison.Ordinal) ? rgbBits / 8 : 0);
        if (block == 0 && pixelBytes == 0)
            return dds;

        var storedFace = ChainSize(info.Format, rgbBits, info.Width, info.Height, info.MipCount);
        var fullMips = info.FullMipCount;
        var mips = Math.Min(info.MipCount, fullMips);
        var fullFace = ChainSize(info.Format, rgbBits, info.Width, info.Height, fullMips);
        if (128 + storedFace > dds.Length)
            throw new InvalidDataException("DDS pixel data truncated");

        var outBuf = new byte[128 + fullFace];
        Buffer.BlockCopy(dds, 0, outBuf, 0, 128);
        var copied = ChainSize(info.Format, rgbBits, info.Width, info.Height, mips);
        Buffer.BlockCopy(dds, 128, outBuf, 128, copied);

        // Pad the missing small levels with copies of the smallest stored level's first block / pixel.
        var lastLevelOffset = 128 + ChainSize(info.Format, rgbBits, info.Width, info.Height, mips - 1);
        var unit = block > 0 ? block : pixelBytes;
        var p = 128 + copied;
        for (var level = mips; level < fullMips; level++)
        {
            var size = LevelSize(info.Format, rgbBits, Math.Max(1, info.Width >> level), Math.Max(1, info.Height >> level));
            for (var o = 0; o < size; o += unit)
                Buffer.BlockCopy(dds, lastLevelOffset, outBuf, p + o, Math.Min(unit, size - o));
            p += size;
        }

        // Header: mip count, mipmap flags, single 2D surface.
        BitConverter.GetBytes(fullMips).CopyTo(outBuf, 28);
        var flags = BitConverter.ToUInt32(outBuf, 8) | 0x20000; // DDSD_MIPMAPCOUNT
        BitConverter.GetBytes(flags).CopyTo(outBuf, 8);
        var caps = BitConverter.ToUInt32(outBuf, 108) | 0x400008; // COMPLEX | MIPMAP
        BitConverter.GetBytes(caps).CopyTo(outBuf, 108);
        BitConverter.GetBytes(0u).CopyTo(outBuf, 112); // drop cubemap caps

        if (swapAti2 && info.Format == "ATI2")
            for (var o = 128; o + 16 <= outBuf.Length; o += 16)
                for (var k = 0; k < 8; k++)
                    (outBuf[o + k], outBuf[o + 8 + k]) = (outBuf[o + 8 + k], outBuf[o + k]);
        return outBuf;
    }

    public static int BlockBytes(string format) => format switch
    {
        "DXT1" or "ATI1" or "BC4U" or "BC4S" => 8,
        "DXT2" or "DXT3" or "DXT4" or "DXT5" or "ATI2" or "BC5U" or "BC5S" => 16,
        _ => 0,
    };

    private static int LevelSize(string format, int rgbBits, int w, int h)
    {
        var block = BlockBytes(format);
        if (block > 0)
            return ((w + 3) / 4) * ((h + 3) / 4) * block;
        return w * h * Math.Max(1, rgbBits / 8);
    }

    private static int ChainSize(string format, int rgbBits, int w, int h, int mips)
    {
        var total = 0;
        for (var i = 0; i < mips; i++)
            total += LevelSize(format, rgbBits, Math.Max(1, w >> i), Math.Max(1, h >> i));
        return total;
    }
}
