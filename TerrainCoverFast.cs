using Godot;

namespace AAEmu.GodotViewer;

/// <summary>
/// Builds a cell's cover texture (cover.ctc) as a mipmapped DXT1 image without decoding it: the file's tiles are already
/// DXT1, so the blocks of the chosen quadtree level are copied into place and the coarser levels become the mipmaps
/// (1024, 512, 256, 128 px straight from the file; only the last mips below 128 px are encoded here).
/// <para>
/// Orientation: image rows run along cell-local X (east) and columns along Y (north), the order the tiles are stored in
/// and the [x, y] order of <see cref="TerrainCellReader"/>. For heightmap sample [ux, uy] the UV is (uy / 512, ux / 512).
/// Files the fast path can't take (partial quadtrees, other formats, brightness != 1) are decoded with
/// <see cref="TerrainTextureReader"/> and transposed into the same orientation.
/// </para>
/// </summary>
internal static class TerrainCoverFast
{
    private const int TilePixels = 128;
    private const int TileBlocks = TilePixels / 4;
    private const int BlockBytes = 8;
    private const ushort FormatDxt1 = 22;

    /// <summary>Worker thread. The cover image with its top mip at quadtree <paramref name="level"/> (4 = 2048 px), or null.</summary>
    public static Image Build(byte[] ctc, int level)
    {
        return TryAssemble(ctc, level) ?? Fallback(ctc, level);
    }

