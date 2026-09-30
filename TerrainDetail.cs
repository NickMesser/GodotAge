using System.Collections.Concurrent;
using Godot;

namespace AAEmu.GodotViewer;

/// <summary>
/// Terrain detail layers, the way CryEngine draws terrain: the cell's cover texture gives the painted colour, and every
/// 2 m heightmap sample names a surface type (surface.xml) whose detail material (Terrain.Layer) tiles a sharp texture
/// and a normal map over it. The shader blends the four samples around each pixel.
/// <para>
/// The colour follows terrain.cfx (TerrainLayerPS, frag_custom_begin): the layer texture is an offset of the colour map
/// in encoded (sRGB) space, <c>saturate(cover + (detail - 0.5) * DetailTextureStrength)</c>, decoded with
/// <c>pow(x, 2.2)</c> and multiplied by the material's Diffuse colour. The far terrain (TerrainPS) is
/// <c>pow(cover, 2.2)</c>; a layer fades into it with the engine's weight <c>sqrt(1 - (distance / viewDistance)^4)</c>.
/// Both textures are therefore sampled raw, without Godot's sRGB decode. The layer's bump map (_ddn) perturbs the
/// normal in the frame terrain.cfx uses: tangent along +u (east), binormal along +v (north).
/// </para>
/// <para>
/// Detail textures live in one <see cref="DetailLayerBank"/> shared by every cell; each cell maps its up to 16 most used
/// surfaces to bank layers. UVs follow <see cref="TerrainCoverFast"/>: (uy / 512, ux / 512) for heightmap sample [ux, uy].
/// </para>
/// </summary>
internal sealed class TerrainDetail(DetailLayerBank bank, ModelLibrary models, CellSurfaceTypes surfaces)
{
    public const int MaxSlots = 16;
    private const byte NoSlot = 255;

    /// <summary>terrain.cfx default of DetailTextureStrength, for materials that don't set it.</summary>
    private const float DefaultDetailStrength = 1f;

