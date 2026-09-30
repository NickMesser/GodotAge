using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Godot;

namespace AAEmu.GodotViewer.Effects;

/// <summary>Creates GPU emitters and the small CPU ribbon used for CE3 tail emitters.</summary>
public sealed partial class ParticleFactory
{
    private sealed record TextureAsset(Texture2D Texture, bool HasAlpha);
    private sealed record SharedResources(ParticleProcessMaterial Process, Mesh DrawMesh, Mesh TrailMesh,float FinalSpriteExtent);
    private readonly Dictionary<string, TextureAsset> _textures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SharedResources> _shared = new(StringComparer.Ordinal);
    private readonly ModelLibrary _models;
    /// <summary>
    /// Draws sprites authored with FocusGravityDir as vertical beams: the sprite's long axis (SizeX) stands along the gravity direction and
    /// the sprite turns about that axis to face the camera. Off by default (the world's effects are unchanged); the login stage turns it on
    /// for the race page's portal, whose flares are vertical light beams in the client.
    /// </summary>
    public bool FocusGravityBeams { get; set; }
    /// <summary>
    /// Emitters authored with EmissiveLighting below 1 are only partly self-lit in the client (smoke and haze with 0.25 stay close to the
    /// scene's own light); by default this factory draws them at full brightness, which washes a dark scene white. When set, an authored
    /// EmissiveLighting below 1 scales the colour. Off by default (the world's effects are unchanged); the login stage turns it on.
    /// </summary>
    public bool DimByEmissive { get; set; }
    private float EmissiveScale(float emissiveLighting) => DimByEmissive && emissiveLighting > 0f && emissiveLighting < 1f ? emissiveLighting : Math.Max(1f, emissiveLighting);
    public bool DebugEnabled { get; } = OS.GetCmdlineUserArgs().Any(x => x.TrimStart('-').Equals("particle-debug", StringComparison.OrdinalIgnoreCase));
    private static readonly object BudgetLock = new();
    private static int _reservedParticles;
    /// <summary>Maximum capacity assigned to one authored emitter before the shared scene budget is applied.</summary>
    public static int PerEmitterParticleLimit { get; set; } = 8_192;
    public static int GlobalParticleLimit { get; set; } = 60_000;
    /// <summary>Default camera distance in metres for particle visibility when the XML has no MaxViewDist.</summary>
    public static float DefaultVisibilityRange { get; set; } = 300f;
    public static int ReservedParticles { get { lock (BudgetLock) return _reservedParticles; } }

    [GeneratedRegex(@"[-+]?(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][-+]?\d+)?", RegexOptions.CultureInvariant)]
    private static partial Regex NumberRegex();

    public ParticleFactory()
    {
        _models = new ModelLibrary(false);
        _models.CreateDefaults();
    }

    /// <summary>Creates the effect tree for the selected water state; underwater-only branches are omitted above water.</summary>
    public Node3D Create(ParticleEffectDefinition effect, bool emitting = true, bool underwater = false)
    {
        var root = new Node3D { Name = SafeName(effect.Name) }; BuildTree(root, effect, emitting, underwater); return root;
    }

    public GpuParticles3D CreateEmitter(ParticleEffectDefinition effect, bool emitting = true)
    {
        ArgumentNullException.ThrowIfNull(effect);
        var p = effect.Parameters;
        var life = Math.Max(.01f, p.LifeTime.Base);
        // Count is the live-particle capacity in CryEngine. Preserve an authored zero and bound one
        // malformed emitter before it can consume the entire scene-wide reservation.
        var requested = Math.Clamp((int)MathF.Ceiling(Math.Max(0, Maximum(p.Count))), 0, Math.Max(0, PerEmitterParticleLimit));
        var amount = Reserve(requested);
        var key = ResourceKey(p);
        if (!_shared.TryGetValue(key, out var resources))
        {
            resources = BuildShared(p, life);
            _shared[key] = resources;
        }
        var travel = Math.Abs(Maximum(p.Speed)) * life + Math.Abs(p.GravityScale.Base) * 4.91f * life * life;
        var radius = Math.Max(1f, Maximum(p.Size) * Math.Max(1f, Math.Max(p.SizeX.Base, p.SizeY.Base)) + travel + RandomExtent(p));
        var pulse = Math.Max(0, p.PulsePeriod.Base);
        var visibilityRange = Math.Max(0f, p.Number(DefaultVisibilityRange,
            "MaxViewDist", "MaxViewDistance", "ViewDistance", "ViewDist"));
        var emitter = new GpuParticles3D
        {
            Name = SafeName(effect.Name + " Emitter"), Amount = Math.Max(1, amount), Lifetime = life,
            OneShot = !p.Continuous, Explosiveness = !p.Continuous ? 1 : 0,
            Randomness = Math.Clamp(Math.Abs(p.LifeTime.Random) <= 1 ? Math.Abs(p.LifeTime.Random) : Math.Abs(p.LifeTime.Random) / life, 0, 1),
            LocalCoords = p.LocalSpace, ProcessMaterial = resources.Process, DrawPass1 = resources.DrawMesh,
            VisibilityAabb = new Aabb(Vector3.One * -radius, Vector3.One * radius * 2),
            VisibilityRangeEnd = visibilityRange,
            VisibilityRangeEndMargin = Math.Min(25f, visibilityRange * .1f),
            Emitting = emitting && effect.Enabled && p.SpawnDelay.Base <= 0 && amount > 0
        };
        if(resources.TrailMesh!=null)
        {
            emitter.DrawPasses=2;emitter.DrawPass2=resources.TrailMesh;emitter.TrailEnabled=true;
            emitter.TrailLifetime=Math.Max(p.TailLength,.01f);
        }
        emitter.SetMeta("particle_budget", amount);
        emitter.SetMeta("particle_sprite_extent",resources.FinalSpriteExtent);
        return emitter;
    }

