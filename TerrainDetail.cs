using System.Collections.Concurrent;
using Godot;

namespace AAEmu.GodotViewer;

/// <summary>
/// Terrain detail layers, the way CryEngine draws terrain: the cell's cover texture gives the painted colour, and every
/// 2 m heightmap sample names a surface type (surface.xml) whose detail material tiles a sharp texture over it. Like the
/// engine, the colour is cover x detail x 2 (gamma space); beyond the fade distance each layer's average colour stands in
/// for its texture, so near and far ground keep the same tone. The shader blends the four samples around each pixel.
/// <para>
/// Detail textures live in one <see cref="DetailLayerBank"/> shared by every cell; each cell maps its up to 16 most used
/// surfaces to bank layers. UVs follow <see cref="TerrainCoverFast"/>: (uy / 512, ux / 512) for heightmap sample [ux, uy].
/// </para>
/// </summary>
internal sealed class TerrainDetail(DetailLayerBank bank, ModelLibrary models, CellSurfaceTypes surfaces)
{
    public const int MaxSlots = 16;
    private const byte NoSlot = 255;

    private const string ShaderCode = """
        shader_type spatial;
        render_mode cull_back;

        uniform sampler2D cover : source_color, filter_linear_mipmap_anisotropic, repeat_disable;
        uniform sampler2D surface_index : filter_nearest, repeat_disable;
        uniform sampler2DArray detail : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
        uniform int slot_count = 0;
        uniform int slot_layer[16];
        uniform vec2 slot_scale[16];
        uniform vec3 slot_mean[16];
        uniform vec2 world_offset;
        uniform float fade_start = 70.0;
        uniform float fade_end = 180.0;

        varying vec3 world_pos;

        void vertex() {
            world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
        }

        // CryEngine multiplies the colour map by the layer's detail texture times two in gamma space: x 2^2.2 in linear.
        const float DETAIL_GAIN = 4.59;

        vec3 slot_color(int slot, vec2 cry_xy, vec3 base, float near) {
            if (slot >= slot_count) {
                return base;
            }
            vec3 d = slot_mean[slot];
            if (near > 0.0 && slot_layer[slot] >= 0) {
                d = mix(d, texture(detail, vec3(cry_xy * slot_scale[slot], float(slot_layer[slot]))).rgb, near);
            }
            return clamp(base * d * DETAIL_GAIN, 0.0, 1.0);
        }

        void fragment() {
            vec3 base = texture(cover, UV).rgb;
            vec3 color = base;
            if (slot_count > 0) {
                float near = 1.0 - smoothstep(fade_start, fade_end, distance(world_pos, CAMERA_POSITION_WORLD));
                // Surface slots sit on the 2 m grid; blend the four samples around this pixel.
                vec2 g = clamp(UV * 512.0, vec2(0.0), vec2(511.999));
                ivec2 i0 = ivec2(floor(g));
                vec2 f = fract(g);
                vec2 cry_xy = vec2(world_pos.x + world_offset.x, -world_pos.z + world_offset.y);
                int a = int(texelFetch(surface_index, i0, 0).r * 255.0 + 0.5);
                int b = int(texelFetch(surface_index, i0 + ivec2(1, 0), 0).r * 255.0 + 0.5);
                int c = int(texelFetch(surface_index, i0 + ivec2(0, 1), 0).r * 255.0 + 0.5);
                int d = int(texelFetch(surface_index, i0 + ivec2(1, 1), 0).r * 255.0 + 0.5);
                vec3 ca = slot_color(a, cry_xy, base, near);
                vec3 cb = (b == a) ? ca : slot_color(b, cry_xy, base, near);
                vec3 cc = (c == a) ? ca : slot_color(c, cry_xy, base, near);
                vec3 cd = (d == a) ? ca : slot_color(d, cry_xy, base, near);
                color = mix(mix(ca, cb, f.x), mix(cc, cd, f.x), f.y);
            }
            ALBEDO = color;
            ROUGHNESS = 0.95;
        }
        """;

    private static readonly Lazy<Shader> SharedShader = new(() => new Shader { Code = ShaderCode });

    /// <summary>A cell's slot table and per-sample slot index, built on a worker thread.</summary>
    public sealed record Layers(byte[] IndexImage, int SlotCount, int[] SlotLayers, Vector2[] Scales, Vector3[] Means);

