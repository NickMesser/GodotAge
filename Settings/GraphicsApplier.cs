#nullable enable
using Godot;

namespace AAEmu.GodotViewer.Settings;

public readonly record struct StreamingGraphicsOptions(int NearRadius, int FarRadius, int ViewDistanceMetres, int TextureMipBias);

/// <summary>Applies gameplay-neutral X2 graphics options to a Godot viewport/window/environment.</summary>
public sealed class GraphicsApplier
{
    private readonly Window _window;
    private readonly Viewport _viewport;
    private readonly Godot.Environment? _environment;
    private readonly DirectionalLight3D? _sun;

    public GraphicsApplier(Window window, Viewport viewport, Godot.Environment? environment = null, DirectionalLight3D? sun = null)
        => (_window, _viewport, _environment, _sun) = (window, viewport, environment, sun);

    public StreamingGraphicsOptions Apply(OptionStore options)
    {
        _window.Size = new Vector2I(options.Get<int>("OIT_R_DESIREWIDTH"), options.Get<int>("OIT_R_DESIREHEIGHT"));
        _window.Mode = options.Get<int>("OIT_R_FULLSCREEN") switch
        {
            1 => Window.ModeEnum.Fullscreen, 2 => Window.ModeEnum.ExclusiveFullscreen, _ => Window.ModeEnum.Windowed
        };
        DisplayServer.WindowSetVsyncMode(options.Get<bool>("OIT_R_VSYNC")
            ? DisplayServer.VSyncMode.Enabled : DisplayServer.VSyncMode.Disabled, _window.GetWindowId());
        Engine.MaxFps = options.Get<bool>("OIT_SYS_USE_LIMIT_FPS") ? options.Get<int>("OIT_SYS_MAX_FPS") : 0;
        _viewport.Scaling3DScale = options.Get<float>("VIEWER_RENDER_SCALE");

        var aa = options.Get<int>("OIT_OPTION_ANTI_ALIASING");
        _viewport.Msaa3D = aa switch
        {
            5 or 12 => Viewport.Msaa.Msaa2X, 6 or 8 or 10 or 13 => Viewport.Msaa.Msaa4X,
            7 or 9 or 11 => Viewport.Msaa.Msaa8X, _ => Viewport.Msaa.Disabled
        };
        // Cry's choices 2/3 are FXAA, 4 is post-AA, and 12/13 are TXAA.
        // Godot has FXAA and TAA but no TXAA or Cry PostAA equivalent.
        _viewport.ScreenSpaceAA = aa is 2 or 3 or 4 ? Viewport.ScreenSpaceAAEnum.Fxaa : Viewport.ScreenSpaceAAEnum.Disabled;
        _viewport.UseTaa = aa is 12 or 13;

        var shadowLevel = options.Get<int>("OIT_OPTION_SHADOW_DIST");
        if (_sun != null)
        {
            _sun.ShadowEnabled = options.Get<bool>("OIT_OPTION_USE_SHADOW");
            _sun.DirectionalShadowMaxDistance = shadowLevel switch { 1 => 100, 2 => 200, 3 => 300, _ => 400 };
        }
        if (_environment != null)
        {
            _environment.SsaoEnabled = options.Get<bool>("VIEWER_RENDER_SSAO");
            _environment.GlowEnabled = options.Get<bool>("OIT_OPTION_USE_HDR");
            _environment.FogEnabled = options.Get<bool>("VIEWER_RENDER_FOG");
        }

        var view = options.Get<int>("OIT_OPTION_VIEW_DISTANCE");
        var metres = view switch { 1 => 600, 2 => 1000, 3 => 1500, _ => 2000 };
        var textureBackground = options.Get<int>("OIT_OPTION_TEXTURE_BG");
        var textureCharacter = options.Get<int>("OIT_OPTION_TEXTURE_CHARACTER");
        static int ToMipBias(int quality) => quality switch { 1 => 2, 2 => 1, _ => 0 };
        // Godot exposes one viewport mip bias, so apply the lower of the two Cry texture tiers globally.
        var mipBias = Math.Max(ToMipBias(textureBackground), ToMipBias(textureCharacter));
        _viewport.TextureMipmapBias = mipBias;
        return new StreamingGraphicsOptions(options.Get<int>("VIEWER_STREAM_NEAR_RADIUS"),
            options.Get<int>("VIEWER_STREAM_FAR_RADIUS"), metres, mipBias);
    }
}
