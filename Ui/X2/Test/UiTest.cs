using AAEmu.GodotViewer.Client;
using AAEmu.GodotViewer.Net;
using AAEmu.GodotViewer.Ui.X2.Scripting;
using Godot;

namespace AAEmu.GodotViewer.Ui.X2;

/// <summary>
/// Standalone test for the original login UI: godot --path &lt;project&gt; res://Ui/X2/Test/UiTest.tscn -- [options]
/// <list type="bullet">
/// <item>pak=&lt;game_pak&gt;, db=&lt;client database&gt;, host=127.0.0.1, port=1237 (defaults and the godotage.cfg / GODOTAGE_* alternatives: <see cref="ClientPaths"/>), scale=1</item>
/// <item>mock=1 uses MockLoginBackend instead of the network.</item>
/// <item>auto=login|server|select|world drives the screens with real input events up to that point
///   (login: type the launcher account and a dummy password, press Enter; server: pick the first world and connect;
///   select: stop at the character list; world: press Start Game on the first character).</item>
/// <item>shots=&lt;dir&gt; saves a PNG of every screen; quit=&lt;seconds&gt; exits after that long.</item>
/// </list>
/// Log lines (script errors, stubbed APIs, login progress) go to stdout; tokens and passwords are never printed.
/// </summary>
public partial class UiTest : Node
{
    private readonly Dictionary<string, string> _args = new(StringComparer.OrdinalIgnoreCase);
    private X2UiLayer _ui;
    private NetLoginBackend _net;
    private string _auto = "";
    private string _shots;
    private double _quitAfter;
    private double _clock;
    private double _stepAt;
    private int _step;
    private bool _quitting;
    private string _launcherAccount = "";
    private bool _hudBuilt;

    public override void _Ready()
    {
        // the login stage streams the login2 world through the same loaders as WorldViewer, which paces them per frame
        RenderBudget.SetMainThread();
        foreach (var arg in OS.GetCmdlineUserArgs())
        {
            var kv = arg.TrimStart('-').Split('=', 2);
            _args[kv[0]] = kv.Length > 1 ? kv[1] : "1";
        }
        _auto = Arg("auto", "");
        _shots = Arg("shots", null);
        _quitAfter = double.TryParse(Arg("quit", "0"), System.Globalization.CultureInfo.InvariantCulture, out var q) ? q : 0;
        if (_shots != null) Directory.CreateDirectory(_shots);

        var pak = ClientPaths.Pak; // pak=, GODOTAGE_PAK or godotage.cfg
        if (!PakFiles.Open(pak))
        {
            GD.PrintErr($"UiTest: cannot open {pak}");
            GetTree().Quit(1);
            return;
        }

        ILoginBackend backend;
        if (Arg("mock", "0") != "0") backend = new MockLoginBackend();
        else backend = _net = new NetLoginBackend();
        var launcher = LauncherConfig.TryRead();
        _launcherAccount = launcher?.UserName ?? "";

        _ui = new X2UiLayer(backend, Arg("host", ClientPaths.LoginHost), int.Parse(Arg("port", ClientPaths.LoginPort.ToString())),
            account: Arg("account", ""), launcherAccount: _launcherAccount, gameDatabase: ClientPaths.Database)
        {
            UiScale = float.Parse(Arg("scale", "1"), System.Globalization.CultureInfo.InvariantCulture),
        };
        _ui.Log += line => GD.Print(line.Length > 2000 ? line[..2000] + " ..." : line);
        _ui.EnteredWorld += OnEnteredWorld;
        _ui.Session.StageBuilt += stage => { GD.Print($"UiTest: stage {stage} built"); if (stage == X2Engine.StageWorld) _hudBuilt = true; };
        AddChild(_ui);
        GD.Print($"UiTest: window {GetViewport().GetVisibleRect().Size}, auto={_auto}, backend={backend.GetType().Name}");
    }

    private string Arg(string name, string fallback) => _args.TryGetValue(name, out var v) ? v : fallback;

