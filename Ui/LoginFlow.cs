using Godot;

namespace AAEmu.GodotViewer.Ui;

public partial class LoginFlow : Control
{
    private readonly ILoginBackend _backend;
    private readonly VBoxContainer _content;
    private readonly LineEdit _host;
    private readonly LineEdit _port;
    private readonly LineEdit _account;
    private readonly LineEdit _password;
    private readonly Label _status;
    private readonly Button _loginButton;
    private readonly ItemList _worldList;
    private readonly Button _worldButton;
    private readonly ItemList _characterList;
    private readonly Button _enterButton;
    private readonly Button _worldBack;
    private readonly Button _characterBack;
    private readonly Label _loginHeading = Heading("Login");
    private readonly Label _worldHeading = Heading("Select Server");
    private readonly Label _characterHeading = Heading("Select Character");
    private readonly List<WorldInfo> _worlds = [];
    private readonly List<CharacterInfo> _characters = [];
    private CancellationTokenSource _pending;
    private int _panel;

    public event Action<CharacterInfo> EnteredWorld;

    public LoginFlow(ILoginBackend backend, string host = "127.0.0.1", int port = 1237, string account = "", string password = "")
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var shade = new ColorRect { Color = new Color(0.025f, 0.035f, 0.055f, 0.88f), MouseFilter = MouseFilterEnum.Stop };
        shade.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(shade);

        var center = new CenterContainer();
        center.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(center);
        var panel = new PanelContainer { CustomMinimumSize = new Vector2(420, 0) };
        center.AddChild(panel);
        _content = new VBoxContainer { CustomMinimumSize = new Vector2(380, 0) };
        _content.AddThemeConstantOverride("separation", 12);
        panel.AddChild(_content);

        _host = new LineEdit { PlaceholderText = "Host", Text = host };
        _port = new LineEdit { PlaceholderText = "Port", Text = port.ToString() };
        _account = new LineEdit { PlaceholderText = "Account", Text = account };
        _password = new LineEdit { PlaceholderText = "Password", Text = password, Secret = true };
        _loginButton = new Button { Text = "Login" };
        _status = new Label { Text = "", AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _loginButton.Pressed += SubmitLogin;
        _password.TextSubmitted += _ => SubmitLogin();
        _account.TextSubmitted += _ => _password.GrabFocus();
        _content.AddChild(_loginHeading);
        _content.AddChild(_host);
        _content.AddChild(_port);
        _content.AddChild(_account);
        _content.AddChild(_password);
        _content.AddChild(_loginButton);
        _content.AddChild(_status);

        _worldList = new ItemList { CustomMinimumSize = new Vector2(380, 180) };
        _worldButton = new Button { Text = "Select Server" };
        _worldBack = new Button { Text = "Back" };
        _worldButton.Pressed += SelectWorld;
        _worldBack.Pressed += () => ShowPanel(0);
        _worldList.ItemActivated += _ => SelectWorld();

        _characterList = new ItemList { CustomMinimumSize = new Vector2(380, 180) };
        _enterButton = new Button { Text = "Enter World" };
        _characterBack = new Button { Text = "Back" };
        _enterButton.Pressed += EnterWorld;
        _characterBack.Pressed += () => ShowPanel(1);
        _characterList.ItemActivated += _ => EnterWorld();

        _content.AddChild(_worldHeading);
        _content.AddChild(_worldList);
        _content.AddChild(Row(_worldButton, _worldBack));
        _content.AddChild(_characterHeading);
        _content.AddChild(_characterList);
        _content.AddChild(Row(_enterButton, _characterBack));
        ShowPanel(0);
    }

    public LoginFlow() : this(new MockLoginBackend()) { }

    public override void _UnhandledKeyInput(InputEvent @event)
    {
        if (@event is InputEventKey { Pressed: true, Keycode: Key.Escape } && _pending is not null)
        {
            _pending.Cancel();
            GetViewport().SetInputAsHandled();
        }
    }