    private static Image TryAssemble(byte[] f, int level)
    {
        if (f.Length < 32 || f[0] != 'C' || f[1] != 'R' || f[2] != 'Y')
            return null;
        int layerCount = BitConverter.ToUInt16(f, 8);
        var brightness = BitConverter.ToSingle(f, 12);
        if (layerCount < 1 || brightness != 1f)
            return null;
        int tilePixels = BitConverter.ToUInt16(f, 16);
        var format = BitConverter.ToUInt32(f, 20);
        var tileBytes = BitConverter.ToInt32(f, 24);
        var recordBytes = 0;
        for (var l = 0; l < layerCount; l++)
            recordBytes += BitConverter.ToInt32(f, 16 + l * 12 + 8);
        if (tilePixels != TilePixels || format != FormatDxt1 || tileBytes != TileBlocks * TileBlocks * BlockBytes)
            return null;

        var tableOffset = 16 + layerCount * 12;
        int nodeCount = BitConverter.ToUInt16(f, tableOffset);
        var dataStart = tableOffset + 2 + nodeCount * 2;

        // Texture index of every quadtree node per level: depth-first, child c covers east when c & 1, north when c & 2.
        var tiles = new Dictionary<(int X, int Y), int>[level + 1];
        for (var l = 0; l <= level; l++)
            tiles[l] = [];
        var cursor = 0;
        void Walk(int depth, int x, int y)
        {
            var index = BitConverter.ToInt16(f, tableOffset + 2 + cursor * 2);
            cursor++;
            if (index < 0)
                return;
            if (depth <= level)
                tiles[depth][(x, y)] = index;
            for (var c = 0; c < 4; c++)
                Walk(depth + 1, x * 2 + (c & 1), y * 2 + ((c >> 1) & 1));
        }
        Walk(0, 0, 0);
        for (var l = 0; l <= level; l++)
            if (tiles[l].Count != 1 << (2 * l))
                return null; // partial tree: let the decoder fill the gaps from parents

        var size = TilePixels << level;
        // Godot decides how far a DXT1 mip chain goes (4x4 or 1x1): take its expected size from an empty image.
        var data = new byte[ExpectedBytes(size)];
        var offset = 0;
        for (var l = level; l >= 0; l--)
        {
            var levelSize = TilePixels << l;
            var blocksPerRow = levelSize / 4;
            foreach (var ((tx, ty), index) in tiles[l])
            {
                var src = dataStart + index * recordBytes;
                for (var br = 0; br < TileBlocks; br++)
                    Buffer.BlockCopy(f, src + br * TileBlocks * BlockBytes, data,
                        offset + ((tx * TileBlocks + br) * blocksPerRow + ty * TileBlocks) * BlockBytes, TileBlocks * BlockBytes);
            }
            offset += blocksPerRow * blocksPerRow * BlockBytes;
        }

        // Below 128 px: decode the root tile, box-filter it down and encode each level.
        var root = new byte[TilePixels * TilePixels * 4];
        TerrainTextureReader.DecodeBc1(f.AsSpan(dataStart + tiles[0][(0, 0)] * recordBytes, tileBytes), TilePixels, TilePixels, root);
        var rgba = root;
        for (var s = TilePixels / 2; s >= 1 && offset < data.Length; s /= 2)
        {
            rgba = Downsample(rgba, s * 2);
            var encoded = EncodeDxt1(rgba, s);
            Buffer.BlockCopy(encoded, 0, data, offset, Math.Min(encoded.Length, data.Length - offset));
            offset += encoded.Length;
        }
        return Image.CreateFromData(size, size, true, Image.Format.Dxt1, data);
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, int> ExpectedSizes = new();

    private static int ExpectedBytes(int size) =>
        ExpectedSizes.GetOrAdd(size, s => Image.CreateEmpty(s, s, true, Image.Format.Dxt1).GetData().Length);

    private static byte[] Downsample(byte[] src, int srcSize)
    {
        var size = srcSize / 2;
        var dst = new byte[size * size * 4];
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        for (var c = 0; c < 4; c++)
        {
            var sum = src[((2 * y) * srcSize + 2 * x) * 4 + c] + src[((2 * y) * srcSize + 2 * x + 1) * 4 + c]
                      + src[((2 * y + 1) * srcSize + 2 * x) * 4 + c] + src[((2 * y + 1) * srcSize + 2 * x + 1) * 4 + c];
            dst[(y * size + x) * 4 + c] = (byte)((sum + 2) / 4);
        }
        return dst;
    }

    /// <summary>A plain DXT1 encoder for the small mips: per block, the darkest and brightest colours as endpoints.</summary>
    private static byte[] EncodeDxt1(byte[] rgba, int size)
    {
        var blocks = Math.Max(1, size / 4);
        var output = new byte[blocks * blocks * BlockBytes];
        var block = new (int R, int G, int B)[16];
        for (var by = 0; by < blocks; by++)
        for (var bx = 0; bx < blocks; bx++)
        {
            for (var i = 0; i < 16; i++)
            {
                var x = Math.Min(bx * 4 + (i & 3), size - 1);
                var y = Math.Min(by * 4 + (i >> 2), size - 1);
                var p = (y * size + x) * 4;
                block[i] = (rgba[p], rgba[p + 1], rgba[p + 2]);
            }
            var lo = block.MinBy(c => c.R * 3 + c.G * 6 + c.B);
            var hi = block.MaxBy(c => c.R * 3 + c.G * 6 + c.B);
            var c0 = To565(hi);
            var c1 = To565(lo);
            if (c0 < c1)
                (c0, c1) = (c1, c0);
            var o = (by * blocks + bx) * BlockBytes;
            BitConverter.GetBytes(c0).CopyTo(output, o);
            BitConverter.GetBytes(c1).CopyTo(output, o + 2);
            if (c0 == c1)
                continue; // all indices 0
            var p0 = From565(c0);
            var p1 = From565(c1);
            var palette = new[]
            {
                p0, p1,
                ((2 * p0.R + p1.R) / 3, (2 * p0.G + p1.G) / 3, (2 * p0.B + p1.B) / 3),
                ((p0.R + 2 * p1.R) / 3, (p0.G + 2 * p1.G) / 3, (p0.B + 2 * p1.B) / 3),
            };
            uint indices = 0;
            for (var i = 0; i < 16; i++)
            {
                var best = 0;
                var bestDistance = int.MaxValue;
                for (var k = 0; k < 4; k++)
                {
                    int dr = block[i].R - palette[k].Item1, dg = block[i].G - palette[k].Item2, db = block[i].B - palette[k].Item3;
                    var distance = dr * dr + dg * dg + db * db;
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        best = k;
                    }
                }
                indices |= (uint)best << (i * 2);
            }
            BitConverter.GetBytes(indices).CopyTo(output, o + 4);
        }
        return output;
    }

    private static ushort To565((int R, int G, int B) c) => (ushort)(((c.R >> 3) << 11) | ((c.G >> 2) << 5) | (c.B >> 3));

    private static (int R, int G, int B) From565(ushort c) =>
        (((c >> 11) & 31) * 255 / 31, ((c >> 5) & 63) * 255 / 63, (c & 31) * 255 / 31);

    /// <summary>The general decoder, transposed from its map orientation (row 0 north, column 0 west) into ours.</summary>
    private static Image Fallback(byte[] ctc, int level)
    {
        var map = TerrainTextureReader.ReadCellRgba(ctc, level, out var size);
        if (map == null)
            return null;
        var rgba = new byte[map.Length];
        for (var row = 0; row < size; row++) // row = X
        for (var col = 0; col < size; col++) // col = Y
            Buffer.BlockCopy(map, ((size - 1 - col) * size + row) * 4, rgba, (row * size + col) * 4, 4);
        var image = Image.CreateFromData(size, size, false, Image.Format.Rgba8, rgba);
        image.GenerateMipmaps();
        image.Compress(Image.CompressMode.S3Tc, Image.CompressSource.Srgb);
        return image;
    }
}