    private void OnEnteredWorld(CharacterInfo character)
    {
        GD.Print($"UiTest: ENTERED WORLD as {character.Name} (level {character.Level} {character.Race})");
        if (_net?.Entered is { } e) GD.Print($"UiTest: unit {e.UnitId} zone {e.ZoneId} at {e.Position}");
        _stepAt = _clock + 3;
    }

    public override void _Process(double delta)
    {
        RenderBudget.NewFrame(delta * 1000.0);
        if (_ui == null || _quitting) return;
        _clock += delta;
        if (_quitAfter > 0 && _clock > _quitAfter) { Quit(); return; }
        if (_clock < _stepAt) return;
        var engine = _ui.Session.Engine;
        var root = _ui.Root;
        if (root == null) return;
        switch (_step)
        {
            case 0: // login screen up (fade-in done)
                Next(1.5);
                break;
            case 1:
                Shot("01_login");
                if (Arg("resize", null) is { } size && size.Split('x') is [var w, var h])
                {
                    DisplayServer.WindowSetSize(new Vector2I(int.Parse(w), int.Parse(h)));
                    _step = 50;
                    _stepAt = _clock + 1;
                    break;
                }
                if (_auto.Length == 0) { _step = 100; break; }
                Click("idEdit ");
                Type(_launcherAccount.Length > 0 && Arg("account", "").Length == 0 ? _launcherAccount : Arg("account", "tester"));
                Key(Godot.Key.Tab);
                Type(Arg("typepw", "devpassword"));
                Next(0.5);
                break;
            case 2:
                Shot("02_typed");
                Key(Godot.Key.Enter);
                Next(0.5);
                break;
            case 3: // wait for the server list
                if (engine.Status.StartsWith("login failed", StringComparison.Ordinal))
                {
                    _step = 60;
                    _stepAt = _clock + 1;
                }
                else if (engine.Stage == X2Engine.StageServer && Find("worldListWnd") != null) Next(1.2);
                else if (_clock > 60) { GD.PrintErr("UiTest: no server list"); Quit(); }
                break;
            case 4:
                Shot("03_server");
                if (_auto == "login") { _step = 100; break; }
                ClickListRow();
                Next(0.4);
                break;
            case 5:
                Shot("04_server_selected");
                Click("selectBtn");
                Next(0.5);
                break;
            case 6: // wait for character select
                if (engine.Stage == X2Engine.StageSelect && Find("startBtn") != null) Next(1.5);
                else if (_clock > 120) { GD.PrintErr("UiTest: no character select"); Quit(); }
                break;
            case 7:
                Shot("05_select");
                if (_auto != "world") { _step = 100; break; }
                if (Arg("char", "") is { Length: > 0 } name)
                {
                    var button = _ui.Root.AllWidgets().FirstOrDefault(w => w is ButtonWidget && w.IsVisible() && w.GetText() == name);
                    if (button != null) { ClickAt(Center(button)); GD.Print($"UiTest: picked character {name}"); }
                    else GD.PrintErr($"UiTest: character {name} not in the list");
                }
                Next(1.0);
                break;
            case 8:
                Shot("06_character_picked");
                Click("startBtn");
                Next(0.5);
                break;
            case 9: // wait for the world
                if (engine.Stage == X2Engine.StageWorld && (!_ui.Session.Options.HudInWorld || _hudBuilt)) { Next(double.Parse(Arg("hudwait", "4"), System.Globalization.CultureInfo.InvariantCulture)); }
                else if (_clock > 180) { GD.PrintErr("UiTest: did not enter the world"); Shot("07_error"); _step = 100; }
                break;
            case 10:
                Shot("07_world");
                _step = 100;
                break;
            case 50: // after a window resize
                GD.Print($"UiTest: resized to {GetViewport().GetVisibleRect().Size}");
                Shot("01b_resized");
                _step = _auto.Length == 0 ? 100 : 51;
                break;
            case 51:
                _step = 1;
                _args.Remove("resize");
                break;
            case 60:
                Shot("03_login_failed");
                _step = 100;
                break;
            case 100:
                PrintSummary();
                _step = 101;
                if (_quitAfter <= 0 && _auto.Length > 0) Quit();
                break;
        }
    }

    private void Next(double wait)
    {
        _step++;
        _stepAt = _clock + wait;
    }

