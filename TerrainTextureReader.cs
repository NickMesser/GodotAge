#nullable enable

namespace AAEmu.GodotViewer;

/// <summary>
/// Reads a cell's <c>client/terrain/cover.ctc</c>, the baked terrain colour texture, into one RGBA8 image for the
/// whole 1024 m cell. No Godot dependency.
/// <para>
/// File layout (all little-endian), checked against all 1625 files in the 10.0.2.13 pak:
/// <code>
///   0  char[4] "CRY\0"
///   4  u8  file type (0x64), u8 flags (0), u16 version (8)
///   8  u16 layer count (1 or 2), u16 flags, f32 brightness multiplier (always 1.0)
///  16  layer count x { u16 tile size in pixels (128), u16 unused (garbage), u32 texture format, u32 bytes per tile }
///      u16 node count, then that many s16 texture indices, one per quadtree node, -1 for none
///      texture data: record i starts at (end of index table) + i * (sum of all layers' bytes per tile), and holds
///      every layer's tile back to back, layer 0 first.
/// </code>
/// The index table is the terrain quadtree in depth-first order (node, then its four children). A node with a texture
/// always has four children in the table; a node with -1 has none. So the node count is always 1 + 4 x textures, and
/// the texture indices run 0, 1, 2 ... in table order. Level 0 is the root (the whole cell), level n has nodes of
/// 1024 / 2^n metres. Land cells have textures down to level 4 (64 m sectors, 341 textures); flat open-ocean cells
/// mostly stop at level 2 or 1, some cells leave whole branches empty (the parent's texture then covers them), and
/// 24 instance cells have no texture at all. Child c of a node covers the east half when (c &amp; 1) == 1 and the
/// north half when (c &amp; 2) == 2, the same order as heightmap.dat.
/// </para>
/// <para>
/// Formats: layer 0 is the diffuse colour. Format 22 is DXT1 (BC1; every main_world cell), 24 is DXT5 (BC3; 89
/// instance/intro/login cells), 23 would be DXT3 (BC2). The 16 instance_black_thorn cells declare format 7 with
/// 32768-byte tiles, but those tiles hold a 128 x 128 DXT1 image in the first 8192 bytes followed by zeros; this
/// reader decodes them as DXT1. Layer 1 (2-layer DXT5 files) looks like a normal map and is ignored; in the three
/// files with flags bit 0 set, layer 0 alpha holds shading data rather than transparency. Colours are plain RGB.
/// </para>
/// <para>
/// Inside a tile, stored pixel rows run along +X (east) and columns along +Y (north): tile byte order is indexed
/// [x, y] like the <see cref="TerrainCellReader"/> height grid. Tiles abut without overlap (texel centres at
/// (i + 0.5) x tileMetres / 128), and the encoder made each tile's outermost texel rows match the neighbouring tile's,
/// including across cell borders, so plain concatenation is seamless.
/// </para>
/// </summary>
internal static class TerrainTextureReader
{
    public const float CellMeters = 1024f;

    /// <summary>Deepest level <see cref="ReadCellRgba"/> accepts (32 m nodes, 4096 px at 128 px tiles).</summary>
    public const int MaxLevel = 5;

    private const ushort FormatDxt1 = 22;
    private const ushort FormatDxt3 = 23;
    private const ushort FormatDxt5 = 24;

    public sealed record LayerInfo(int TilePixels, uint Format, int TileBytes);

    public sealed record CoverInfo(
        int Version,
        int Flags,
        float BrightnessMultiplier,
        IReadOnlyList<LayerInfo> Layers,
        int NodeCount,
        int TextureCount,
        int DeepestLevel,
        IReadOnlyList<int> TexturesPerLevel);