    internal void ReleaseEmitter(GpuParticles3D emitter)
    {
        if (emitter == null || !emitter.HasMeta("particle_budget")) return;
        var n = (int)emitter.GetMeta("particle_budget"); emitter.RemoveMeta("particle_budget");
        lock (BudgetLock) _reservedParticles = Math.Max(0, _reservedParticles - n);
    }

    internal ParticleTrail3D CreateTrail(Node3D target, ParticleParameters p)
    {
        var texture = ResolveTextureAndBlend(p).Asset?.Texture;
        var resolved = ResolveTextureAndBlend(p);
        var additive = resolved.Blend == BaseMaterial3D.BlendModeEnum.Add;
        var decalIntensity = p.Facing.Contains("decal", StringComparison.OrdinalIgnoreCase)
            ? Math.Max(1f, p.Number(1f, "DefereedDecalAlphaMultiply", "DeferredDecalAlphaMultiply"))
            : 1f;
        var color = ParticleColor(ParseColorParameter(p.Color).Base, p.Alpha.Base, additive, decalIntensity, p.EmissiveLighting);
        var trail = new ParticleTrail3D();
        trail.Configure(target, Math.Clamp(p.TailSteps, 2, 64), Math.Max(Maximum(p.Size) * 2f,.04f),
            Math.Max(p.TailLength,.08f), color, texture, additive);
        return trail;
    }