    /// <summary>
    /// Worker thread. Reads the surface id of every heightmap sample and maps the cell's most used surfaces (up to
    /// <see cref="MaxSlots"/>) to detail layers. Null when the cell has no usable detail materials.
    /// </summary>
    public Layers Build(byte[] heightmap)
    {
        if (surfaces == null)
            return null;
        int[,] ids;
        try
        {
            ids = TerrainSurfaceReader.ReadSurfaceIds(heightmap);
        }
        catch (Exception e)
        {
            GD.PrintErr($"Surface ids: {e.Message}");
            return null;
        }

        var usage = new Dictionary<int, int>();
        foreach (var id in ids)
            if (id >= 0)
                usage[id] = usage.GetValueOrDefault(id) + 1;

        var slotOf = new Dictionary<int, byte>();
        var slotLayers = Enumerable.Repeat(-1, MaxSlots).ToArray();
        var scales = Enumerable.Repeat(Vector2.One, MaxSlots).ToArray();
        var means = Enumerable.Repeat(Vector3.One, MaxSlots).ToArray();
        var count = 0;
        foreach (var id in usage.OrderByDescending(kv => kv.Value).Select(kv => kv.Key))
        {
            if (count == MaxSlots)
                break;
            if (!surfaces.ById.TryGetValue(id, out var surface) || surface.DetailMaterial == null)
                continue;
            var diffuse = models.Mtl(surface.DetailMaterial)?.ForSubset(0)?.DiffuseMap;
            if (diffuse == null)
                continue;
            var (layer, mean) = bank.Request(diffuse.PakPath);
            if (layer < 0 && mean == Vector3.Zero)
                continue; // unreadable texture
            slotOf[id] = (byte)count;
            slotLayers[count] = layer;
            // Repeats per metre: the surface's detail scale times the material's texture tiling.
            scales[count] = new Vector2(surface.DetailScale.X * diffuse.TileU, surface.DetailScale.Y * diffuse.TileV);
            means[count] = mean;
            count++;
        }
        if (count == 0)
            return null;

        // Row = X sample, column = Y sample, matching the cover texture's orientation and the mesh UVs.
        var n = TerrainCellReader.Samples;
        var index = new byte[n * n];
        for (var ux = 0; ux < n; ux++)
        for (var uy = 0; uy < n; uy++)
        {
            var id = ids[ux, uy];
            index[ux * n + uy] = id >= 0 && slotOf.TryGetValue(id, out var slot) ? slot : NoSlot;
        }
        return new Layers(index, count, slotLayers, scales, means);
    }

    /// <summary>Any thread (after the bank is created). The terrain material: cover texture plus detail layers.</summary>
    public Material CreateMaterial(Texture2D cover, Layers layers, Vector2 worldOffset)
    {
        var index = ImageTexture.CreateFromImage(Image.CreateFromData(TerrainCellReader.Samples, TerrainCellReader.Samples, false, Image.Format.R8, layers.IndexImage));
        var material = new ShaderMaterial { Shader = SharedShader.Value };
        material.SetShaderParameter("cover", cover);
        material.SetShaderParameter("surface_index", index);
        material.SetShaderParameter("detail", bank.Array);
        material.SetShaderParameter("slot_count", layers.SlotCount);
        material.SetShaderParameter("slot_layer", layers.SlotLayers);
        material.SetShaderParameter("slot_scale", layers.Scales);
        material.SetShaderParameter("slot_mean", layers.Means);
        material.SetShaderParameter("world_offset", worldOffset);
        return material;
    }
}

/// <summary>
/// One texture array of detail textures for the whole session (512 px DXT1 with mipmaps per layer). A texture is
/// decoded, resized and compressed once, on whichever worker thread first needs it, and uploaded into the next free
/// layer on the main thread.
/// </summary>
internal sealed class DetailLayerBank(Action<Action> post)
{
    public const int Capacity = 128;
    private const int LayerSize = 512;

    private readonly ConcurrentDictionary<string, Lazy<(int Layer, Vector3 Mean)>> _layers = new();
    private int _next;

    /// <summary>Created on the main thread before any cell loads; read from any thread after that.</summary>
    public Texture2DArray Array { get; private set; }

    /// <summary>Main thread, once at startup: the array with empty layers.</summary>
    public void Create()
    {
        var blank = Image.CreateEmpty(LayerSize, LayerSize, true, Image.Format.Dxt1);
        Array = new Texture2DArray();
        Array.CreateFromImages(new Godot.Collections.Array<Image>(Enumerable.Repeat(blank, Capacity)));
    }

    /// <summary>
    /// Any worker thread. The bank layer of a detail texture and its average colour (linear). Layer -1 with a zero mean
    /// when the texture can't be read; layer -1 with a real mean when the bank is full (the average colour still works).
    /// </summary>
    public (int Layer, Vector3 Mean) Request(string texturePath) =>
        _layers.GetOrAdd(texturePath, path => new Lazy<(int, Vector3)>(() => Load(path))).Value;

    private (int, Vector3) Load(string texturePath)
    {
        var bytes = PakFiles.Read(texturePath);
        if (bytes == null)
            return (-1, Vector3.Zero);
        var image = new Image();
        if (image.LoadDdsFromBuffer(CryDds.PrepareForGodot(bytes)) != Error.Ok || image.IsEmpty())
            return (-1, Vector3.Zero);
        if (image.IsCompressed())
            image.Decompress();
        image.ClearMipmaps();
        image.Convert(Image.Format.Rgb8); // no alpha, so compression picks DXT1 like the array
        image.Resize(LayerSize, LayerSize, Image.Interpolation.Bilinear);
        var tiny = (Image)image.Duplicate();
        tiny.Resize(1, 1, Image.Interpolation.Bilinear);
        // The shader samples the layers as sRGB (converted to linear), so keep a linear mean.
        var c = tiny.GetPixel(0, 0).SrgbToLinear();
        var mean = new Vector3(c.R, c.G, c.B);
        image.GenerateMipmaps();
        image.Compress(Image.CompressMode.S3Tc, Image.CompressSource.Srgb);
        if (image.GetFormat() != Image.Format.Dxt1)
            return (-1, mean);
        var layer = Interlocked.Increment(ref _next) - 1;
        if (layer >= Capacity)
            return (-1, mean);
        post(() => Array.UpdateLayer(image, layer));
        return (layer, mean);
    }
}