    /// <summary>
    /// Decodes the cell's colour texture at quadtree <paramref name="level"/> (0..<see cref="MaxLevel"/>). The result
    /// is <paramref name="size"/> x <paramref name="size"/> RGBA8, row-major, 4 bytes per pixel, with
    /// size = tile pixels x 2^level (128, 256, 512, 1024, 2048 at levels 0..4).
    /// <para>
    /// Pixel-to-world mapping, north up as on a map: column i covers cell-local X in [i, i + 1] x m and row j covers
    /// cell-local Y in [1024 - (j + 1) x m, 1024 - j x m], where m = 1024 / size metres. Row 0 is the north edge
    /// (Y = 1024), column 0 the west edge (X = 0). So the UV for cell-local CryEngine (X, Y) is
    /// u = X / 1024, v = 1 - Y / 1024; for heightmap sample [ux, uy] it is (ux / 512, 1 - uy / 512). With
    /// <c>CryAxes</c> (Cry (x, y, z) shown at Godot (x, z, -y)) and the cell's south-west corner at Cry (x0, y0),
    /// u = (godotX - x0) / 1024 and v = 1 + (godotZ + y0) / 1024.
    /// </para>
    /// <para>
    /// Areas without a texture at the requested level are filled from the nearest ancestor, upscaled bilinearly.
    /// Returns null (size 0) when the file has no texture at all (an empty root). Alpha is 255 unless
    /// <paramref name="keepAlpha"/> is set; layer 0 alpha is not transparency (DXT5 files store 254, or shading data
    /// when flag bit 0 is set). The brightness multiplier is applied to RGB (every file in the pak uses 1.0).
    /// </para>
    /// </summary>
    public static byte[]? ReadCellRgba(byte[] ctcFile, int level, out int size, bool keepAlpha = false)
    {
        ArgumentNullException.ThrowIfNull(ctcFile);
        if (level is < 0 or > MaxLevel)
            throw new ArgumentOutOfRangeException(nameof(level), level, $"level must be 0..{MaxLevel}");

        var file = Parse(ctcFile);
        size = 0;
        if (file.Root.TextureIndex < 0)
            return null;

        var px = file.TilePixels;
        if ((long)(px << level) * (px << level) * 4 > Array.MaxLength)
            throw new NotSupportedException($"{px} px tiles at level {level} make an image too large for one array");
        size = px << level;
        var output = new byte[size * size * 4];
        var ctx = new AssembleContext(file, ctcFile, level, size, output);
        ctx.Fill(file.Root, 0, 0, 0);

        var brightness = file.Brightness;
        if (brightness is > 0f and not 1f)
        {
            for (var i = 0; i < output.Length; i += 4)
            {
                output[i] = ScaleByte(output[i], brightness);
                output[i + 1] = ScaleByte(output[i + 1], brightness);
                output[i + 2] = ScaleByte(output[i + 2], brightness);
            }
        }
        if (!keepAlpha)
        {
            for (var i = 3; i < output.Length; i += 4)
                output[i] = 255;
        }
        return output;
    }

    /// <summary>Header and quadtree summary, without decoding any pixels.</summary>
    public static CoverInfo ReadInfo(byte[] ctcFile)
    {
        ArgumentNullException.ThrowIfNull(ctcFile);
        var file = Parse(ctcFile);
        var perLevel = new List<int>();
        CountLevels(file.Root, 0, perLevel);
        return new CoverInfo(file.Version, file.Flags, file.Brightness, file.Layers, file.NodeCount, file.TextureCount,
            perLevel.Count - 1, perLevel);
    }

    private static void CountLevels(Node node, int level, List<int> perLevel)
    {
        if (node.TextureIndex < 0)
            return;
        while (perLevel.Count <= level)
            perLevel.Add(0);
        perLevel[level]++;
        foreach (var child in node.Children!)
            CountLevels(child, level + 1, perLevel);
    }

    private static byte ScaleByte(byte value, float scale) => (byte)Math.Clamp((int)(value * scale + 0.5f), 0, 255);

    // ---------------------------------------------------------------- parsing

    private sealed class Node
    {
        public int TextureIndex;
        public Node[]? Children;
    }

    private sealed record ParsedFile(
        int Version,
        int Flags,
        float Brightness,
        IReadOnlyList<LayerInfo> Layers,
        int NodeCount,
        int TextureCount,
        Node Root,
        int DataStart,
        int RecordBytes,
        int TilePixels,
        TileCodec Codec);

    private enum TileCodec { Dxt1, Dxt3, Dxt5 }

