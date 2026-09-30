#nullable enable
using Godot;

namespace AAEmu.GodotViewer.Ui.X2;

// LEFT_LOADING is the client's "loading screen is gone" event: center_message_manager.lua shows the zone banner
// (zone_state_alarm.lua: zone name, "Nuia Alliance Controlled Zone", the protection text) on it and remembers the
// zone as announced; the banner hides (and removes) itself a few seconds later. Several places send LEFT_LOADING
// (the world stage start in X2Engine, the first quest snapshot in X2ProtocolBinding, the 3 s refresh after
// attach), all while our world is still behind its loading screen, so the banner was shown and used up unseen.
// While the world's LoadingScreen exists and has not closed since world entry, LEFT_LOADING is held and sent once
// when it closes (and again after every later load). Without a LoadingScreen (offline UI test) nothing is held.
// The hold has to be in place before the world stage is built (X2LoginSession.StartInWorld in _Ready): that build
// dispatches a LEFT_LOADING of its own, the banner script took it for the end of loading, announced the zone behind
// the loading screen and ignored the real event afterwards.
public partial class X2UiLayer
{
    private CanvasLayer? _loadingScreen;
    private bool _loadingWasVisible, _worldShown;
    private double _sinceAttach;
    private UiRoot? _filteredRoot;
    private Client.OnlineSession? _loadingSession;
    private Func<string, bool>? _leftLoadingFilter;

    /// <summary>Installs the LEFT_LOADING hold for every UI root (called before the first stage is built).</summary>
    private void InstallLeftLoadingFilter()
    {
        _leftLoadingFilter = name => name != "LEFT_LOADING" || !IsInsideTree() || !HoldLeftLoading();
        UiRoot.DefaultEventFilter = _leftLoadingFilter;
    }

    private void RemoveLeftLoadingFilter()
    {
        if (ReferenceEquals(UiRoot.DefaultEventFilter, _leftLoadingFilter)) UiRoot.DefaultEventFilter = null;
    }

    private bool LoadingScreenVisible()
    {
        if (_loadingScreen == null || !IsInstanceValid(_loadingScreen))
            _loadingScreen = GetTree().Root.FindChild("LoadingScreen", true, false) as CanvasLayer;
        return _loadingScreen is { Visible: true };
    }

    /// <summary>True while LEFT_LOADING must wait: the world's loading screen is up, or has not closed yet since entry.</summary>
    private bool HoldLeftLoading()
    {
        var visible = LoadingScreenVisible();
        // live (network backend): nothing before the world's loading screen has closed once since world entry, including
        // the world stage start (stage 12) that runs before the session and the LoadingScreen exist
        if (Backend is Client.NetLoginBackend) return visible || !_worldShown;
        return _loadingScreen != null && visible;
    }

    private void LoadingTick(double delta)
    {
        if (_session.Root is { } root && !ReferenceEquals(root, _filteredRoot))
        {
            _filteredRoot = root;
            root.EventFilter = name => name != "LEFT_LOADING" || !HoldLeftLoading();
        }
        if (!ReferenceEquals(LiveSession, _loadingSession))
        {
            _loadingSession = LiveSession;
            _worldShown = false;
            _sinceAttach = 0;
        }
        if (LiveSession is null || _session.Engine.Stage != Scripting.X2Engine.StageWorld) return;
        _sinceAttach += delta;
        var visible = LoadingScreenVisible();
        // closed after being up, or never shown at all (a world that was already loaded)
        if (!visible && (_loadingWasVisible || (!_worldShown && _sinceAttach > 5)))
        {
            _worldShown = true;
            Emit("[x2] loading screen closed: LEFT_LOADING");
            _session.Root?.DispatchEvent("LEFT_LOADING");
        }
        _loadingWasVisible = visible;
    }
}