    private const string ShaderCode = """
        shader_type spatial;
        render_mode cull_back;

        // Raw (encoded) samples: terrain.cfx does its colour maths on the sRGB values and decodes with pow(x, 2.2).
        uniform sampler2D cover : filter_linear_mipmap_anisotropic, repeat_disable;
        uniform sampler2D surface_index : filter_nearest, repeat_disable;
        uniform sampler2DArray detail : filter_linear_mipmap_anisotropic, repeat_enable;
        // Layer normal maps (the material's Bumpmap, _ddn): RG = tangent-space X, Y, DirectX style (+Y along +v).
        uniform sampler2DArray detail_normal : filter_linear_mipmap_anisotropic, repeat_enable;
        uniform int slot_count = 0;
        uniform int slot_layer[16];
        uniform int slot_normal[16];
        uniform vec2 slot_scale[16];
        uniform vec3 slot_mean[16];
        uniform float slot_strength[16];
        uniform vec3 slot_diffuse[16];
        uniform vec2 world_offset;
        // Detail material view distance: e_detail_materials_view_dist_z/xy of the client's top terrain-detail level
        // (game/config/cvargroups/option_terrain_detail.cfg); the layers' normal maps stop at
        // e_detail_materials_zpass_normal_draw_dist.
        uniform float detail_distance = 1024.0;
        uniform float normal_distance = 100.0;

        varying vec3 world_pos;
        varying vec3 world_normal;

        void vertex() {
            world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
            world_normal = normalize((MODEL_MATRIX * vec4(NORMAL, 0.0)).xyz);
        }

        // One surface slot: its colour (linear) and its tangent-space normal, both faded out by `near`.
        vec3 slot_color(int slot, vec2 cry_xy, vec3 cover_enc, vec3 far_color, float near, float near_normal,
                inout vec3 normal_ts) {
            if (slot >= slot_count || near <= 0.0) {
                return far_color;
            }
            vec2 uv = cry_xy * slot_scale[slot];
            vec3 d = slot_mean[slot];
            if (slot_layer[slot] >= 0) {
                d = texture(detail, vec3(uv, float(slot_layer[slot]))).rgb;
            }
            vec3 layer_enc = clamp(cover_enc + (d - 0.5) * slot_strength[slot], 0.0, 1.0);
            vec3 layer = pow(layer_enc, vec3(2.2)) * slot_diffuse[slot];
            if (slot_normal[slot] >= 0 && near_normal > 0.0) {
                vec2 xy = texture(detail_normal, vec3(uv, float(slot_normal[slot]))).rg * 2.0 - 1.0;
                vec3 n = vec3(xy, sqrt(clamp(1.0 - dot(xy, xy), 0.0, 1.0)));
                normal_ts = mix(vec3(0.0, 0.0, 1.0), n, near_normal);
            }
            return mix(far_color, layer, near);
        }

        void fragment() {
            vec3 cover_enc = texture(cover, UV).rgb;
            vec3 far_color = pow(cover_enc, vec3(2.2));
            vec3 color = far_color;
            vec3 n_world = normalize(world_normal);
            if (slot_count > 0) {
                float dist = distance(world_pos, CAMERA_POSITION_WORLD);
                float fade = pow(min(dist / max(detail_distance, 1.0), 1.0), 4.0);
                float near = sqrt(max(1.0 - fade, 0.0));
                float near_normal = 1.0 - smoothstep(normal_distance * 0.75, normal_distance, dist);
                // Surface slots sit on the 2 m grid; blend the four samples around this pixel.
                vec2 g = clamp(UV * 512.0, vec2(0.0), vec2(511.999));
                ivec2 i0 = ivec2(floor(g));
                vec2 f = fract(g);
                vec2 cry_xy = vec2(world_pos.x + world_offset.x, -world_pos.z + world_offset.y);
                int a = int(texelFetch(surface_index, i0, 0).r * 255.0 + 0.5);
                int b = int(texelFetch(surface_index, i0 + ivec2(1, 0), 0).r * 255.0 + 0.5);
                int c = int(texelFetch(surface_index, i0 + ivec2(0, 1), 0).r * 255.0 + 0.5);
                int d = int(texelFetch(surface_index, i0 + ivec2(1, 1), 0).r * 255.0 + 0.5);
                vec3 na = vec3(0.0, 0.0, 1.0);
                vec3 nb = na;
                vec3 nc = na;
                vec3 nd = na;
                vec3 ca = slot_color(a, cry_xy, cover_enc, far_color, near, near_normal, na);
                vec3 cb = ca;
                vec3 cc = ca;
                vec3 cd = ca;
                if (b == a) { nb = na; } else { cb = slot_color(b, cry_xy, cover_enc, far_color, near, near_normal, nb); }
                if (c == a) { nc = na; } else { cc = slot_color(c, cry_xy, cover_enc, far_color, near, near_normal, nc); }
                if (d == a) { nd = na; } else { cd = slot_color(d, cry_xy, cover_enc, far_color, near, near_normal, nd); }
                color = mix(mix(ca, cb, f.x), mix(cc, cd, f.x), f.y);
                vec3 n_ts = mix(mix(na, nb, f.x), mix(nc, nd, f.x), f.y);
                // terrain.cfx TerrainLayerVS: tangent along +u (Cry +X), binormal along +v (Cry +Y = Godot -Z), both
                // projected onto the plane of the terrain normal.
                vec3 t = normalize(vec3(1.0, 0.0, 0.0) - n_world * n_world.x);
                vec3 bn = normalize(vec3(0.0, 0.0, -1.0) + n_world * n_world.z);
                n_world = normalize(t * n_ts.x + bn * n_ts.y + n_world * n_ts.z);
                // frag_custom_ambient: the ambient is scaled by the bump map's z (cBumpMap.z).
                AO = n_ts.z;
                AO_LIGHT_AFFECT = 0.0;
            }
            ALBEDO = color;
            NORMAL = normalize((VIEW_MATRIX * vec4(n_world, 0.0)).xyz);
            ROUGHNESS = 0.95;
            // terrain.cfx has no environment reflection (no ENVIRONMENT_MAP in the layers' GenMask): without this the sky
            // reflection tints shaded ground blue.
            SPECULAR = 0.0;
        }
        """;