    private static ParsedFile Parse(byte[] d)
    {
        if (d.Length < 18 || d[0] != (byte)'C' || d[1] != (byte)'R' || d[2] != (byte)'Y' || d[3] != 0)
            throw new InvalidDataException("not a CryEngine terrain texture (missing CRY signature)");
        var version = ReadU16(d, 6);
        var layerCount = ReadU16(d, 8);
        var flags = ReadU16(d, 10);
        var brightness = BitConverter.ToSingle(d, 12);
        if (layerCount < 1 || layerCount > 8)
            throw new InvalidDataException($"unexpected layer count {layerCount}");

        var pos = 16;
        var layers = new List<LayerInfo>();
        long recordBytes = 0;
        for (var i = 0; i < layerCount; i++)
        {
            Need(d, pos, 12);
            var tilePx = ReadU16(d, pos);
            var format = BitConverter.ToUInt32(d, pos + 4);
            var tileBytes = BitConverter.ToUInt32(d, pos + 8);
            if (tileBytes > int.MaxValue)
                throw new InvalidDataException($"layer {i}: tile size {tileBytes} bytes");
            layers.Add(new LayerInfo(tilePx, format, (int)tileBytes));
            recordBytes += tileBytes;
            pos += 12;
        }
        if (recordBytes > int.MaxValue)
            throw new InvalidDataException($"texture records of {recordBytes} bytes");

        Need(d, pos, 2);
        var nodeCount = ReadU16(d, pos);
        pos += 2;
        Need(d, pos, nodeCount * 2);
        var indexStart = pos;
        var dataStart = indexStart + nodeCount * 2;

        var cursor = 0;
        var textureCount = 0;
        var root = ReadNode(d, indexStart, nodeCount, ref cursor, 0, ref textureCount);
        if (cursor != nodeCount)
            throw new InvalidDataException($"quadtree uses {cursor} of {nodeCount} index entries");

        var layer0 = layers[0];
        var tilePixels = layer0.TilePixels;
        if (tilePixels < 4 || (tilePixels & (tilePixels - 1)) != 0)
            throw new InvalidDataException($"tile size {tilePixels} px is not a power of two");
        if (dataStart + textureCount * recordBytes > d.Length)
            throw new InvalidDataException($"file holds {d.Length} bytes, {textureCount} textures need {dataStart + textureCount * recordBytes}");

        var codec = PickCodec(d, layer0, dataStart, textureCount);
        return new ParsedFile(version, flags, brightness, layers, nodeCount, textureCount, root, dataStart,
            (int)recordBytes, tilePixels, codec);
    }

    private static Node ReadNode(byte[] d, int indexStart, int nodeCount, ref int cursor, int depth, ref int textureCount)
    {
        if (cursor >= nodeCount)
            throw new InvalidDataException("quadtree index table ends inside a node's children");
        if (depth > 16)
            throw new InvalidDataException("quadtree deeper than 16 levels");
        var node = new Node { TextureIndex = (short)ReadU16(d, indexStart + cursor * 2) };
        cursor++;
        if (node.TextureIndex < 0)
            return node;
        textureCount = Math.Max(textureCount, node.TextureIndex + 1);
        node.Children = new Node[4];
        for (var c = 0; c < 4; c++)
            node.Children[c] = ReadNode(d, indexStart, nodeCount, ref cursor, depth + 1, ref textureCount);
        return node;
    }

    private static TileCodec PickCodec(byte[] d, LayerInfo layer, int dataStart, int textureCount)
    {
        var px = layer.TilePixels;
        var blocks = px / 4 * (px / 4);
        switch (layer.Format)
        {
            case FormatDxt1 when layer.TileBytes >= blocks * 8:
                return TileCodec.Dxt1;
            case FormatDxt3 when layer.TileBytes >= blocks * 16:
                return TileCodec.Dxt3;
            case FormatDxt5 when layer.TileBytes >= blocks * 16:
                return TileCodec.Dxt5;
        }

        // Format 7 (A4R4G4B4 in CryEngine's enum) files carry DXT1 data padded with zeros to the declared tile size.
        var dxt1Bytes = blocks * 8;
        if (layer.TileBytes > dxt1Bytes && textureCount > 0)
        {
            var tail = d.AsSpan(dataStart + dxt1Bytes, layer.TileBytes - dxt1Bytes);
            if (!tail.ContainsAnyExcept((byte)0))
                return TileCodec.Dxt1;
        }
        throw new NotSupportedException($"terrain texture format {layer.Format} with {layer.TileBytes}-byte {px} px tiles");
    }

    private static int ReadU16(byte[] d, int pos) => d[pos] | (d[pos + 1] << 8);

    private static void Need(byte[] d, int pos, int count)
    {
        if (pos + count > d.Length)
            throw new InvalidDataException("terrain texture header is truncated");
    }

    // ---------------------------------------------------------------- assembly

    private sealed class AssembleContext(ParsedFile file, byte[] data, int level, int size, byte[] output)
    {
        private readonly Dictionary<int, byte[]> _tiles = [];

