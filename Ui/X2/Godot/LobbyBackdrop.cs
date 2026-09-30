#nullable enable
using Godot;

namespace AAEmu.GodotViewer.Ui.X2;

/// <summary>
/// The login2 stage's sky and floor. The original client shows a dark navy night on every lobby page (select, race, skillset,
/// customize): a sky that fades from black at the horizon to teal-navy overhead, and a mirror-like ocean floor that is darkest at
/// the horizon and brightens toward the camera. The gradients below are the mean sRGB colours measured on original-client
/// captures at the stage's default 06:30 (select: x 430..540, cameras 02_*_cam_df; race agrees), keyed by view angle so every
/// camera gets the same backdrop. Other hours scale them by the zone's sky colour relative to 06:30 (the original's dev TOD
/// slider tints the whole stage the same way). Login stage only: the world sky, terrain and water are untouched.
/// </summary>
internal sealed class LobbyBackdrop
{
    private const int StopCount = 16;

    // (degrees above the horizon, sRGB 0..255). Plateau above the last stop.
    private static readonly (float Angle, float R, float G, float B)[] SkyStops =
    [
        (0f, 4, 9, 9), (0.5f, 5, 12, 12), (1.0f, 7, 15, 16), (1.7f, 9, 19, 21), (2.4f, 11, 22, 25), (3.1f, 12, 25, 29),
        (3.8f, 14, 28, 32), (4.4f, 17, 32, 36), (5.1f, 19, 35, 40), (5.8f, 20, 37, 43), (6.4f, 21, 39, 47),
        (7.8f, 22, 41, 52), (9.1f, 23, 44, 58), (10.4f, 24, 47, 62), (12.0f, 28, 51, 65), (16.0f, 31, 52, 66),
    ];

    // (degrees below the horizon, sRGB): the mirror-ocean floor. Plateau beyond the last stop.
    private static readonly (float Angle, float R, float G, float B)[] FloorStops =
    [
        (0f, 4, 9, 9), (1.0f, 4, 10, 9), (2.4f, 5, 13, 14), (3.1f, 9, 19, 27), (3.8f, 12, 24, 36), (4.5f, 11, 22, 30),
        (5.8f, 11, 24, 33), (7.2f, 14, 28, 38), (8.5f, 15, 31, 43), (9.9f, 18, 35, 50), (11.2f, 23, 43, 60),
        (12.5f, 26, 45, 61), (13.9f, 31, 48, 65), (15.2f, 34, 52, 69), (18.0f, 34, 52, 69), (30.0f, 34, 52, 69),
    ];

    private const string SkyShader = """
        shader_type sky;
        uniform vec3 sky_stops[16];
        uniform float sky_angles[16];
        uniform vec3 floor_stops[16];
        uniform float floor_angles[16];
        uniform vec3 tint = vec3(1.0);

        vec3 ramp(vec3 colors[16], float angles[16], float a) {
            vec3 c = colors[0];
            for (int i = 1; i < 16; i++) {
                if (a >= angles[i - 1]) {
                    float span = max(angles[i] - angles[i - 1], 0.0001);
                    c = mix(colors[i - 1], colors[i], clamp((a - angles[i - 1]) / span, 0.0, 1.0));
                }
            }
            return c;
        }

        void sky() {
            float degrees = degrees(asin(clamp(EYEDIR.y, -1.0, 1.0)));
            vec3 c = degrees >= 0.0 ? ramp(sky_stops, sky_angles, degrees) : ramp(floor_stops, floor_angles, -degrees);
            COLOR = c * tint;
        }
        """;

    // The ocean plane is the same floor ramp evaluated per pixel from the view ray, so it joins the sky's lower half seamlessly and
    // hides the bare terrain of the stage cell. Unlit: the original floor is a dark mirror, not a lit surface.
    private const string FloorShader = """
        shader_type spatial;
        render_mode unshaded, cull_disabled, fog_disabled, depth_draw_opaque;
        uniform vec3 floor_stops[16];
        uniform float floor_angles[16];
        uniform vec3 tint = vec3(1.0);
        varying vec3 world_position;

        void vertex() {
            world_position = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
        }

        void fragment() {
            vec3 ray = world_position - CAMERA_POSITION_WORLD;
            float degrees = degrees(asin(clamp(-ray.y / max(length(ray), 0.0001), 0.0, 1.0)));
            vec3 c = floor_stops[0];
            for (int i = 1; i < 16; i++) {
                if (degrees >= floor_angles[i - 1]) {
                    float span = max(floor_angles[i] - floor_angles[i - 1], 0.0001);
                    c = mix(floor_stops[i - 1], floor_stops[i], clamp((degrees - floor_angles[i - 1]) / span, 0.0, 1.0));
                }
            }
            ALBEDO = c * tint;
        }
        """;