    private static readonly Lazy<Shader> SharedShader = new(() => new Shader { Code = ShaderCode });

    /// <summary>A cell's slot table and per-sample slot index, built on a worker thread.</summary>
    public sealed record Layers(byte[] IndexImage, int SlotCount, int[] SlotLayers, int[] NormalLayers, Vector2[] Scales,
        Vector3[] Means, float[] Strengths, Vector3[] Diffuse);

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
        var normalLayers = Enumerable.Repeat(-1, MaxSlots).ToArray();
        var scales = Enumerable.Repeat(Vector2.One, MaxSlots).ToArray();
        var means = Enumerable.Repeat(new Vector3(0.5f, 0.5f, 0.5f), MaxSlots).ToArray();
        var strengths = new float[MaxSlots];
        var diffuse = Enumerable.Repeat(Vector3.One, MaxSlots).ToArray();
        var count = 0;
        foreach (var id in usage.OrderByDescending(kv => kv.Value).Select(kv => kv.Key))
        {
            if (count == MaxSlots)
                break;
            if (!surfaces.ById.TryGetValue(id, out var surface) || surface.DetailMaterial == null)
                continue;
            var material = models.Mtl(surface.DetailMaterial)?.ForSubset(0);
            var diffuseMap = material?.DiffuseMap;
            if (diffuseMap == null)
                continue;
            var (layer, mean) = bank.Request(diffuseMap.PakPath);
            if (layer < 0 && mean == Vector3.Zero)
                continue; // unreadable texture
            slotOf[id] = (byte)count;
            slotLayers[count] = layer;
            normalLayers[count] = material.NormalMap is { PakPath.Length: > 0 } normal ? bank.RequestNormal(normal.PakPath) : -1;
            // Repeats per metre: the surface's detail scale times the material's texture tiling.
            scales[count] = new Vector2(surface.DetailScale.X * diffuseMap.TileU, surface.DetailScale.Y * diffuseMap.TileV);
            means[count] = mean;
            strengths[count] = PublicScalar(material, "DetailTextureStrength", DefaultDetailStrength);
            diffuse[count] = new Vector3(material.DiffuseColor.X, material.DiffuseColor.Y, material.DiffuseColor.Z);
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
        return new Layers(index, count, slotLayers, normalLayers, scales, means, strengths, diffuse);
    }

    private static float PublicScalar(MtlMaterial material, string name, float fallback) =>
        material.PublicParams.TryGetValue(name, out var text) &&
        float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;

    /// <summary>Any thread (after the bank is created). The terrain material: cover texture plus detail layers.</summary>
    public Material CreateMaterial(Texture2D cover, Layers layers, Vector2 worldOffset)
    {
        var index = ImageTexture.CreateFromImage(Image.CreateFromData(TerrainCellReader.Samples, TerrainCellReader.Samples, false, Image.Format.R8, layers.IndexImage));
        var material = new ShaderMaterial { Shader = SharedShader.Value };
        material.SetShaderParameter("cover", cover);
        material.SetShaderParameter("surface_index", index);
        material.SetShaderParameter("detail", bank.Array);
        material.SetShaderParameter("detail_normal", bank.NormalArray);
        material.SetShaderParameter("slot_count", layers.SlotCount);
        material.SetShaderParameter("slot_layer", layers.SlotLayers);
        material.SetShaderParameter("slot_normal", layers.NormalLayers);
        material.SetShaderParameter("slot_scale", layers.Scales);
        material.SetShaderParameter("slot_mean", layers.Means);
        material.SetShaderParameter("slot_strength", layers.Strengths);
        material.SetShaderParameter("slot_diffuse", layers.Diffuse);
        material.SetShaderParameter("world_offset", worldOffset);
        return material;
    }
}