        /// <summary>Node at (nx, ny) on its level; nx counts west to east, ny south to north.</summary>
        public void Fill(Node node, int nodeLevel, int nx, int ny)
        {
            if (nodeLevel == level)
            {
                Blit(Tile(node.TextureIndex), nx, ny);
                return;
            }
            for (var c = 0; c < 4; c++)
            {
                var child = node.Children![c];
                var cx = nx * 2 + (c & 1);
                var cy = ny * 2 + (c >> 1);
                if (child.TextureIndex >= 0)
                    Fill(child, nodeLevel + 1, cx, cy);
                else
                    Upscale(Tile(node.TextureIndex), nodeLevel, nx, ny, nodeLevel + 1, cx, cy);
            }
        }

        private byte[] Tile(int index)
        {
            if (_tiles.TryGetValue(index, out var tile))
                return tile;
            var px = file.TilePixels;
            tile = new byte[px * px * 4];
            // Parse checked that every texture record lies inside the file, so the offset fits an int.
            var src = data.AsSpan((int)(file.DataStart + (long)index * file.RecordBytes));
            switch (file.Codec)
            {
                case TileCodec.Dxt1: DecodeBc1(src, px, px, tile); break;
                case TileCodec.Dxt3: DecodeBc2(src, px, px, tile); break;
                default: DecodeBc3(src, px, px, tile); break;
            }
            _tiles[index] = tile;
            return tile;
        }

        /// <summary>Copies a tile (memory rows = +X, columns = +Y) into the north-up output at level-grid (nx, ny).</summary>
        private void Blit(byte[] tile, int nx, int ny)
        {
            var px = file.TilePixels;
            for (var r = 0; r < px; r++)
            {
                var gx = nx * px + r;
                for (var c = 0; c < px; c++)
                {
                    var gy = ny * px + c;
                    var o = ((size - 1 - gy) * size + gx) * 4;
                    var s = (r * px + c) * 4;
                    output[o] = tile[s];
                    output[o + 1] = tile[s + 1];
                    output[o + 2] = tile[s + 2];
                    output[o + 3] = tile[s + 3];
                }
            }
        }

        /// <summary>
        /// Fills the region of the empty node (level emptyLevel, at ex, ey) from ancestor tile (level ancLevel, at ax, ay)
        /// with bilinear sampling. The region spans 2^(level - emptyLevel) output tiles.
        /// </summary>
        private void Upscale(byte[] tile, int ancLevel, int ax, int ay, int emptyLevel, int ex, int ey)
        {
            var px = file.TilePixels;
            var span = px << (level - emptyLevel); // output pixels across the empty node
            var scale = 1 << (level - ancLevel); // output pixels per ancestor texel
            var x0 = ex * span;
            var y0 = ey * span;
            var ancX0 = ax * px;
            var ancY0 = ay * px;
            for (var gx = x0; gx < x0 + span; gx++)
            {
                var fx = Math.Clamp((gx + 0.5f) / scale - 0.5f - ancX0, 0f, px - 1);
                var r0 = Math.Min((int)fx, px - 2);
                var tx = fx - r0;
                for (var gy = y0; gy < y0 + span; gy++)
                {
                    var fy = Math.Clamp((gy + 0.5f) / scale - 0.5f - ancY0, 0f, px - 1);
                    var c0 = Math.Min((int)fy, px - 2);
                    var ty = fy - c0;
                    var o = ((size - 1 - gy) * size + gx) * 4;
                    var s00 = (r0 * px + c0) * 4;
                    var s10 = s00 + px * 4;
                    for (var k = 0; k < 4; k++)
                    {
                        var a = tile[s00 + k] + (tile[s00 + 4 + k] - tile[s00 + k]) * ty;
                        var b = tile[s10 + k] + (tile[s10 + 4 + k] - tile[s10 + k]) * ty;
                        output[o + k] = (byte)(a + (b - a) * tx + 0.5f);
                    }
                }
            }
        }
    }

    // ---------------------------------------------------------------- block decoders

    /// <summary>Decodes DXT1 / BC1 into RGBA8, rows as stored. Width and height must be multiples of 4.</summary>
    public static void DecodeBc1(ReadOnlySpan<byte> src, int width, int height, Span<byte> rgba)
    {
        var bw = width / 4;
        Span<byte> palette = stackalloc byte[16];
        for (var by = 0; by < height / 4; by++)
        for (var bx = 0; bx < bw; bx++)
        {
            var block = src.Slice((by * bw + bx) * 8, 8);
            ColourPalette(block, palette, allowThreeColour: true);
            WriteColourIndices(block, palette, rgba, width, bx, by);
        }
    }