    // The two "graphic_renew" background emitters of the lobby cinemas (X2_EFFECT_KSW.Test.graphic_renew_BG_00 / _01). The particle
    // system cannot draw them: they are geometry particles built from pieces of CGF meshes. BG_01 is a hemisphere of 7.35 m
    // (mesh radius 0.92 x Size 8) carrying the star texture, BG_00 four 10 x 23 m glowing ribbon sheets (mesh 1.69 x 3.80 x Size 6).
    public const string StarEffect = "X2_EFFECT_KSW.Test.graphic_renew_BG_01";
    public const string RibbonEffect = "X2_EFFECT_KSW.Test.graphic_renew_BG_00";
    private const string StarTexture = "game/textures/p_effects/ksw_effect/graphic_renew_bg_01.dds";
    private const string RibbonTexture = "game/textures/p_effects/ksw_effect/graphic_renew_bg_00.dds";

    private const string StarShader = """
        shader_type spatial;
        render_mode unshaded, blend_add, cull_disabled, depth_draw_never, fog_disabled;
        uniform sampler2D stars : source_color, filter_linear_mipmap, repeat_enable;
        uniform vec2 tiling = vec2(15.0, 10.0);
        uniform float gain = 16.0;
        varying float height;
        void vertex() {
            height = VERTEX.y / 7.35;
        }
        void fragment() {
            vec3 c = texture(stars, UV * tiling + vec2(TIME * 0.004, 0.0)).rgb;
            // No stars in the mist band along the horizon (the original's sky is black there).
            ALBEDO = c * gain * smoothstep(0.04, 0.32, height);
        }
        """;

    private const string RibbonShader = """
        shader_type spatial;
        render_mode unshaded, blend_add, cull_disabled, depth_draw_never, fog_disabled;
        uniform sampler2D band : source_color, filter_linear_mipmap, repeat_enable;
        uniform vec3 colour = vec3(1.0);
        uniform float gain = 1.0;
        uniform float amplitude = 0.35;
        uniform float phase = 0.0;
        uniform float speed = 0.12;
        void vertex() {
            VERTEX.y += amplitude * sin(VERTEX.z * 2.1 + TIME * speed * 6.0 + phase) * sin(VERTEX.x * 1.7 - TIME * speed * 3.0 + phase);
        }
        void fragment() {
            vec2 uv = UV + vec2(sin(TIME * speed + phase) * 0.03, 0.0);
            ALBEDO = texture(band, uv).rgb * colour * gain;
        }
        """;

    private readonly ShaderMaterial _sky;
    private readonly ShaderMaterial _floor;
    private Node3D? _background;

    public LobbyBackdrop()
    {
        _sky = new ShaderMaterial { Shader = new Shader { Code = SkyShader } };
        _floor = new ShaderMaterial { Shader = new Shader { Code = FloorShader } };
        var (skyColors, skyAngles) = Pack(SkyStops);
        var (floorColors, floorAngles) = Pack(FloorStops);
        _sky.SetShaderParameter("sky_stops", skyColors);
        _sky.SetShaderParameter("sky_angles", skyAngles);
        _sky.SetShaderParameter("floor_stops", floorColors);
        _sky.SetShaderParameter("floor_angles", floorAngles);
        _floor.SetShaderParameter("floor_stops", floorColors);
        _floor.SetShaderParameter("floor_angles", floorAngles);
    }

    public Sky CreateSky() => new() { SkyMaterial = _sky };

    /// <summary>The star dome of graphic_renew_BG_01 at the emitter (offset -1,-6,1.5 in emitter space, mesh radius 0.92 x Size 8).</summary>
    public Node3D CreateStars(Transform3D emitter)
    {
        var material = new ShaderMaterial { Shader = new Shader { Code = StarShader } };
        material.SetShaderParameter("gain", Env("X2_LOBBY_STAR", 18f));
        if (LoadTexture(StarTexture) is { } stars) material.SetShaderParameter("stars", stars);
        var dome = new MeshInstance3D
        {
            Name = "LobbyStars",
            Mesh = new SphereMesh { Radius = 7.35f, Height = 7.35f, IsHemisphere = true, RadialSegments = 96, Rings = 32, Material = material },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Position = new Vector3(-1f, 1.5f, 6f), // Cry (-1,-6,1.5) -> Godot (x, z, -y)
        };
        var anchor = new Node3D { Name = "graphic_renew_BG_01", Transform = emitter };
        anchor.AddChild(dome);
        return anchor;
    }