    private SharedResources BuildShared(ParticleParameters p, float life)
    {
        var colors = ParseColorParameter(p.Color);
        var resolved = ResolveTextureAndBlend(p);
        var additive = resolved.Blend == BaseMaterial3D.BlendModeEnum.Add;
        var alpha = Math.Max(0f, p.Alpha.Base);
        // Cry deferred decals can author alpha multipliers above one. Preserve their HDR source
        // contribution in RGB because Godot's alpha channel is normalized to [0, 1].
        var decalIntensity = p.Facing.Contains("decal", StringComparison.OrdinalIgnoreCase)
            ? Math.Max(1f, p.Number(1f, "DefereedDecalAlphaMultiply", "DeferredDecalAlphaMultiply"))
            : 1f;
        var emitterScale = Math.Max(0f, p.EmitterScale.Base * Evaluate(p.EmitterScale.EmitterCurve, 1f, 1f));
        var sizeStrength = Math.Max(0f, Evaluate(p.Size.EmitterCurve, 1f, 1f));
        var stretchStrength=Math.Max(0f,Evaluate(p.Stretch.EmitterCurve,1f,1f));
        var sizeMin=Math.Max(.0001f,Minimum(p.Size)*sizeStrength*emitterScale);var sizeMax=Math.Max(sizeMin,Maximum(p.Size)*sizeStrength*emitterScale);
        var speedMin=Minimum(p.Speed)*emitterScale;var speedMax=Maximum(p.Speed)*emitterScale;
        var sx=Math.Max(.02f,p.Text("SizeX").Length>0?p.SizeX.Base:1);var sy=Math.Max(.02f,p.Text("SizeY").Length>0?p.SizeY.Base:1);
        var sxBound=p.Text("SizeX").Length>0?Math.Max(sx,Maximum(p.SizeX)*Math.Max(0f,Evaluate(p.SizeX.EmitterCurve,1f,1f))*Math.Max(0f,CurveMaximum(p.SizeX.Curve,1f))):sx;
        var syBound=p.Text("SizeY").Length>0?Math.Max(sy,Maximum(p.SizeY)*Math.Max(0f,Evaluate(p.SizeY.EmitterCurve,1f,1f))*Math.Max(0f,CurveMaximum(p.SizeY.Curve,1f))):sy;
        var maxSpeed=Math.Max(Math.Abs(speedMin),Math.Abs(speedMax));var maxStretch=Math.Max(0f,Maximum(p.Stretch)*stretchStrength);
        var stretchWorld=maxSpeed*maxStretch*Math.Max(0f,CurveMaximum(p.Stretch.Curve,1f));
        var sizeCurveMax=Math.Max(0f,CurveMaximum(p.Size.Curve,1f));
        var process = new ParticleProcessMaterial
        {
            Direction = Vector3.Up, Spread = Math.Clamp(p.EmitAngle.Base, 0, 180),
            InitialVelocityMin = speedMin, InitialVelocityMax = speedMax,
            ScaleMin = sizeMin, ScaleMax = sizeMax,
            Gravity = Vector3.Down * 9.80665f * p.GravityScale.Base + CryVector(p.Text("Acceleration")),
            DampingMin = Math.Max(0, Minimum(p.AirResistance)), DampingMax = Math.Max(0, Maximum(p.AirResistance)),
            AngleMin = Minimum(p.Rotation), AngleMax = Maximum(p.Rotation),
            AngularVelocityMin = AxisZ(p.Text("RotationRate", "RotationSpeed"), p.RotationRate.Base) - Math.Abs(AxisZ(p.Text("RandomRotationRate"), p.RotationRate.Random)),
            AngularVelocityMax = AxisZ(p.Text("RotationRate", "RotationSpeed"), p.RotationRate.Base) + Math.Abs(AxisZ(p.Text("RandomRotationRate"), p.RotationRate.Random)),
            ParticleFlagAlignY = p.FacingVelocity || p.Facing.Contains("velocity", StringComparison.OrdinalIgnoreCase),
            Color = ParticleColor(colors.Base, alpha, additive, decalIntensity, p.EmissiveLighting)
        };
        ApplyEmissionVolume(process, p);
        var stretchAspect=1f+stretchWorld/Math.Max(.01f,sizeMax*sy);
        if(stretchWorld>.0001f)
        {
            process.UseScale3D=true;process.Scale3DMin=Vector3.One*sizeMin;process.Scale3DMax=Vector3.One*sizeMax;
            process.ScaleOverVelocityMin=0;process.ScaleOverVelocityMax=Math.Max(.01f,maxSpeed);
            process.ScaleOverVelocityCurve=MakeVelocityStretchCurve(stretchAspect);
            process.ScaleCurve=MakeCurve(p.Size.Curve);
        }
        else if (MakeCurve(p.Size.Curve) is { } scale) process.ScaleCurve = scale;
        if (MakeCurve(p.Alpha.Curve) is { } opacity) process.AlphaCurve = opacity;
        if (colors.Random > 0) process.ColorInitialRamp = RandomColorRamp(process.Color, colors.Random);
        if (MakeColorRamp(p.Color, process.Color, decalIntensity, p.EmissiveLighting) is { } overLife) { process.Color = Colors.White; process.ColorRamp = overLife; }

        Mesh mesh;
        if (p.Geometry.Length > 0)
        {
            var materialOverride = CellPaths.MaterialFile(CellPaths.Normalize(p.Material));
            var meshRef = _models.Request(CellPaths.Normalize(p.Geometry), materialOverride, false);
            mesh = PrepareGeometryMesh(_models.GetMesh(meshRef),p,alpha < .999f || p.Alpha.Curve.Count > 0 || p.Alpha.Random > 0);
        }
        else mesh = null;
        if (mesh == null)
        {
            var (columns, rows, start, count, rate) = Atlas(p);
            if (columns > 1 || rows > 1)
            {
                var frames = columns * rows;
                process.AnimOffsetMin = process.AnimOffsetMax = Math.Clamp((float)start / frames, 0, 1);
                process.AnimSpeedMin = process.AnimSpeedMax = rate > 0 ? rate * life / frames : Math.Clamp((float)count / frames, 0, 1);
            }
            var facing = p.Facing.ToLowerInvariant();
            var focusGravity = FocusGravityBeams && p.Raw.TryGetValue("FocusGravityDir", out var focusText) &&
                               focusText.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
            var material = new StandardMaterial3D
            {
                Transparency = resolved.Opaque ? BaseMaterial3D.TransparencyEnum.Disabled : BaseMaterial3D.TransparencyEnum.Alpha,
                ShadingMode = p.DiffuseLighting > 0 && p.EmissiveLighting <= 0 ? BaseMaterial3D.ShadingModeEnum.PerPixel : BaseMaterial3D.ShadingModeEnum.Unshaded,
                BillboardMode = focusGravity ? BaseMaterial3D.BillboardModeEnum.FixedY
                    : facing.Contains("free") || facing.Contains("decal") || facing.Contains("horizontal") || facing.Contains("bottom")
                    ? BaseMaterial3D.BillboardModeEnum.Disabled : BaseMaterial3D.BillboardModeEnum.Particles,
                VertexColorUseAsAlbedo = true, CullMode = BaseMaterial3D.CullModeEnum.Disabled, AlbedoColor = MaterialTint(p),
                AlbedoTexture = resolved.Asset?.Texture, BlendMode = resolved.Blend,
                ParticlesAnimHFrames = columns, ParticlesAnimVFrames = rows, ParticlesAnimLoop = true,
                ProximityFadeEnabled = p.SoftParticle, ProximityFadeDistance = p.SoftParticle ? Math.Max(.1f, p.Size.Base) : 1f
            };
            // A missing texture/material must not become an opaque white card.
            if (resolved.Asset == null) material.AlbedoColor = new Color(1, 1, 1, 0);
            // CE defines sprite Size as radius; Godot's quad uses full dimensions.
            mesh = new QuadMesh { Size = focusGravity ? new Vector2(sy * 2, sx * 2) : new Vector2(sx * 2, sy * 2), Material = material };
        }
        // A tail traces the particle's path: a particle that never moves (Speed 0, no gravity or acceleration) has none. Rendering one anyway
        // drew a metre-long vertical streak for the lobby's dust motes (Login_particle_blue/trail: Speed 0, TailLength 15).
        var trailMesh=p.TailLength>0&&ParticleMoves(p)?CreateParticleTrailMesh(mesh,p):null;
        var finalSpriteExtent=p.Geometry.Length==0?Math.Max(2f*sxBound*sizeMax*sizeCurveMax,2f*syBound*sizeMax*sizeCurveMax+2f*stretchWorld):0f;
        return new(process, mesh,trailMesh,finalSpriteExtent);
    }

    internal static bool ParticleMoves(ParticleParameters p) =>
        Math.Max(Math.Abs(Minimum(p.Speed)), Math.Abs(Maximum(p.Speed))) > 1e-3f || Math.Abs(p.GravityScale.Base) > 1e-3f ||
        p.Text("Acceleration").Length > 0 || p.Raw.Keys.Any(k => k.StartsWith("Turbulence", StringComparison.OrdinalIgnoreCase));