    private void SubmitLogin()
    {
        if (!int.TryParse(_port.Text, out var port) || port is < 1 or > 65535)
        {
            _status.Text = "Enter a valid port (1–65535).";
            return;
        }
        var host = _host.Text;
        var account = _account.Text;
        var password = _password.Text;
        Run(async ct =>
        {
            var result = await _backend.LoginAsync(host, port, account, password, ct);
            if (!result.Ok) throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.Error) ? "Login failed." : result.Error);
            return await _backend.GetWorldsAsync(ct);
        }, worlds =>
        {
            _worlds.Clear();
            _worlds.AddRange(worlds);
            _worldList.Clear();
            foreach (var world in _worlds)
                _worldList.AddItem($"{world.Name}  |  {(world.Online ? "Online" : "Offline")}  |  {world.Load}");
            ShowPanel(1);
        });
    }

    private void SelectWorld()
    {
        var selected = _worldList.GetSelectedItems();
        if (selected.Length == 0) { _status.Text = "Select a server first."; return; }
        var world = _worlds[selected[0]];
        if (!world.Online) { _status.Text = "That server is offline."; return; }
        Run(ct => _backend.SelectWorldAsync(world.Id, ct), characters =>
        {
            _characters.Clear();
            _characters.AddRange(characters);
            _characterList.Clear();
            foreach (var character in _characters)
                _characterList.AddItem($"{character.Name}  |  Lv. {character.Level}  |  {character.Race}  |  {character.Gender}  |  {character.Zone}");
            ShowPanel(2);
        });
    }

    private void EnterWorld()
    {
        var selected = _characterList.GetSelectedItems();
        if (selected.Length == 0) { _status.Text = "Select a character first."; return; }
        var character = _characters[selected[0]];
        Run(async ct => { await _backend.EnterWorldAsync(character.Id, ct); return true; }, _ =>
        {
            EnteredWorld?.Invoke(character);
            Hide();
        });
    }

    private void Run<T>(Func<CancellationToken, Task<T>> operation, Action<T> completed)
    {
        _pending?.Dispose();
        _pending = new CancellationTokenSource();
        var token = _pending.Token;
        SetBusy(true);
        _ = CompleteAsync(operation, completed, token);
    }

    private async Task CompleteAsync<T>(Func<CancellationToken, Task<T>> operation, Action<T> completed, CancellationToken token)
    {
        T result = default;
        string error = null;
        try { result = await operation(token).ConfigureAwait(false); }
        catch (OperationCanceledException) { error = "Cancelled."; }
        catch (Exception exception) { error = exception.Message; }
        Callable.From(() =>
        {
            if (token.IsCancellationRequested)
            {
                SetBusy(false);
                _status.Text = "Cancelled.";
                return;
            }
            SetBusy(false);
            if (error is not null) { _status.Text = error; return; }
            _status.Text = string.Empty;
            completed(result);
        }).CallDeferred();
    }

    private void SetBusy(bool busy)
    {
        _status.Text = busy ? "Connecting ... (Escape to cancel)" : _status.Text;
        SetBusy(_content, busy);
    }

    /// <summary>Disables every input under <paramref name="node"/>, including the buttons inside the rows.</summary>
    private static void SetBusy(Node node, bool busy)
    {
        foreach (var child in node.GetChildren())
        {
            if (child is Button button) button.Disabled = busy;
            if (child is LineEdit edit) edit.Editable = !busy;
            if (child is ItemList list) list.MouseFilter = busy ? MouseFilterEnum.Ignore : MouseFilterEnum.Stop;
            SetBusy(child, busy);
        }
    }

    private void ShowPanel(int panel)
    {
        _panel = panel;
        _status.Text = string.Empty;
        _loginButton.Visible = panel == 0;
        _host.Visible = panel == 0;
        _port.Visible = panel == 0;
        _account.Visible = panel == 0;
        _password.Visible = panel == 0;
        _loginHeading.Visible = panel == 0;
        _worldList.Visible = panel == 1;
        _worldButton.Visible = panel == 1;
        _worldBack.Visible = panel == 1;
        _worldHeading.Visible = panel == 1;
        _characterList.Visible = panel == 2;
        _enterButton.Visible = panel == 2;
        _characterBack.Visible = panel == 2;
        _characterHeading.Visible = panel == 2;
        _status.Visible = true;
    }

    private static Label Heading(string text) => new() { Text = text, HorizontalAlignment = HorizontalAlignment.Center };
    private static HBoxContainer Row(Control first, Control second)
    {
        var row = new HBoxContainer();
        row.AddChild(first);
        row.AddChild(second);
        return row;
    }
}
