using Godot;

namespace AAEmu.GodotViewer;

/// <summary>Character surfaces: skin colour and face decals (<see cref="CharacterLooks"/>) on top of the plain .mtl materials.</summary>
internal sealed partial class ModelLibrary
{
    /// <summary>
    /// Any worker thread. Material key for one surface of a skinned character part: HumanSkin sub-materials get the
    /// skin shader with the unit's skin colour, and the face's skin and eye sub-materials get the face texture with the
    /// unit's decals painted in. Everything else (and any failure) is the plain <see cref="RequestMaterial(string, int, bool)"/>.
    /// Materials are cached per look, so units that look alike share them.
    /// </summary>
    public string RequestCharacterMaterial(CharacterAssets assets, CharacterPart part, string mtlPath, int subIndex)
    {
        var baseKey = RequestMaterial(mtlPath, subIndex);
        try
        {
            return CharacterMaterial(assets, part, mtlPath, subIndex, baseKey) ?? baseKey;
        }
        catch (Exception e)
        {
            GD.PrintErr($"{mtlPath}#{subIndex}: character material failed: {e.Message}");
            return baseKey;
        }
    }

    private string CharacterMaterial(CharacterAssets assets, CharacterPart part, string mtlPath, int subIndex, string baseKey)
    {
        var mtl = Mtl(mtlPath);
        var sub = mtl?.ForSubset(subIndex);
        var skin = CharacterLooks.IsHumanSkin(sub);
        var eye = CharacterLooks.IsEye(sub);
        if (GetMaterial(baseKey) is not StandardMaterial3D source)
            return null;
        if (CharacterHair.IsHair(sub))
            return HairMaterial(assets, part, sub, source, baseKey);
        if (!skin && !eye)
        {
            // Eyelashes (the face's alpha-tested Illum sub-material). Their texture also holds a soft shadow over the
            // eyeball: the client blends it (Opacity 0.99, the alpha test only drops the nearly clear texels), a
            // scissor makes it an opaque grey lid over the eyes (warborn female) or a thick line. The default
            // specular also showed the sky on them as a blue rim.
            if (part.Slot != "face" && CharacterIllum.IsIllum(sub))
                return IllumMaterial(sub, source, baseKey);
            if (part.Slot != "face" || sub == null || !sub.IsAlphaTested)
                return null;
            return AddMaterial($"nospec|{baseKey}", () =>
            {
                RenderBudget.Acquire();
                var m = (StandardMaterial3D)source.Duplicate();
                m.MetallicSpecular = 0f;
                m.Roughness = 1f;
                m.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
                return m;
            });
        }

        // The face textures with the decals painted in: skin decals on the (first) HumanSkin sub-material's texture,
        // pupils on the Eye sub-material's texture. Most faces use one texture for both; some use two (hariharan faces
        // borrow nuian eyes). A missing eye texture (el_f_face00.mtl names el_f_face00_df2.dds) means the skin's.
        string faceKey = null;
        if (part.Slot == "face")
        {
            var skinPath = mtl.SubMaterials.FirstOrDefault(CharacterLooks.IsHumanSkin)?.DiffuseMap?.PakPath;
            var eyePath = mtl.SubMaterials.FirstOrDefault(CharacterLooks.IsEye)?.DiffuseMap?.PakPath;
            if (eyePath != null && !PakFiles.Exists(eyePath))
                eyePath = skinPath;
            var shared = eyePath == null || eyePath == skinPath;
            if (skin && skinPath != null && sub.DiffuseMap?.PakPath == skinPath)
                faceKey = RequestFaceTexture(assets, skinPath, true, shared);
            else if (eye && eyePath != null)
                faceKey = RequestFaceTexture(assets, eyePath, shared, true);
        }
        var albedo = faceKey != null ? GetTexture(faceKey) : source.AlbedoTexture;
        if (eye)
        {
            if (albedo == null)
                return null;
            var irisMask = sub.SpecularMap is { } irisMap ? GetTexture(RequestTexture(irisMap.PakPath, false)) : null;
            var eyeScale = new Vector2(source.Uv1Scale.X, source.Uv1Scale.Y);
            var eyeOffset = new Vector2(source.Uv1Offset.X, source.Uv1Offset.Y);
            return AddMaterial($"eye|{baseKey}|{faceKey ?? ""}", () =>
            {
                RenderBudget.Acquire();
                return CharacterLooks.CreateEyeMaterial(sub, albedo, irisMask, eyeScale, eyeOffset);
            });
        }
        if (albedo == null)
            return null;

        // Normal map: the appearance's face/body normal map replaces the material's (face_normal_maps / body_normal_maps).
        var normal = source.NormalEnabled ? source.NormalTexture : null;
        var normalId = normal != null ? sub.NormalMap?.PakPath : "";
        var normalDepth = 1f;
        var custom = part.Slot == "face" ? assets.FaceNormalMap : part.Slot == "body" ? assets.BodyNormalMap : "";
        if (normal != null && custom.Length > 0 && GetTexture(RequestTexture(custom, false)) is { } customNormal)
        {
            normal = customNormal;
            normalId = custom;
            if (part.Slot == "face")
                normalDepth = 0.5f + 0.5f * Math.Clamp(assets.FaceNormalMapWeight, 0f, 1f);
        }
        var glossFromAlpha = CharacterLooks.GlossFromAlpha(sub);
        var specular = sub.SpecularMap is { } sp && glossFromAlpha != null ? GetTexture(RequestTexture(sp.PakPath, glossFromAlpha.Value)) : null;
        var parameters = CharacterLooks.Skin(sub, assets.SkinColor);
        var twoSided = source.CullMode == BaseMaterial3D.CullModeEnum.Disabled;
        var scissor = source.Transparency == BaseMaterial3D.TransparencyEnum.AlphaScissor ? source.AlphaScissorThreshold : 0f;
        var uvScale = new Vector2(source.Uv1Scale.X, source.Uv1Scale.Y);
        var uvOffset = new Vector2(source.Uv1Offset.X, source.Uv1Offset.Y);
        var key = $"skin|{baseKey}|{faceKey ?? ""}|{parameters.Key}|{normalId}|{normalDepth:F2}";
        return AddMaterial(key, () =>
        {
            RenderBudget.Acquire();
            return CharacterLooks.CreateSkinMaterial(parameters, albedo, uvScale, uvOffset, normal, normalDepth, specular,
                glossFromAlpha == true, twoSided, scissor);
        });
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<Cubemap>> _cubemaps = new();

    /// <summary>Any worker thread. A cube map .dds (faces +X -X +Y -Y +Z -Z, as D3D stores them) as a Godot cube map, or null.</summary>
    private Cubemap RequestCubemap(string pakPath) =>
        _cubemaps.GetOrAdd(pakPath, p => new Lazy<Cubemap>(() =>
        {
            try
            {
                var bytes = PakFiles.Read(p);
                if (bytes == null)
                    return null;
                var info = CryDds.ReadInfo(bytes);
                if (!info.IsCubemap || info.DataSize % 6 != 0)
                    return null;
                var face = info.DataSize / 6;
                var images = new Godot.Collections.Array<Image>();
                for (var i = 0; i < 6; i++)
                {
                    var single = new byte[128 + face];
                    Buffer.BlockCopy(bytes, 0, single, 0, 128);
                    Buffer.BlockCopy(bytes, 128 + i * face, single, 128, face);
                    var image = new Image();
                    if (image.LoadDdsFromBuffer(CryDds.PrepareForGodot(single)) != Error.Ok || image.IsEmpty())
                        return null;
                    images.Add(image);
                }
                RenderBudget.Acquire();
                var cube = new Cubemap();
                return cube.CreateFromImages(images) == Error.Ok ? cube : null;
            }
            catch (Exception e)
            {
                GD.PrintErr($"{p}: cube map failed: {e.Message}");
                return null;
            }
        })).Value;

    /// <summary>Equipment sub-material: the client's Illum shader with its gloss map and environment reflection (see <see cref="CharacterIllum"/>).</summary>
    private string IllumMaterial(MtlMaterial sub, StandardMaterial3D source, string baseKey)
    {
        if (source.AlbedoTexture == null)
            return null;
        var parameters = CharacterIllum.Read(sub);
        var gloss = parameters.HasGloss ? GetTexture(RequestTexture(sub.SpecularMap.PakPath, false)) : null;
        var normal = source.NormalEnabled ? source.NormalTexture : null;
        var cube = parameters.HasEnv ? RequestCubemap(sub.Texture("Environment").PakPath) : null;
        var twoSided = source.CullMode == BaseMaterial3D.CullModeEnum.Disabled;
        var scissor = source.Transparency == BaseMaterial3D.TransparencyEnum.AlphaScissor ? source.AlphaScissorThreshold : 0f;
        var blend = source.Transparency == BaseMaterial3D.TransparencyEnum.Alpha;
        var uvScale = new Vector2(source.Uv1Scale.X, source.Uv1Scale.Y);
        var uvOffset = new Vector2(source.Uv1Offset.X, source.Uv1Offset.Y);
        var opacity = source.AlbedoColor.A;
        return AddMaterial($"illum|{baseKey}|{parameters.Key}|{(cube != null ? sub.Texture("Environment").PakPath : "")}", () =>
        {
            RenderBudget.Acquire();
            var material = CharacterIllum.Create(parameters, source.AlbedoTexture, gloss, normal, cube, uvScale, uvOffset, twoSided, scissor, blend);
            if (blend)
                material.SetShaderParameter("opacity", opacity);
            return material;
        });
    }

    /// <summary>Hair sub-material: the client's Hair shader with the unit's palette colours (see <see cref="CharacterHair"/>).</summary>
    private string HairMaterial(CharacterAssets assets, CharacterPart part, MtlMaterial sub, StandardMaterial3D source, string baseKey)
    {
        if (source.AlbedoTexture == null)
            return null;
        var parameters = CharacterHair.Read(sub, assets, part.Slot == "hair");
        var mask = sub.Texture("SubSurface") is { } maskMap ? GetTexture(RequestTexture(maskMap.PakPath, false)) : null;
        var gloss = sub.SpecularMap is { } glossMap ? GetTexture(RequestTexture(glossMap.PakPath, false)) : null;
        var normal = source.NormalEnabled ? source.NormalTexture : null;
        var uvScale = new Vector2(source.Uv1Scale.X, source.Uv1Scale.Y);
        var uvOffset = new Vector2(source.Uv1Offset.X, source.Uv1Offset.Y);
        var alphaTest = sub.AlphaTest;
        return AddMaterial($"hair|{baseKey}|{parameters.Key}", () =>
        {
            RenderBudget.Acquire();
            return CharacterHair.Create(parameters, source.AlbedoTexture, mask, gloss, normal, uvScale, uvOffset, true, alphaTest);
        });
    }

    /// <summary>Texture key of the unit's composited face texture, or null when it has no decals (or can't be made).</summary>
    private string RequestFaceTexture(CharacterAssets assets, string facePath, bool skinLayers, bool eyeLayers)
    {
        var mask = skinLayers ? CharacterLooks.FaceMaskPath(facePath) : "";
        var key = CharacterLooks.FaceKey(facePath, assets, mask, skinLayers, eyeLayers);
        if (key.Length == 0)
            return null;
        return _textureKeys.GetOrAdd(key, k => new Lazy<string>(() =>
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var image = CharacterLooks.ComposeFace(facePath, assets, mask, skinLayers, eyeLayers);
            if (image == null)
                return null;
            var composeMs = watch.Elapsed.TotalMilliseconds;
            // GV_FACE_DUMP=<folder>: writes every composited face texture as PNG (diagnostics).
            if (System.Environment.GetEnvironmentVariable("GV_FACE_DUMP") is { Length: > 0 } dump)
            {
                var copy = (Image)image.Duplicate();
                copy.Decompress();
                copy.ClearMipmaps();
                copy.SavePng(System.IO.Path.Combine(dump, $"face_{(uint)k.GetHashCode():X8}.png"));
                GD.Print($"face composite {(uint)k.GetHashCode():X8} in {composeMs:F1} ms ({image.GetFormat()}): {k}");
            }
            RenderBudget.Acquire();
            _textures[k] = ImageTexture.CreateFromImage(image);
            return k;
        })).Value;
    }

    /// <summary>Registers a material made by <paramref name="create"/> under <paramref name="key"/> once; null when it made none.</summary>
    private string AddMaterial(string key, Func<Material> create) =>
        _materialKeys.GetOrAdd(key, k => new Lazy<string>(() =>
        {
            var m = create();
            if (m == null)
                return null;
            _materials[k] = m;
            return k;
        })).Value;
}