    private Mesh PrepareGeometryMesh(Mesh source,ParticleParameters p,bool usesAlpha)
    {
        if (source == null) return null;
        // ModelLibrary meshes and their materials are cached globally. Duplicate before enabling particle color so
        // per-emitter tint/alpha multiplies the CGF's own textured MTL without changing world-model instances.
        var mesh = source.Duplicate(true) as Mesh;
        if (mesh == null) return source;
        var textureOverride=LoadTextureAsset(p.Texture)?.Texture;var resolved=ResolveTextureAndBlend(p);
        for (var i = 0; i < mesh.GetSurfaceCount(); i++)
        {
            if (mesh.SurfaceGetMaterial(i)?.Duplicate(true) is not StandardMaterial3D material) continue;
            material.VertexColorUseAsAlbedo = true;
            if(textureOverride!=null)material.AlbedoTexture=textureOverride;
            if(textureOverride!=null||usesAlpha){material.Transparency=resolved.Opaque?BaseMaterial3D.TransparencyEnum.Disabled:BaseMaterial3D.TransparencyEnum.Alpha;material.BlendMode=resolved.Blend;}
            mesh.SurfaceSetMaterial(i, material);
        }
        return mesh;
    }

    private static Mesh CreateParticleTrailMesh(Mesh source,ParticleParameters p)
    {
        if(source==null||source.GetSurfaceCount()==0||source.SurfaceGetMaterial(0)?.Duplicate(true) is not StandardMaterial3D material)return null;
        material.UseParticleTrails=true;material.BillboardMode=BaseMaterial3D.BillboardModeEnum.Disabled;material.CullMode=BaseMaterial3D.CullModeEnum.Disabled;
        var sections=Math.Clamp(p.TailSteps>0?p.TailSteps:8,2,64);
        return new RibbonTrailMesh{Size=2f,Sections=sections,SectionSegments=1,SectionLength=1f/sections,Material=material};
    }
    internal void PrintDebug(ParticleEffectDefinition effect, GpuParticles3D emitter)
    {
        if (!DebugEnabled) return;
        var p = effect.Parameters; var process = emitter.ProcessMaterial as ParticleProcessMaterial;
        var quad = emitter.DrawPass1 as QuadMesh; var emitterCurve=FormatCurve(p.Size.EmitterCurve);var lifeCurve = FormatCurve(p.Size.Curve);var resolved=ResolveTextureAndBlend(p);
        var geometryMaterial=p.Geometry.Length==0?"<none>":CellPaths.MaterialFile(CellPaths.Normalize(p.Material))??"<model-own-mtl>";var geometryTexture=p.Geometry.Length==0?"<none>":ResolveTexturePath(p.Texture)??"<model-own-texture>";
        var geometryExtent=0f;if(quad==null&&emitter.DrawPass1!=null){var extent=emitter.DrawPass1.GetAabb().Size.Abs();geometryExtent=Math.Max(extent.X,Math.Max(extent.Y,extent.Z));}
        var spriteExtent=(float)emitter.GetMeta("particle_sprite_extent",0f);
        var world = emitter.GlobalTransform; var effectiveScale = world.Basis.Scale;
        GD.Print($"[particle-debug] name={effect.Name} count={p.Raw.GetValueOrDefault("Count", "<default 1>")} " +
                 $"life={p.Raw.GetValueOrDefault("ParticleLifeTime", p.Raw.GetValueOrDefault("LifeTime", "<default 1>"))} " +
                 $"continuous={p.Continuous} spawnDelay={p.SpawnDelay.Base:G6} emitterLife={p.EmitterLifeTime.Base:G6} pulse={p.PulsePeriod.Base:G6} " +
                 $"size(base={p.Size.Base:G6},random={p.Size.Random:G6},emitterCurve={emitterCurve},lifeCurve={lifeCurve}) " +
                 $"speed={p.Raw.GetValueOrDefault("Speed", "0")} stretch={p.Raw.GetValueOrDefault("Stretch", "0")} " +
                 $"tail={p.Raw.GetValueOrDefault("TailLength", "0")} emitterScale={p.Raw.GetValueOrDefault("EmitterScale",p.Raw.GetValueOrDefault("Scale","<default 1>"))} " +
                 $"minPixels={p.MinPixels:G6} scaleByDistance={p.ScaleByDistance} blend={p.Blend} facing={p.Facing} " +
                 $"texture={p.Texture} material={p.Material} geometry={p.Geometry} | resolvedGeometryMaterial={geometryMaterial} resolvedGeometryTexture={geometryTexture} " +
                 $"quad={(quad == null ? "<geometry>" : quad.Size.ToString())} amount={emitter.Amount} lifetime={emitter.Lifetime:G6} " +
                 $"velocity=[{process?.InitialVelocityMin:G6},{process?.InitialVelocityMax:G6}] " +
                 $"scale=[{process?.ScaleMin:G6},{process?.ScaleMax:G6}] finalSpriteExtent={spriteExtent:G6} geometryLocalExtent={geometryExtent:G6} geometryWorldExtent={(geometryExtent*(process?.ScaleMax??0)):G6} visibilityRange={emitter.VisibilityRangeEnd:G6} stretchVelocity=[{process?.ScaleOverVelocityMin:G6},{process?.ScaleOverVelocityMax:G6}] " +
                 $"oneShot={emitter.OneShot} particleTrail={emitter.TrailEnabled}:{emitter.TrailLifetime:G6}s blend={resolved.Blend} billboard={(quad?.Material as StandardMaterial3D)?.BillboardMode} local={emitter.LocalCoords} " +
                 $"worldOrigin={world.Origin} effectiveScale={effectiveScale}");
    }