/// <summary>
/// Texture arrays of terrain detail textures for the whole session: the layers' diffuse maps (512 px DXT1 with mipmaps)
/// and their normal maps (512 px BC5). A texture is decoded, resized and compressed once, on whichever worker thread
/// first needs it, and uploaded into the next free layer on the main thread.
/// </summary>
internal sealed class DetailLayerBank(Action<Action> post)
{
    public const int Capacity = 128;
    private const int LayerSize = 512;

    private readonly ConcurrentDictionary<string, Lazy<(int Layer, Vector3 Mean)>> _layers = new();
    private readonly ConcurrentDictionary<string, Lazy<int>> _normalLayers = new();
    private int _next;
    private int _nextNormal;
    private int _normalMipmaps;

    /// <summary>Created on the main thread before any cell loads; read from any thread after that.</summary>
    public Texture2DArray Array { get; private set; }

    /// <summary>The layers' normal maps (RG = tangent-space X, Y), same lifetime as <see cref="Array"/>.</summary>
    public Texture2DArray NormalArray { get; private set; }

    /// <summary>Main thread, once at startup: the arrays with empty layers.</summary>
    public void Create()
    {
        var blank = Image.CreateEmpty(LayerSize, LayerSize, true, Image.Format.Dxt1);
        Array = new Texture2DArray();
        Array.CreateFromImages(new Godot.Collections.Array<Image>(Enumerable.Repeat(blank, Capacity)));
        var flat = Image.CreateEmpty(LayerSize, LayerSize, true, Image.Format.RgtcRg);
        _normalMipmaps = flat.GetMipmapCount();
        NormalArray = new Texture2DArray();
        NormalArray.CreateFromImages(new Godot.Collections.Array<Image>(Enumerable.Repeat(flat, Capacity)));
    }

    /// <summary>
    /// Any worker thread. The bank layer of a detail texture and its average colour (encoded, as stored). Layer -1 with
    /// a zero mean when the texture can't be read; layer -1 with a real mean when the bank is full (the average colour
    /// still works).
    /// </summary>
    public (int Layer, Vector3 Mean) Request(string texturePath) =>
        _layers.GetOrAdd(texturePath, path => new Lazy<(int, Vector3)>(() => Load(path))).Value;

    /// <summary>Any worker thread. The normal-map layer of a detail material's bump map, or -1 when unusable.</summary>
    public int RequestNormal(string texturePath) =>
        _normalLayers.GetOrAdd(texturePath, path => new Lazy<int>(() => LoadNormal(path))).Value;

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
        // The shader works on the encoded values, like terrain.cfx.
        var c = tiny.GetPixel(0, 0);
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

    /// <summary>ATI2 (BC5) _ddn maps of the bank's size are copied as they are; anything else is converted to BC5.</summary>
    private int LoadNormal(string texturePath)
    {
        var bytes = PakFiles.Read(texturePath);
        if (bytes == null)
            return -1;
        var image = new Image();
        if (image.LoadDdsFromBuffer(CryDds.PrepareForGodot(bytes)) != Error.Ok || image.IsEmpty())
            return -1;
        if (image.GetFormat() != Image.Format.RgtcRg || image.GetWidth() != LayerSize || image.GetHeight() != LayerSize ||
            image.GetMipmapCount() != _normalMipmaps)
        {
            if (image.IsCompressed())
                image.Decompress();
            image.ClearMipmaps();
            image.Convert(Image.Format.Rg8);
            image.Resize(LayerSize, LayerSize, Image.Interpolation.Bilinear);
            image.GenerateMipmaps();
            image.Compress(Image.CompressMode.S3Tc, Image.CompressSource.Normal);
            if (image.GetFormat() != Image.Format.RgtcRg || image.GetMipmapCount() != _normalMipmaps)
                return -1;
        }
        var layer = Interlocked.Increment(ref _nextNormal) - 1;
        if (layer >= Capacity)
            return -1;
        post(() => NormalArray.UpdateLayer(image, layer));
        return layer;
    }
}