    private Widget Find(string id) => _ui.Root?.AllWidgets().FirstOrDefault(w => w.Id == id && w.IsVisible());

    private Vector2 Center(Widget w)
    {
        var r = w.ScreenRect;
        return new Vector2(r.X + r.Width / 2, r.Y + r.Height / 2) * _ui.UiScale;
    }

    private void Click(string id)
    {
        var w = Find(id);
        if (w == null) { GD.PrintErr($"UiTest: widget '{id}' not found"); return; }
        ClickAt(Center(w));
        GD.Print($"UiTest: clicked {w}");
    }

    private void ClickAt(Vector2 p)
    {
        var vp = GetViewport();
        vp.PushInput(new InputEventMouseMotion { Position = p, GlobalPosition = p });
        vp.PushInput(new InputEventMouseButton { Position = p, GlobalPosition = p, ButtonIndex = MouseButton.Left, Pressed = true });
        vp.PushInput(new InputEventMouseButton { Position = p, GlobalPosition = p, ButtonIndex = MouseButton.Left, Pressed = false });
    }

    private void ClickListRow()
    {
        var list = _ui.Root.AllWidgets().OfType<ListCtrlWidget>().FirstOrDefault(l => l.IsVisible());
        var row = list?.Children.FirstOrDefault(c => c.Id == "item[1]");
        if (row == null) { GD.PrintErr("UiTest: no world row"); return; }
        var r = row.ScreenRect;
        ClickAt(new Vector2(r.X + 40, r.Y + r.Height / 2) * _ui.UiScale);
        GD.Print($"UiTest: clicked world row 1 ({list.GetSelectedIdx()})");
    }

    private void Type(string text)
    {
        foreach (var ch in text)
        {
            var key = new InputEventKey { Pressed = true, Unicode = ch, Keycode = OS.FindKeycodeFromString(ch.ToString().ToUpperInvariant()) };
            GetViewport().PushInput(key);
            GetViewport().PushInput(new InputEventKey { Pressed = false, Unicode = ch, Keycode = key.Keycode });
        }
    }

    private void Key(Key key)
    {
        GetViewport().PushInput(new InputEventKey { Pressed = true, Keycode = key });
        GetViewport().PushInput(new InputEventKey { Pressed = false, Keycode = key });
    }

    private void Shot(string name)
    {
        if (_shots == null) return;
        var image = GetViewport().GetTexture().GetImage();
        var path = Path.Combine(_shots, name + ".png");
        image.SavePng(path);
        GD.Print($"UiTest: screenshot {path}");
    }

    private void PrintSummary()
    {
        var host = _ui.Session.Host;
        GD.Print($"UiTest: stage {_ui.Session.Engine.Stage}, status '{_ui.Session.Engine.Status}', script errors {host?.Errors.Count ?? 0}");
        if (host != null) GD.Print("UiTest: stubbed APIs hit: " + string.Join(", ", host.StubHits));
        var missing = _ui.Session.Options.Translator.Missing;
        GD.Print($"UiTest: untranslated strings: {missing.Count}");
        foreach (var m in missing) GD.Print("UiTest:   untranslated: " + m);
        if (Arg("dumptex", null) is { } dir)
        {
            foreach (var (path, texture) in _ui.Textures.Loaded)
            {
                if (texture == null) continue;
                var image = texture.GetImage();
                if (image.IsCompressed()) image.Decompress();
                var file = Path.Combine(dir, path.Replace('/', '_') + ".png");
                image.SavePng(file);
            }
            GD.Print($"UiTest: dumped {_ui.Textures.Loaded.Count} textures to {dir}");
        }
    }

    private async void Quit()
    {
        if (_quitting) return;
        _quitting = true;
        if (_net?.Client != null)
        {
            GD.Print("UiTest: logging out");
            try { await _net.Client.LogoutAsync().WaitAsync(TimeSpan.FromSeconds(20)); }
            catch (Exception e) { GD.PrintErr($"UiTest: logout: {e.Message}"); }
            GD.Print("UiTest: logged out");
        }
        GetTree().Quit();
    }
}