    internal void PrintTrailDebug(ParticleEffectDefinition effect, Node3D node)
    {
        if (!DebugEnabled) return;var p=effect.Parameters;var world=node.GlobalTransform;
        var effectiveWidth=Math.Max(Maximum(p.Size)*2f,.04f);var effectiveSeconds=Math.Max(p.TailLength,.08f);var effectiveSteps=Math.Clamp(p.TailSteps,2,64);
        GD.Print($"[particle-debug] name={effect.Name} count={p.Raw.GetValueOrDefault("Count","<default 1>")} " +
                 $"life={p.Raw.GetValueOrDefault("ParticleLifeTime","<default 1>")} size(base={p.Size.Base:G6},random={p.Size.Random:G6},emitterCurve={FormatCurve(p.Size.EmitterCurve)},lifeCurve={FormatCurve(p.Size.Curve)}) " +
                 $"continuous={p.Continuous} spawnDelay={p.SpawnDelay.Base:G6} emitterLife={p.EmitterLifeTime.Base:G6} pulse={p.PulsePeriod.Base:G6} " +
                 $"speed={p.Raw.GetValueOrDefault("Speed","0")} stretch={p.Raw.GetValueOrDefault("Stretch","0")} tail={p.Raw.GetValueOrDefault("TailLength","0")} " +
                 $"blend={p.Blend} facing={p.Facing} texture={p.Texture} material={p.Material} geometry={p.Geometry} | " +
                 $"output=emitter-path-ribbon quad=<ribbon> amount=1 lifetime={effectiveSeconds:G6} velocity=<moving-track> " +
                 $"authoredWidth={(Maximum(p.Size)*2f):G6} effectiveWidth={effectiveWidth:G6} authoredTail={p.TailLength:G6} effectiveTail={effectiveSeconds:G6} authoredSteps={p.TailSteps} effectiveSteps={effectiveSteps} " +
                 $"worldOrigin={world.Origin} effectiveScale={world.Basis.Scale}");
    }

    private static string FormatCurve(IReadOnlyList<ParticleCurveKey> keys) => keys == null || keys.Count == 0
        ? "<none>" : string.Join(';', keys.Select(k => $"{k.Time:G4}:{k.Value:G4}"));

    private (TextureAsset Asset, BaseMaterial3D.BlendModeEnum Blend, bool Opaque) ResolveTextureAndBlend(ParticleParameters p)
    {
        var material = p.Material.Length == 0 ? null : _models.Mtl(p.Material)?.ForSubset(0);
        // CE particle Material overrides Texture (notably Materials/effects/fire4).
        var reference = material?.DiffuseMap?.PakPath ?? p.Texture;
        var asset = LoadTextureAsset(reference);
        var blend = p.Blend;
        BaseMaterial3D.BlendModeEnum mode;
        var opaque = false;
        if (blend.Length > 0)
        {
            // An explicit CE BlendType is authoritative, including Opaque for RGB-only textures.
            if (blend.Contains("opaque", StringComparison.OrdinalIgnoreCase)) { mode = BaseMaterial3D.BlendModeEnum.Mix; opaque = true; }
            else if (blend.Contains("add", StringComparison.OrdinalIgnoreCase) || blend.Contains("colorbased", StringComparison.OrdinalIgnoreCase)) mode = BaseMaterial3D.BlendModeEnum.Add;
            else if (blend.Contains("multip", StringComparison.OrdinalIgnoreCase)) mode = BaseMaterial3D.BlendModeEnum.Mul;
            else if (blend.Contains("subtract", StringComparison.OrdinalIgnoreCase)) mode = BaseMaterial3D.BlendModeEnum.Sub;
            else mode = BaseMaterial3D.BlendModeEnum.Mix; // AlphaBased and unknown explicit modes do not use DDS inference.
        }
        else if (p.Boolean(false, "Additive") || material?.IsAdditive == true) mode = BaseMaterial3D.BlendModeEnum.Add;
        else mode = asset is { HasAlpha: false } ? BaseMaterial3D.BlendModeEnum.Add : BaseMaterial3D.BlendModeEnum.Mix;
        return (asset, mode, opaque);
    }
    private Color MaterialTint(ParticleParameters p)
    {
        var diffuse = p.Material.Length == 0 ? null : _models.Mtl(p.Material)?.ForSubset(0);
        if (diffuse == null) return Colors.White;
        var c = diffuse.DiffuseColor;
        return new Color(c.X, c.Y, c.Z);
    }
    private static string ResolveTexturePath(string reference)=>string.IsNullOrWhiteSpace(reference)?null:TextureCandidates(reference).FirstOrDefault(PakFiles.Exists);

    public Texture2D LoadTexture(string reference) => LoadTextureAsset(reference)?.Texture;
    private TextureAsset LoadTextureAsset(string reference)
    {
        if (string.IsNullOrWhiteSpace(reference)) return null;
        foreach (var path in TextureCandidates(reference))
        {
            if (_textures.TryGetValue(path, out var cached)) return cached;
            var bytes = PakFiles.Read(path); if (bytes == null) continue;
            try
            {
                var prepared = CryDds.PrepareForGodot(bytes);
                var image = new Image(); if (image.LoadDdsFromBuffer(prepared) != Error.Ok || image.IsEmpty()) continue;
                // DDS_PIXELFORMAT.dwFlags is at byte 80 in a standard DDS file; bit 0 is DDPF_ALPHAPIXELS.
                // DXT1 only has 1-bit alpha when that metadata bit is present.
                var ddsAlphaPixels = prepared.Length >= 84 && (BitConverter.ToUInt32(prepared, 80) & 0x1u) != 0;
                var hasAlpha = image.GetFormat() is Image.Format.Dxt3 or Image.Format.Dxt5 or Image.Format.Rgba8 or Image.Format.Rgba4444 ||
                               image.GetFormat() == Image.Format.Dxt1 && ddsAlphaPixels;
                var asset = new TextureAsset(ImageTexture.CreateFromImage(image), hasAlpha); _textures[path] = asset; return asset;
            }
            catch (Exception e) { GD.PrintErr($"Particle texture '{path}' could not be loaded: {e.Message}"); }
        }
        return null;
    }