    /// <summary>Decodes DXT3 / BC2 (explicit 4-bit alpha) into RGBA8.</summary>
    public static void DecodeBc2(ReadOnlySpan<byte> src, int width, int height, Span<byte> rgba)
    {
        var bw = width / 4;
        Span<byte> palette = stackalloc byte[16];
        for (var by = 0; by < height / 4; by++)
        for (var bx = 0; bx < bw; bx++)
        {
            var block = src.Slice((by * bw + bx) * 16, 16);
            ColourPalette(block[8..], palette, allowThreeColour: false);
            WriteColourIndices(block[8..], palette, rgba, width, bx, by);
            for (var i = 0; i < 16; i++)
            {
                var nibble = (block[i / 2] >> (4 * (i & 1))) & 0xF;
                rgba[((by * 4 + i / 4) * width + bx * 4 + i % 4) * 4 + 3] = (byte)(nibble * 17);
            }
        }
    }

    /// <summary>Decodes DXT5 / BC3 (interpolated alpha) into RGBA8.</summary>
    public static void DecodeBc3(ReadOnlySpan<byte> src, int width, int height, Span<byte> rgba)
    {
        var bw = width / 4;
        Span<byte> palette = stackalloc byte[16];
        Span<byte> alphas = stackalloc byte[8];
        for (var by = 0; by < height / 4; by++)
        for (var bx = 0; bx < bw; bx++)
        {
            var block = src.Slice((by * bw + bx) * 16, 16);
            ColourPalette(block[8..], palette, allowThreeColour: false);
            WriteColourIndices(block[8..], palette, rgba, width, bx, by);

            int a0 = block[0], a1 = block[1];
            alphas[0] = (byte)a0;
            alphas[1] = (byte)a1;
            if (a0 > a1)
            {
                for (var i = 1; i < 7; i++)
                    alphas[i + 1] = (byte)(((7 - i) * a0 + i * a1) / 7);
            }
            else
            {
                for (var i = 1; i < 5; i++)
                    alphas[i + 1] = (byte)(((5 - i) * a0 + i * a1) / 5);
                alphas[6] = 0;
                alphas[7] = 255;
            }
            ulong bits = 0;
            for (var i = 0; i < 6; i++)
                bits |= (ulong)block[2 + i] << (8 * i);
            for (var i = 0; i < 16; i++)
                rgba[((by * 4 + i / 4) * width + bx * 4 + i % 4) * 4 + 3] = alphas[(int)((bits >> (3 * i)) & 7)];
        }
    }

    private static void ColourPalette(ReadOnlySpan<byte> block, Span<byte> palette, bool allowThreeColour)
    {
        var c0 = block[0] | (block[1] << 8);
        var c1 = block[2] | (block[3] << 8);
        Expand565(c0, palette[..4]);
        Expand565(c1, palette.Slice(4, 4));
        if (c0 > c1 || !allowThreeColour)
        {
            for (var k = 0; k < 3; k++)
            {
                palette[8 + k] = (byte)((2 * palette[k] + palette[4 + k]) / 3);
                palette[12 + k] = (byte)((palette[k] + 2 * palette[4 + k]) / 3);
            }
            palette[11] = 255;
            palette[15] = 255;
        }
        else
        {
            for (var k = 0; k < 3; k++)
            {
                palette[8 + k] = (byte)((palette[k] + palette[4 + k]) / 2);
                palette[12 + k] = 0;
            }
            palette[11] = 255;
            palette[15] = 0;
        }
    }

    private static void Expand565(int c, Span<byte> rgba)
    {
        var r = (c >> 11) & 31;
        var g = (c >> 5) & 63;
        var b = c & 31;
        rgba[0] = (byte)((r << 3) | (r >> 2));
        rgba[1] = (byte)((g << 2) | (g >> 4));
        rgba[2] = (byte)((b << 3) | (b >> 2));
        rgba[3] = 255;
    }

    private static void WriteColourIndices(ReadOnlySpan<byte> block, ReadOnlySpan<byte> palette, Span<byte> rgba,
        int width, int bx, int by)
    {
        var bits = (uint)(block[4] | (block[5] << 8) | (block[6] << 16) | (block[7] << 24));
        for (var i = 0; i < 16; i++)
        {
            var p = (int)((bits >> (2 * i)) & 3) * 4;
            var o = ((by * 4 + i / 4) * width + bx * 4 + i % 4) * 4;
            rgba[o] = palette[p];
            rgba[o + 1] = palette[p + 1];
            rgba[o + 2] = palette[p + 2];
            rgba[o + 3] = palette[p + 3];
        }
    }
}