    /// <summary>The four glowing ribbon sheets of graphic_renew_BG_00 (materials graphic_renew_bg_00..03, alpha 0.3/0.5/0.5/0.5).</summary>
    public Node3D CreateRibbons(Transform3D emitter)
    {
        var anchor = new Node3D { Name = "graphic_renew_BG_00", Transform = emitter };
        var texture = LoadTexture(RibbonTexture);
        // Layer tints and strengths follow the emitter's EmissiveHDRDynamic (10, 7, 5, 7) x Alpha (0.3, 0.5, 0.5, 0.5).
        (float Gain, Vector3 Colour, float Phase)[] layers =
        [
            (3.0f, new Vector3(0.55f, 0.75f, 1.00f), 0.0f), (3.5f, new Vector3(0.45f, 0.70f, 1.00f), 1.7f),
            (2.5f, new Vector3(0.60f, 0.80f, 1.00f), 3.1f), (3.5f, new Vector3(0.50f, 0.65f, 0.95f), 4.6f),
        ];
        for (var i = 0; i < layers.Length; i++)
        {
            var material = new ShaderMaterial { Shader = new Shader { Code = RibbonShader } };
            if (texture != null) material.SetShaderParameter("band", texture);
            material.SetShaderParameter("colour", layers[i].Colour);
            material.SetShaderParameter("gain", layers[i].Gain * RibbonGain);
            material.SetShaderParameter("phase", layers[i].Phase);
            var sheet = new MeshInstance3D
            {
                Name = $"BG{i + 1}",
                // The CGF plane is 1.6885 x 3.8024 m (x -0.60..1.09, y +-1.90); Size 6 scales it to 10.1 x 22.8 m.
                Mesh = new PlaneMesh { Size = new Vector2(1.6885f, 3.8024f), SubdivideWidth = 10, SubdivideDepth = 60, Material = material },
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                Scale = Vector3.One * 6f,
                Position = new Vector3(-5f + 0.2443f * 6f, 0.2f + 0.02f * i, 0f), // offset (-5,0,0.2) plus the mesh centre, layers stacked 2 cm
                RotationDegrees = new Vector3(0f, 0f, -5f),
            };
            anchor.AddChild(sheet);
        }
        return anchor;
    }

    /// <summary>Mean brightness scale of the ribbons, tuned against the original captures.</summary>
    public static float RibbonGain { get; set; } = Env("X2_LOBBY_RIBBON", 0.015f);
    private static float Env(string name, float fallback) =>
        float.TryParse(System.Environment.GetEnvironmentVariable(name), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : fallback;

    private static Texture2D? LoadTexture(string pakPath)
    {
        var bytes = PakFiles.Read(pakPath);
        if (bytes == null) return null;
        var image = new Image();
        return image.LoadDdsFromBuffer(CryDds.PrepareForGodot(bytes)) == Error.Ok && !image.IsEmpty() ? ImageTexture.CreateFromImage(image) : null;
    }

    /// <summary>A huge plane at <paramref name="level"/> (the world's oceanLevel) centred on the stage cell.</summary>
    public MeshInstance3D CreateFloor(float level, Vector3 centre) => new()
    {
        Name = "LobbyOcean",
        Mesh = new PlaneMesh { Size = new Vector2(13120f, 13120f), Material = _floor },
        CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        Position = new Vector3(centre.X, level, centre.Z),
    };

    /// <summary>Scales the 06:30 look by the zone's sky colour at the current hour relative to 06:30.</summary>
    public void SetTint(Vector3 ratio)
    {
        var tint = new Vector3(Math.Clamp(ratio.X, 0f, 6f), Math.Clamp(ratio.Y, 0f, 6f), Math.Clamp(ratio.Z, 0f, 6f));
        _sky.SetShaderParameter("tint", tint);
        _floor.SetShaderParameter("tint", tint);
    }

    private static (Vector3[] Colors, float[] Angles) Pack((float Angle, float R, float G, float B)[] stops)
    {
        var colors = new Vector3[StopCount];
        var angles = new float[StopCount];
        for (var i = 0; i < StopCount; i++)
        {
            var s = stops[Math.Min(i, stops.Length - 1)];
            colors[i] = new Vector3(Linear(s.R), Linear(s.G), Linear(s.B));
            angles[i] = s.Angle;
        }
        return (colors, angles);
    }

    private static float Linear(float srgb255)
    {
        var v = srgb255 / 255f;
        return v <= 0.04045f ? v / 12.92f : MathF.Pow((v + 0.055f) / 1.055f, 2.4f);
    }
}