    private void BuildTree(Node3D root, ParticleEffectDefinition effect, bool emitting, bool underwater)
    {
        // Disabled parents gate their complete authored subtree in CryEngine. The water flag likewise
        // applies at the node level so an excluded branch cannot leak visible descendants.
        if (!effect.Enabled || !IsVisibleInWater(effect.Parameters.Text("VisibleUnderwater"), underwater)) return;
        var p=effect.Parameters;var hasVisual=p.Texture.Length>0||p.Material.Length>0||p.Geometry.Length>0;
        if(hasVisual){var emitter=CreateEmitter(effect,emitting);root.AddChild(emitter);PrintDebug(effect,emitter);}
        foreach (var c in effect.Children) { var n = new Node3D { Name = SafeName(c.Name) }; root.AddChild(n); BuildTree(n, c, emitting, underwater); }
    }

    internal static bool IsVisibleInWater(string condition, bool underwater)
    {
        if (condition.Contains("If_True", StringComparison.OrdinalIgnoreCase)) return underwater;
        if (condition.Contains("If_False", StringComparison.OrdinalIgnoreCase)) return !underwater;
        return true;
    }
    private static int Reserve(int requested) { lock (BudgetLock) { var n = Math.Clamp(requested, 0, Math.Max(0, GlobalParticleLimit - _reservedParticles)); _reservedParticles += n; return n; } }
    private static string ResourceKey(ParticleParameters p) { var b = new StringBuilder(); foreach (var x in p.Raw.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)) b.Append(x.Key).Append('=').Append(x.Value).Append(';'); return b.ToString(); }
    private static IEnumerable<string> TextureCandidates(string reference)
    {
        var p = reference.Trim().Trim('"').Replace('\\', '/').Replace("%ENGINE%/", "", StringComparison.OrdinalIgnoreCase).TrimStart('/');
        var ext = Path.GetExtension(p); if (ext.Equals(".tif", StringComparison.OrdinalIgnoreCase) || ext.Equals(".tiff", StringComparison.OrdinalIgnoreCase)) p = Path.ChangeExtension(p, ".dds").Replace('\\', '/'); else if (ext.Length == 0) p += ".dds";
        p = PakFiles.Normalize(p); yield return p; if (!p.StartsWith("game/")) yield return "game/" + p; if (!p.StartsWith("game/textures/") && !p.StartsWith("textures/")) yield return "game/textures/" + p;
    }
    private static CurveTexture MakeCurve(IReadOnlyList<ParticleCurveKey> keys)
    {
        if (keys == null || keys.Count == 0) return null; var c = new Curve { MinValue = 0, MaxValue = Math.Max(1, keys.Max(k => k.Value)) };
        foreach (var k in keys) c.AddPoint(new Vector2(Math.Clamp(k.Time, 0, 1), Math.Max(0, k.Value)), 0, 0, Curve.TangentMode.Linear, Curve.TangentMode.Linear); return new CurveTexture { Curve = c };
    }
    private static CurveXyzTexture MakeVelocityStretchCurve(float aspect)
    {
        var one=CurveFrom(new[]{(0f,1f),(1f,1f)});var y=CurveFrom(new[]{(0f,1f),(1f,Math.Max(1f,aspect))});
        return new CurveXyzTexture{CurveX=one,CurveY=y,CurveZ=(Curve)one.Duplicate()};
    }
    private static Curve CurveFrom(IEnumerable<(float Time,float Value)> points)
    {
        var a=points.ToArray();var c=new Curve{MinValue=0,MaxValue=Math.Max(1,a.Length==0?1:a.Max(x=>x.Value))};foreach(var p in a)c.AddPoint(new Vector2(p.Time,Math.Max(0,p.Value)),0,0,Curve.TangentMode.Linear,Curve.TangentMode.Linear);return c;
    }
    private Color ParticleColor(Color color, float authoredAlpha, bool additive, float decalIntensity = 1f, float emissiveLighting = 0f)
    {
        var alpha=Math.Max(0f,authoredAlpha);var brightness=(additive?Math.Max(1f,alpha):1f)*decalIntensity*EmissiveScale(emissiveLighting);
        return new Color(color.R*brightness,color.G*brightness,color.B*brightness,color.A*Math.Min(alpha,1f));
    }
    private static GradientTexture1D RandomColorRamp(Color c, float random)
    {
        var g = new Gradient(); var d = Math.Clamp(random, 0, 1); g.SetColor(0, new Color(c.R * (1-d), c.G * (1-d), c.B * (1-d), c.A)); g.SetColor(1, new Color(c.R*(1+d), c.G*(1+d), c.B*(1+d), c.A)); return new() { Gradient = g };
    }
    private GradientTexture1D MakeColorRamp(string text, Color fallback, float decalIntensity = 1f, float emissiveLighting = 0f)
    {
        var fields = SplitTopLevel(text, ','); if (fields.Count < 4 || fields[3].Length == 0) return null;
        var curveText = StripOuterPair(fields[3]); var g = new Gradient(); var count = 0;
        foreach (var group in SplitTopLevel(curveText, ';').Where(x => x.Length > 0))
        {
            var key = SplitTopLevel(group, ','); if (key.Count < 2) continue;
            var time = key[0].Length == 0 ? 0f : TryFloat(key[0], out var parsedTime) ? parsedTime : float.NaN;
            if (!float.IsFinite(time)) continue;
            var color = ParseColorVector(key[1].StartsWith('(') ? key[1] : string.Join(",", key.Skip(1)), fallback.A);
            color = ScaleRgb(Normalize(color), Math.Max(1f, decalIntensity) * EmissiveScale(emissiveLighting));
            if (count < 2) { g.SetOffset(count, Math.Clamp(time,0,1)); g.SetColor(count,color); } else g.AddPoint(Math.Clamp(time,0,1),color); count++;
        }
        return count == 0 ? null : new() { Gradient = g };
    }

    private static List<string> SplitTopLevel(string text, char separator)
    {
        var result = new List<string>(); if (string.IsNullOrEmpty(text)) return result;
        var depth = 0; var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '(') depth++;
            else if (text[i] == ')') depth = Math.Max(0, depth - 1);
            else if (text[i] == separator && depth == 0) { result.Add(text[start..i].Trim()); start = i + 1; }
        }
        result.Add(text[start..].Trim()); return result;
    }

    private static string StripOuterPair(string text)
    {
        text = text.Trim(); if (text.Length < 2 || text[0] != '(' || text[^1] != ')') return text;
        var depth = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '(') depth++;
            else if (text[i] == ')' && --depth == 0 && i != text.Length - 1) return text;
        }
        return text[1..^1];
    }

    private static bool TryFloat(string text, out float value) => float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    private static Color ParseColorVector(string text, float alpha)
    {
        var components = StripOuterPair(text).Split(',', StringSplitOptions.None);
        // An empty component keeps the parameter's default, which for a colour channel is 1: "(,0.29,0.118)" is the orange (1, 0.29, 0.118),
        // not the dark green (0, 0.29, 0.118).
        float Component(int index) => index >= components.Length ? 0f
            : components[index].Trim().Length == 0 ? 1f : TryFloat(components[index], out var value) ? value : 0f;
        return new Color(Component(0), Component(1), Component(2), alpha);
    }
    private static Color ScaleRgb(Color color, float factor) => new(color.R * factor, color.G * factor, color.B * factor, color.A);
    private static (Color Base, float Random) ParseColorParameter(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return (Colors.White, 0);
        var fields = SplitTopLevel(text, ','); if (fields.Count < 1) return (Colors.White, 0);
        var color = Colors.White;
        if (fields[0].Length > 0)
        {
            color = Normalize(ParseColorVector(fields[0], 1));
        }
        var random = fields.Count > 1 && TryFloat(fields[1], out var r) ? Math.Abs(r) : 0f;
        return (color, random);
    }
    private static Color Normalize(Color c) => Math.Max(c.R,Math.Max(c.G,c.B))>16 ? new(c.R/255f,c.G/255f,c.B/255f,c.A) : c;
    private static (int C,int R,int Start,int Count,float Rate) Atlas(ParticleParameters p)
    {
        var c=Math.Max(1,p.AtlasColumns);var r=Math.Max(1,p.AtlasRows);var start=0;var count=0;var rate=p.AtlasFrameRate;
        if(p.Raw.TryGetValue("TextureTiling",out var s)){var a=s.Split(',',StringSplitOptions.TrimEntries);if(a.Length>0&&ParticleLibraryReader.TryFloat(a[0],out var x))c=Math.Max(1,(int)x);if(a.Length>1&&ParticleLibraryReader.TryFloat(a[1],out var y))r=Math.Max(1,(int)y);if(a.Length>2&&ParticleLibraryReader.TryFloat(a[2],out var st))start=Math.Max(0,(int)st);if(a.Length>4&&ParticleLibraryReader.TryFloat(a[4],out var co))count=Math.Max(1,(int)co);else if(a.Length>3&&ParticleLibraryReader.TryFloat(a[3],out co))count=Math.Max(1,(int)co);if(a.Length>5&&ParticleLibraryReader.TryFloat(a[5],out var fps))rate=Math.Max(0,fps);} if(count==0)count=Math.Max(1,c*r-start);return(c,r,start,count,rate);
    }
    private static void ApplyEmissionVolume(ParticleProcessMaterial m, ParticleParameters p)
    {
        var e=ParseExtents(p.EmitterSize);var s=p.EmitterShape.ToLowerInvariant();if(s.Contains("box")||s.Contains("cube")){m.EmissionShape=ParticleProcessMaterial.EmissionShapeEnum.Box;m.EmissionBoxExtents=e;return;}if(s.Contains("ring")||s.Contains("disc")){m.EmissionShape=ParticleProcessMaterial.EmissionShapeEnum.Ring;m.EmissionRingAxis=Vector3.Up;m.EmissionRingRadius=Math.Max(e.X,e.Z);m.EmissionRingHeight=e.Y*2;return;}if(s.Contains("sphere")||s.Contains("ball")){m.EmissionShape=s.Contains("surface")?ParticleProcessMaterial.EmissionShapeEnum.SphereSurface:ParticleProcessMaterial.EmissionShapeEnum.Sphere;m.EmissionSphereRadius=Math.Max(e.X,Math.Max(e.Y,e.Z));return;}var random=ParseExtents(p.Text("RandomOffset","PositionRandomOffset"));if(random.LengthSquared()>0){m.EmissionShape=ParticleProcessMaterial.EmissionShapeEnum.Box;m.EmissionBoxExtents=random;return;}var radius=p.Number(0,"PosRandomOffset");if(radius>0){m.EmissionShape=ParticleProcessMaterial.EmissionShapeEnum.Sphere;m.EmissionSphereRadius=radius;}
    }
    private static Vector3 ParseExtents(string s){var a=(s??"").Split(',',StringSplitOptions.TrimEntries);float V(int i,float d=0)=>i<a.Length&&ParticleLibraryReader.TryFloat(a[i],out var v)?Math.Abs(v):d;var x=V(0);return new(x,V(2,x),V(1,x));}
    private static Vector3 CryVector(string s){var a=(s??"").Split(',',StringSplitOptions.TrimEntries);float V(int i)=>i<a.Length&&ParticleLibraryReader.TryFloat(a[i],out var v)?v:0;return new(V(0),V(2),-V(1));}
    private static float AxisZ(string s,float fallback){var a=(s??"").Split(',',StringSplitOptions.TrimEntries);return a.Length>2&&ParticleLibraryReader.TryFloat(a[2],out var v)?v:fallback;}
    private static float Minimum(ParticleValue v)=>v.Minimum;private static float Maximum(ParticleValue v)=>v.Maximum;
    private static float Evaluate(IReadOnlyList<ParticleCurveKey> keys,float time,float fallback)
    {
        if(keys==null||keys.Count==0)return fallback;if(time<=keys[0].Time)return keys[0].Value;
        for(var i=1;i<keys.Count;i++)if(time<=keys[i].Time){var a=keys[i-1];var b=keys[i];var span=Math.Max(.0001f,b.Time-a.Time);return Mathf.Lerp(a.Value,b.Value,(time-a.Time)/span);}return keys[^1].Value;
    }
    private static float CurveMaximum(IReadOnlyList<ParticleCurveKey> keys,float fallback)=>keys==null||keys.Count==0?fallback:keys.Max(k=>k.Value);
    private static float RandomExtent(ParticleParameters p)=>ParseExtents(p.Text("RandomOffset","PositionRandomOffset")).Length()+p.Number(0,"PosRandomOffset");
    private static float ParseFloat(string s)=>float.TryParse(s,NumberStyles.Float,CultureInfo.InvariantCulture,out var v)?v:0;
    private static string SafeName(string s)=>string.IsNullOrWhiteSpace(s)?"Particle":s.Replace('/','_').Replace(':','_');
}

/// <summary>Emitter-path ribbon approximation for CE TailLength/TailSteps and attached sword trails.</summary>
internal sealed partial class ParticleTrail3D : MeshInstance3D
{
    private Node3D _target = null!; private int _steps; private float _width, _seconds; private readonly List<(Vector3 P,float Age)> _points=[]; private float _sample;
    public void Configure(Node3D target,int steps,float width,float seconds,Color color,Texture2D texture,bool additive)
    {
        _target=target;_steps=steps;_width=width;_seconds=seconds;TopLevel=true;CastShadow=GeometryInstance3D.ShadowCastingSetting.Off;
        MaterialOverride=new StandardMaterial3D{ShadingMode=BaseMaterial3D.ShadingModeEnum.Unshaded,Transparency=BaseMaterial3D.TransparencyEnum.Alpha,BlendMode=additive?BaseMaterial3D.BlendModeEnum.Add:BaseMaterial3D.BlendModeEnum.Mix,AlbedoColor=color,AlbedoTexture=texture,CullMode=BaseMaterial3D.CullModeEnum.Disabled,VertexColorUseAsAlbedo=true};
    }
    public override void _Process(double delta)
    {
        if(!GodotObject.IsInstanceValid(_target))return;var d=(float)delta;for(var i=0;i<_points.Count;i++)_points[i]=(_points[i].P,_points[i].Age+d);_points.RemoveAll(x=>x.Age>_seconds);
        _sample+=d;if(_sample>=_seconds/_steps||_points.Count==0){_sample=0;_points.Insert(0,(_target.GlobalPosition,0));if(_points.Count>_steps)_points.RemoveAt(_points.Count-1);}Rebuild();
    }
    private void Rebuild()
    {
        if(_points.Count<2){Mesh=null;return;}var vertices=new Vector3[_points.Count*2];var colors=new Color[vertices.Length];var uvs=new Vector2[vertices.Length];var indices=new int[(_points.Count-1)*6];
        for(var i=0;i<_points.Count;i++){var fade=1-_points[i].Age/_seconds;var tangent=i+1<_points.Count?_points[i].P-_points[i+1].P:_points[i-1].P-_points[i].P;var side=tangent.Cross(Vector3.Forward).Normalized();if(side.LengthSquared()<.01f)side=Vector3.Up;vertices[i*2]=_points[i].P+side*_width*.5f;vertices[i*2+1]=_points[i].P-side*_width*.5f;colors[i*2]=colors[i*2+1]=new Color(1,1,1,fade);var vCoord=(float)i/(_points.Count-1);uvs[i*2]=new Vector2(0,vCoord);uvs[i*2+1]=new Vector2(1,vCoord);if(i+1<_points.Count){var o=i*6;var v=i*2;indices[o]=v;indices[o+1]=v+1;indices[o+2]=v+2;indices[o+3]=v+1;indices[o+4]=v+3;indices[o+5]=v+2;}}
        var a=new Godot.Collections.Array();a.Resize((int)Godot.Mesh.ArrayType.Max);a[(int)Godot.Mesh.ArrayType.Vertex]=vertices;a[(int)Godot.Mesh.ArrayType.Color]=colors;a[(int)Godot.Mesh.ArrayType.TexUV]=uvs;a[(int)Godot.Mesh.ArrayType.Index]=indices;var mesh=new ArrayMesh();mesh.AddSurfaceFromArrays(Godot.Mesh.PrimitiveType.Triangles,a);Mesh=mesh;
    }
}
