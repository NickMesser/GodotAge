#nullable enable
using System.Globalization;
using System.Text.RegularExpressions;
using AAEmu.GodotViewer.Lua;

namespace AAEmu.GodotViewer.Ui.X2.Scripting;

/// <summary>Settings for the original login flow.</summary>
public sealed class X2LoginOptions
{
    public string Host { get; init; } = "127.0.0.1";
    public int Port { get; init; } = 1237;
    /// <summary>Pre-filled account name (Console "cl_account"); empty leaves the id box empty.</summary>
    public string Account { get; init; } = "";
    /// <summary>
    /// When the typed account equals this name, the backend is called with an empty account and password, which
    /// makes NetLoginBackend use the local launcher's dev account and token.
    /// </summary>
    public string LauncherAccount { get; init; } = "";
    public string Locale { get; init; } = "en_us";
    /// <summary>X2Util:GetGameProvider(); 0 = XLGAMES. Locale remains independently configurable.</summary>
    public int GameProvider { get; init; } = 0;
    public string VersionText { get; init; } = "AAEmu Godot client";
    public string? GameDatabase { get; init; }
    public Func<bool>? IsShiftKeyDown { get; init; }
    /// <summary>Optional lobby details (abilities, money) the plain <see cref="CharacterInfo"/> does not carry.</summary>
    public Func<CharacterInfo, X2CharacterDetails?>? Details { get; init; }
    /// <summary>English for strings the client data has only in Chinese/Korean.</summary>
    public UiTranslator Translator { get; init; } = UiTranslator.None;
    /// <summary>After world entry, load the in-game x2ui (stage 6) as the HUD instead of hiding the UI.</summary>
    public bool HudInWorld { get; init; } = true;
    /// <summary>Overrides the x2ui package list loaded in the world (null = all packages of the pak).</summary>
    public IReadOnlyList<string>? WorldPackages { get; init; }
}

/// <summary>Extra lobby data for the character-select screen.</summary>
public sealed record X2CharacterDetails(int[] Abilities, long Money, string FactionName, uint FactionId, string ZoneName,
    int LaborPower = 0, long BmPoint = 0, int LocalLaborPower = 0);

/// <summary>CharacterBuilder inputs for the selected lobby character or the character currently being created.</summary>
public sealed record LoginCharacterPreview(long ModelId, string Race, string Gender,
    IReadOnlyDictionary<int, long> Equipment, LoginCharacterAppearance Appearance, bool IsCreation);

/// <summary>
/// The engine side of the login stages: stage state, the X2* functions the connect / character-select scripts call,
/// the events the client would fire, and the calls into <see cref="ILoginBackend"/>. All members run on the UI thread;
/// backend completions come back through the post callback.
/// </summary>
public sealed class X2Engine
{
    public const int StageLogin = 5, StageServer = 7, StageSelect = 8, StageCreate = 9, StageCustomize = 10,
        StageAbility = 11, StageWorld = 12;
    private static readonly string[] RaceNames = ["none", "nuian", "fairy", "dwarf", "elf", "hariharan", "ferre", "returned", "warborn", "daru"];
    // enum_abilities
    private static readonly string[] AbilityNames = ["general", "fight", "illusion", "adamant", "will", "death", "wild", "magic", "vocation", "romance", "love", "hatred", "assassin", "madness", "pleasure"];

    private readonly ILoginBackend _backend;
    private readonly X2LoginOptions _options;
    private readonly IUiTextSource _texts;
    private readonly Action<Action> _post;
    private readonly Action<string> _log;
    private readonly LoginCustomizationCatalog _catalog;
    private IReadOnlyList<WorldInfo> _worlds = [];
    private IReadOnlyList<CharacterInfo> _characters = [];
    private int _selectedWorld = -1;
    private int _shownCharacter;
    private LoginCustomizingUnit? _customizingUnit;
    private string _createRace = "nuian", _createGender = "male";
    private readonly List<int> _createAbilities = [0, 0, 0];
    private bool _previewFrozen;
    private bool _skipSkillAction;
    private bool _customizingLight = true;
    private long _loginAnimationVersion;
    private long _skillActionSerial;
    private bool _enteringStage, _skillActionInitial;
    private string _loginAnimation = "select";
    private int _loginAnimationAbility = 1;
    private string _previewFaceName = "";
    private readonly Dictionary<ulong, DateTimeOffset> _pendingDeletes = [];
    private bool _busy;
    private CancellationTokenSource _cts = new();
    private X2LuaHost? _host;
    private readonly Dictionary<string, Widget> _dialogs = [];
    private int _dialogCounter;
    private readonly Dictionary<double, LuaFunctionReference> _dialogTaskHandlers = [];

    public X2Engine(ILoginBackend backend, X2LoginOptions options, IUiTextSource texts, Action<Action> post, Action<string> log)
    {
        _backend = backend;
        _options = options;
        _texts = texts;
        _post = post;
        _log = log;
        _catalog = new LoginCustomizationCatalog(options.GameDatabase);
        _backend.CharacterDeleted += characterId => _post(() => CharacterWasDeleted(characterId));
    }

    public int Stage { get; private set; } = StageLogin;
    /// <summary>Original login2 time of day; the creation script can change it through X2LoginCharacter.</summary>
    public float LoginStageHour { get; private set; } = 6.5f; // the original client's login stage runs at 06:30 (X2Time:GetGameTime() in its lobby)
    public long LoginAnimationVersion => _loginAnimationVersion;
    /// <summary>Counts X2LoginCharacter:ShowSkillAction calls; the model view starts the skill-action cutscene for each.</summary>
    public long SkillActionSerial => _skillActionSerial;
    /// <summary>
    /// True when the latest ShowSkillAction came from the page's own start (UpdateCreateAbilityUI's random first pick, made while the stage's
    /// show event is dispatched) rather than from the player choosing a skillset: the original plays the class cutscene only for the latter.
    /// </summary>
    public bool SkillActionInitial => _skillActionInitial;
    public string LoginAnimation => _loginAnimation;
    public int LoginAnimationAbility => _loginAnimationAbility;
    public bool PreviewFrozen => _previewFrozen;
    public bool SkipSkillAction => _skipSkillAction;
    public bool CustomizingLight => _customizingLight;
    public string PreviewFaceName => _previewFaceName;

    /// <summary>Starts directly in the world (the character entered without the login screens, e.g. auto-login).</summary>
    public void BeginInWorld() => Stage = StageWorld;
    public ILoginBackend Backend => _backend;
    public IReadOnlyList<WorldInfo> Worlds => _worlds;
    public IReadOnlyList<CharacterInfo> Characters => _characters;
    public WorldInfo? SelectedWorld => _worlds.FirstOrDefault(w => w.Id == _selectedWorld);
    public CharacterInfo? ShownCharacter => _shownCharacter >= 1 && _shownCharacter <= _characters.Count ? _characters[_shownCharacter - 1] : null;
    public LoginCharacterPreview? CharacterPreview => Stage is StageCreate or StageAbility or StageCustomize
        ? new LoginCharacterPreview(PlayerModel(_createRace, _createGender), _createRace, _createGender,
            CreationPreviewEquipment(), _customizingUnit?.Appearance ?? new LoginCharacterAppearance(), true)
        : ShownCharacter is { } c ? new LoginCharacterPreview(c.ModelId > 0 ? c.ModelId : PlayerModel(c.Race, c.Gender), c.Race, c.Gender,
            c.Equipment ?? new Dictionary<int, long>(), c.Appearance ?? new LoginCharacterAppearance(), false) : null;
    public string Status { get; private set; } = "";

    /// <summary>The UI must be rebuilt for another stage (its script packages differ).</summary>
    public event Action<int>? StageChanged;
    public event Action<CharacterInfo>? EnteredWorld;
    public event Action? ExitRequested;
    public event Action<string>? StatusChanged;
    public event Action? CharacterPreviewChanged;

    private void SetStatus(string status)
    {
        Status = status;
        _log("[login] " + status);
        StatusChanged?.Invoke(status);
    }

    public void CancelPending()
    {
        _cts.Cancel();
        _cts = new CancellationTokenSource();
        _busy = false;
    }

    // ------------------------------------------------------------------ bindings

    /// <summary>Installs the X2 functions on a freshly created host (one per stage).</summary>
    public void Install(X2LuaHost host)
    {
        _host = host;
        _dialogs.Clear();
        _dialogTaskHandlers.Clear();
        if (Stage is StageCreate or StageAbility or StageCustomize)
            // one unit per stage host; the previous stage's unit hands over the look (the client keeps one unit for stages 9-11)
            _customizingUnit = new LoginCustomizingUnit(host.Root, _catalog, PlayerModel(_createRace, _createGender), RaceId(_createRace),
                _createGender == "female" ? 2 : 1, () => CharacterPreviewChanged?.Invoke(), _customizingUnit);

        host.Define("X2Locale", "GetLocale", _ => _options.Locale);
        host.Define("X2Locale", "GetLocaleIndex", _ => 1.0);
        host.Define("X2Locale", "GetKeyboardLayout", _ => "en");
        host.Define("X2Locale", "LocalizeUiText", a => Localize(a.Int(0), a.Str(1) ?? "", a, 2));
        host.Define("X2Locale", "LocalizeFormatUiText", a => Localize(a.Int(0), a.Str(1) ?? "", a, 2));
        host.Define("X2Locale", "HasLocalizeUiText", a => _texts.Get(a.Int(0), a.Str(1) ?? "") != null);
        host.Define("X2Locale", "LocalizeNonUiText", a => a.Str(1) ?? a.Str(0) ?? "");
        host.Define("X2Locale", "TextFormating", a => a.Str(0) ?? "");

        host.Define("X2Util", "GetGameProvider", _ => (double)_options.GameProvider);
        host.Define("X2Util", "GetVersionInfo", _ => _options.VersionText);
        host.Define("X2Util", "GetRevisionStr", _ => "10.0.2.13");
        // baselib.lua reads this once at load (IS_TICK_RECOVER_LOCAL_LABOR_POWER), before the world API families exist
        host.Define("X2Player", "IsTickRecoverLocalLaborPower", _ => true);
        host.Define("X2Util", "GetNamePolicyInfo", a => Api.X2SystemApi.NamePolicy(a.Int(0, 1)));
        host.Define("X2Util", "AND", a => (double)((long)a.Num(0) & (long)a.Num(1)));
        host.Define("X2Util", "OR", a => (double)((long)a.Num(0) | (long)a.Num(1)));
        host.Define("X2Util", "XOR", a => (double)((long)a.Num(0) ^ (long)a.Num(1)));
        host.Define("X2Util", "UTF8StringLength", a => (double)(a.Str(0) ?? "").Length);
        host.Define("X2Util", "NumberToString", a => a.Str(0) ?? "0");
        host.Define("X2Util", "Random", a => (double)Random.Shared.Next(a.Int(0), Math.Max(a.Int(0), a.Int(1)) + 1));
        host.Define("X2Util", "OpenWeb", a => { _log($"[x2] OpenWeb {a.Str(0)}"); return null; });
        host.Define("X2Util", "RaiseLuaCallStack", a => { _log($"[x2] LuaAssert {a.Str(0)}"); return null; });
        host.Define("X2Util", "GetFocusedWidgetId", _ => host.Root.Focused?.Id ?? "");

        host.Define("X2Debug", "GetDevMode", _ => _showLoginPanel);
        host.Define("X2Input", "GetInputLanguage", _ => "English");
        host.Define("X2Input", "SetInputLanguage", _ => null);
        InstallInput(host);
        host.Define("Console", "GetAttribute", a => a.Str(0) switch
        {
            "cl_account" => _options.Account,
            _ => null,
        });
        host.Define("Console", "ExecuteString", a =>
        {
            if (string.Equals(a.Str(0)?.Trim(), "quit", StringComparison.OrdinalIgnoreCase)) ExitRequested?.Invoke();
            return null;
        });
        host.Define("X2Unit", "GetRaceStr", a => a.Int(0) is var r && r >= 0 && r < RaceNames.Length ? RaceNames[r] : "none");
        host.Define("X2Unit", "GetGenderStr", a => a.Int(0) == 2 ? "female" : "male");
        host.Define("X2Ability", "GetCombatAbilityMax", _ => (double)AbilityNames.Length);
        host.Define("X2Ability", "GetAbilityStr", a => a.Int(0) is var i && i >= 0 && i < AbilityNames.Length ? AbilityNames[i] : "invalid ability");

        // login stage
        host.Define("X2LoginCharacter", "GetCurrentStage", _ => (double)Stage);
        host.Define("X2LoginCharacter", "GetLoginStageTOD", _ => new LuaMulti((double)(int)LoginStageHour,
            (double)Math.Round((LoginStageHour % 1f) * 60f)));
        host.Define("X2LoginCharacter", "SetLoginStageTOD", a =>
        {
            LoginStageHour = Math.Clamp(a.Int(0), 0, 23) + Math.Clamp(a.Int(1), 0, 59) / 60f;
            return null;
        });
        host.Define("X2LoginCharacter", "SetCustomizingLight", a => { _customizingLight = a.Bool(0); CharacterPreviewChanged?.Invoke(); return null; });
        host.Define("X2LoginCharacter", "EndCurrentLoginStage", _ => null);
        host.Define("X2LoginCharacter", "GetLastPlayedWorldInfo", _ => SelectedWorld?.Name ?? _worlds.FirstOrDefault(w => w.Online)?.Name);
        host.Define("X2", "ConnectToServer", a => { ConnectToServer(a.Str(1) ?? "", a.Str(2) ?? ""); return null; });
        host.Define("X2", "GetCurrentWorldId", _ => (double)Math.Max(0, _selectedWorld));
        host.Define("X2", "GetRaceCongestions", _ => RaceCongestions());
        host.Define("X2", "ReturnToLoginStage", _ => { ReturnToLogin(); return null; });
        host.Define("X2", "GetWebWidgetName", _ => "webview");
        host.Define("X2", "IsWebEnable", _ => false);

        // server select
        host.Define("X2World", "IsUsingAutoLogin", _ => false);
        host.Define("X2World", "GetWorldInfo", _ => WorldTable());
        host.Define("X2World", "GetServerInfoTexts", _ => new LuaTable());
        host.Define("X2World", "GetAuthId", _ => 0.0);
        host.Define("X2World", "GetCharactersCountPerWorld", a => (double)(a.Int(0) == _selectedWorld ? _characters.Count : 0));
        host.Define("X2World", "GetCharactersCountPerAccount", _ => (double)_characters.Count);
        host.Define("X2World", "GetCharactersDefaultLimitPerWorld", _ => 6.0);
        host.Define("X2World", "GetCharactersDefaultLimitPerAccount", _ => 12.0);
        host.Define("X2World", "GetCharacterExpandableLimit", _ => 0.0);
        host.Define("X2World", "GetRemainChracterCountPerAccout", _ => (double)Math.Max(0, 6 - _characters.Count));
        host.Define("X2World", "GetExpandedCharacterRemainCount", _ => 0.0);
        host.Define("X2World", "GetTmpMaxCharSlot", _ => 0.0);
        host.Define("X2World", "RequestWorldListRefresh", _ => { RefreshWorlds(); return null; });
        host.Define("X2World", "EnterWorld", a => EnterWorld(a.Int(0)));
        host.Define("X2World", "GetCurrentWorldName", _ => SelectedWorld?.Name ?? "");
        host.Define("X2World", "GetCurrentWorldInfo", _ => SelectedWorld is { } w ? WorldEntry(w) : null);
        host.Define("X2World", "LeaveWorld", a => LeaveWorld(a.Int(0)));
        host.Define("X2World", "IsPreSelectCharacterPeriod", _ => false);
        host.Define("X2World", "ForbidCharCreation", _ => false);
        host.Define("X2World", "CanPreSelectCharacter", _ => false);
        host.Define("X2LoginCharacter", "ConnectToWorld", _ => { ConnectToWorld(); return null; });
        host.Define("X2LoginCharacter", "CancelWorldQueue", _ => { CancelPending(); return true; });
        host.Define("X2LoginCharacter", "GetWorldQueuePosition", _ => 0.0);

        // character select
        host.Define("X2LoginCharacter", "GetNumLoginCharacters", _ => (double)_characters.Count);
        host.Define("X2LoginCharacter", "GetLastPlayedCharacterIndex", _ => _characters.Count == 0 ? null : (double)Math.Clamp(_shownCharacter, 1, _characters.Count));
        host.Define("X2LoginCharacter", "GetRepresentCharacterIndex", _ => 0.0);
        host.Define("X2LoginCharacter", "ShowSelectedCharacter", a => { _shownCharacter = a.Int(0); SetLoginAnimation("select", _loginAnimationAbility); CharacterPreviewChanged?.Invoke(); return null; });
        host.Define("X2LoginCharacter", "SelectCharacter", a => { SelectCharacter(a.Int(0)); return null; });
        host.Define("X2LoginCharacter", "CheckPremiumUserServer", _ => true);
        host.Define("X2LoginCharacter", "IsInEnableStartingLocation", _ => true);
        host.Define("X2LoginCharacter", "IsDeleteRequestedCharacter", a => Character(a) is { } c && _pendingDeletes.ContainsKey(c.Id));
        host.Define("X2LoginCharacter", "GetCharacterDeleteWaitingTime", a => Character(a) is { } c && _pendingDeletes.TryGetValue(c.Id, out var at)
            ? Math.Max(0d, (at - DateTimeOffset.UtcNow).TotalSeconds) : 0d);
        host.Define("X2LoginCharacter", "IsTransferRequestedCharacter", _ => false);
        host.Define("X2LoginCharacter", "IsForceNameChangedCharacter", _ => false);
        host.Define("X2LoginCharacter", "CanShowClientDrivenSkipDialog", _ => false);
        host.Define("X2LoginCharacter", "RequestCharacterListRefresh", _ => null);
        host.Define("X2LoginCharacter", "GetLoginCharacterName", a => Character(a)?.Name ?? "");
        host.Define("X2LoginCharacter", "GetLoginCharacterLevel", a => (double)(Character(a)?.Level ?? 0));
        host.Define("X2LoginCharacter", "GetLoginCharacterRace", a => Character(a)?.Race ?? "nuian");
        host.Define("X2LoginCharacter", "GetLoginCharacterGender", a => Character(a)?.Gender ?? "male");
        host.Define("X2LoginCharacter", "GetLoginCharacterZone", a => Details(a)?.ZoneName is { Length: > 0 } z ? z : Character(a)?.Zone ?? "");
        host.Define("X2LoginCharacter", "GetLoginCharacterFaction", a => Details(a) is { FactionId: > 0 } d ? d.FactionId
            : Character(a)?.FactionId is > 0 ? Character(a)!.FactionId : null);
        host.Define("X2LoginCharacter", "GetLoginCharacterVisualRaceExpiredTimeStr", _ => "");
        host.Define("X2LoginCharacter", "GetLoginCharacterHeirLevel", _ => 0.0);
        host.Define("X2Faction", "GetFactionInfo", a =>
        {
            var factionId = (uint)a.Num(0);
            var match = _characters.Select(c => _options.Details?.Invoke(c)).FirstOrDefault(d => d != null && d.FactionId == factionId);
            var name = match?.FactionName;
            if (string.IsNullOrWhiteSpace(name))
                name = _characters.FirstOrDefault(c => c.FactionId == factionId)?.FactionName;
            return !string.IsNullOrWhiteSpace(name) ? new LuaTable { ["name"] = name, ["id"] = a.Num(0) } : null;
        });
        host.Define("X2LoginCharacter", "GetLoginCharacterFactionName", a => Details(a)?.FactionName is { Length: > 0 } name
            ? name : Character(a)?.FactionName ?? "");
        host.Define("X2LoginCharacter", "GetLoginCharacterMoney", a => (Details(a)?.Money ?? 0).ToString(CultureInfo.InvariantCulture));
        host.Define("X2LoginCharacter", "GetLoginCharacterAbilities", a =>
        {
            var t = new LuaTable();
            var abilities = Details(a)?.Abilities ?? [];
            for (var i = 0; i < abilities.Length; i++) t[(double)(i + 1)] = (double)abilities[i];
            return t;
        });
        host.Define("X2LoginCharacter", "GetLoginCharacterBmPoint", a => (Details(a)?.BmPoint ?? 0).ToString(CultureInfo.InvariantCulture));
        host.Define("X2LoginCharacter", "GetLoginCharacterLaborPower", a => (double)(Details(a)?.LaborPower ?? 0));
        host.Define("X2LoginCharacter", "GetLoginCharacterMaxLaborPower", a => (double)Math.Max(5000, Details(a)?.LaborPower ?? 0));

        // character creation (native stages 9 race, 11 ability, 10 customization)
        host.Define("X2LoginCharacter", "CreateCharacter", _ => { BeginCharacterCreate(); return null; });
        host.Define("X2LoginCharacter", "GotoCharacterSelect", _ => { GotoCharacterSelect(); return null; });
        host.Define("X2LoginCharacter", "EndCharacterCreate", a => { if (a.Bool(0)) ChangeCreateStage(StageAbility); else GotoCharacterSelect(); return null; });
        host.Define("X2LoginCharacter", "EndCharacterAbility", a => { ChangeCreateStage(a.Bool(0) ? StageCustomize : StageCreate); return null; });
        host.Define("X2LoginCharacter", "EndCharacterCustomize", _ => { ChangeCreateStage(StageAbility); return null; });
        host.Define("X2LoginCharacter", "IsRecustomizing", _ => false);
        host.Define("X2LoginCharacter", "GetCustomizingUnit", _ => _customizingUnit == null ? null : new LuaMulti(_customizingUnit, (double)_customizingUnit.Race, (double)_customizingUnit.Gender));
        host.Define("X2LoginCharacter", "ShowPreviewRaceGender", a => { SetPreviewRaceGender(a.Str(0), a.Str(1)); return null; });
        host.Define("X2LoginCharacter", "GetRaceList", _ => RaceList());
        host.Define("X2LoginCharacter", "GetAbilityList", _ => AbilityList());
        host.Define("X2LoginCharacter", "GetSkillTooltip", a => new LuaTable { ["path"] = _catalog.SkillIcon(a.Int(0)) });
        host.Define("X2LoginCharacter", "GetLastAbility", _ => (double)_createAbilities[0]);
        host.Define("X2LoginCharacter", "ShowSkillAction", a =>
        {
            _createAbilities[0] = a.Int(0, 1);
            _skillActionSerial++;
            _skillActionInitial = _enteringStage;
            if (Environment.GetEnvironmentVariable("X2_TRACE_PREVIEW") == "1")
            {
                _host?.RunString("SCENARIO_TB = debug.traceback('', 1)", "=tb");
                _log($"[x2] X2LoginCharacter:ShowSkillAction({_createAbilities[0]}) {_host?.Lua.GetGlobal("SCENARIO_TB")?.ToString()?.Replace((char)10, ' ')}");
            }
            SetLoginAnimation("ability", _createAbilities[0]);
            CharacterPreviewChanged?.Invoke();
            return null;
        });
        host.Define("X2LoginCharacter", "CheckULCAbilityValidation", _ => true);
        host.Define("X2LoginCharacter", "NewCharacter", a => { NewCharacter(a.Table(0)); return true; });
        host.Define("X2LoginCharacter", "DeleteCharacterCheckName", a => { DeleteCharacter(a.Int(0), a.Str(1)); return null; });
        host.Define("X2LoginCharacter", "CancelCharacterDelete", a => { CancelCharacterDelete(a.Int(0)); return null; });
        host.Define("X2LoginCharacter", "IsFrozen", _ => _previewFrozen);
        host.Define("X2LoginCharacter", "SetFreeze", a => { _previewFrozen = a.Bool(0); CharacterPreviewChanged?.Invoke(); return null; });
        host.Define("X2LoginCharacter", "IsSkipSkillAction", _ => _skipSkillAction);
        host.Define("X2LoginCharacter", "SkipSkillAction", a => { _skipSkillAction = a.Bool(0); _loginAnimationVersion++; CharacterPreviewChanged?.Invoke(); return null; });
        host.Define("X2LoginCharacter", "ChangeCharacterFace", a => { _previewFaceName = a.Str(0) ?? ""; _loginAnimationVersion++; CharacterPreviewChanged?.Invoke(); return null; });
        InstallCustomizer(host);

        // dialogs (the script dialog layout runs on a native dialog window)
        host.Define("X2DialogManager", "SetHandler", a =>
        {
            // a nil task (a DLG_TASK_* constant this client does not define, e.g. in the source-only dialoghandler package) must not
            // register as task 0: that would replace the DLG_TASK_DEFAULT layout (components/dialog/common.lua) for every dialog
            if (a[0] is double task && a.Func(1) is { } f) _dialogTaskHandlers[task] = f;
            return null;
        });
        host.Define("X2DialogManager", "RequestDialog", a => RequestDialog(a.Func(0), a.Table(2)));
        host.Define("X2DialogManager", "RequestNoticeDialog", a => RequestDialog(a.Func(0), a.Table(2)));
        host.Define("X2DialogManager", "RequestDefaultDialog", a => RequestDialog(a.Func(0), a.Table(2)));
        host.Define("X2DialogManager", "OnOK", a => { CloseDialog(a.Str(0)); return null; });
        host.Define("X2DialogManager", "OnCancel", a => { CloseDialog(a.Str(0)); return null; });
        host.Define("X2DialogManager", "OnTimeOut", a => { CloseDialog(a.Str(0)); return null; });
        host.Define("X2DialogManager", "Delete", a => { CloseDialog(a.Str(0)); return null; });
    }

    /// <summary>Mouse state used by the modelview Lua drag handler in login and world stages.</summary>
    public void InstallInput(X2LuaHost host)
    {
        host.Define("X2Input", "GetMousePos", _ => host.Root.GetCursorPosition());
        host.Define("X2Input", "IsShiftKeyDown", _ => _options.IsShiftKeyDown?.Invoke() ?? false);
    }

    private X2CharacterDetails? Details(LuaArgs a)
    {
        var c = Character(a);
        if (c == null || _options.Details == null) return null;
        try { return _options.Details(c); }
        catch (Exception e) { _log($"[x2] character details: {e.Message}"); return null; }
    }

    private CharacterInfo? Character(LuaArgs a)
    {
        var i = a.Int(0);
        return i >= 1 && i <= _characters.Count ? _characters[i - 1] : null;
    }

    private string Localize(int category, string key, LuaArgs a, int first)
    {
        var text = _texts.Get(category, key);
        if (text == null) return $"{category} ?? {key}";
        text = _options.Translator.Translate(text);
        for (var i = first; i < a.Count; i++)
            text = text.Replace($"${i - first + 1}", a.Str(i) ?? "");
        return text;
    }

    private LuaTable WorldTable()
    {
        var t = new LuaTable();
        for (var i = 0; i < _worlds.Count; i++) t[(double)(i + 1)] = WorldEntry(_worlds[i]);
        return t;
    }

    private LuaTable WorldEntry(WorldInfo w)
    {
        var congestion = ParseCongestion(w.Load);
        var races = new LuaTable();
        foreach (var r in new[] { "nuian", "elf", "ferre", "hariharan", "warborn", "dwarf", "fairy", "returned" }) races[r] = (double)congestion;
        return new LuaTable
        {
            ["id"] = (double)w.Id,
            ["name"] = w.Name,
            ["parentId"] = 0.0,
            ["type"] = 0.0,
            ["entry"] = 1.0,
            ["available"] = w.Online ? 1.0 : 0.0,
            ["colorIndex"] = 0.0,
            ["congestion"] = (double)congestion,
            ["raceCongestions"] = races,
            ["chCount"] = (double)(w.Id == _selectedWorld ? _characters.Count : 0),
        };
    }

    private static int ParseCongestion(string load)
    {
        var m = Regex.Match(load ?? "", @"\d+");
        return m.Success && int.TryParse(m.Value, CultureInfo.InvariantCulture, out var v) ? Math.Clamp(v, 0, 3) : 0;
    }

    // ------------------------------------------------------------------ flow

    /// <summary>The events the client fires when the login screen comes up.</summary>
    public void StartStage(X2LuaHost host)
    {
        StartStageEvents(host.Root);
    }

    /// <summary>
    /// Brackets the building of a stage's UI (its scripts run their own initial calls while loading, e.g. the skillset page's random
    /// first pick): a ShowSkillAction made in between is the page's own, not the player's.
    /// </summary>
    public void BeginStageBuild() => _enteringStage = true;
    public void EndStageBuild() => _enteringStage = false;

    /// <summary>
    /// character_create_ability.lua creates its "Disable skill preview." check box without ever hiding it and only positions it when the
    /// skillset page opens, so on the race and appearance pages it sat unanchored in the top-left corner. The original shows nothing there.
    /// </summary>
    private static void HideAbilityPageLeftovers(UiRoot root)
    {
        foreach (var widget in root.AllWidgets().Where(w => w.Id == "checkSkipWnd").ToArray()) widget.Show(false);
    }

    private bool _showLoginPanel;

    private void StartStageEvents(UiRoot root)
    {
        switch (Stage)
        {
            case StageLogin:
                root.DispatchEvent("ENTERED_LOGIN");
                // The stock (CN) login script shows its account/password panel only when X2Debug:GetDevMode() is true, and this
                // client logs in through that panel. Dev mode is on just while the window is being shown, nothing else sees it.
                _showLoginPanel = true;
                try { root.DispatchEvent("SHOW_LOGIN_WINDOW", true); }
                finally { _showLoginPanel = false; }
                break;
            case StageServer:
                root.DispatchEvent("ENTERED_WORLD_SELECT");
                root.DispatchEvent("SHOW_SERVER_SELECT_WINDOW", true);
                break;
            case StageSelect:
                root.DispatchEvent("SHOW_CHARACTER_SELECT_WINDOW", true);
                break;
            case StageCreate:
                HideAbilityPageLeftovers(root);
                root.DispatchEvent("SHOW_CHARACTER_CREATE_WINDOW", true);
                break;
            case StageAbility:
                root.DispatchEvent("SHOW_CHARACTER_ABILITY_WINDOW", true, false);
                break;
            case StageCustomize:
                HideAbilityPageLeftovers(root);
                root.DispatchEvent("SHOW_CHARACTER_CUSTOMIZE_WINDOW", true);
                break;
            case StageWorld:
                // the client enters the world behind the loading screen, then leaves it
                root.DispatchEvent("ENTERED_WORLD", true);
                root.DispatchEvent("LEFT_LOADING");
                root.DispatchEvent("UPDATE_BINDINGS");
                break;
        }
    }

    private void Run<T>(string what, Func<CancellationToken, Task<T>> work, Action<T> done, Action<string> failed)
    {
        if (_busy)
        {
            _log($"[login] {what} ignored: busy");
            return;
        }
        _busy = true;
        SetStatus(what + " ...");
        var ct = _cts.Token;
        Task.Run(async () =>
        {
            try
            {
                var result = await work(ct).ConfigureAwait(false);
                _post(() => { _busy = false; done(result); });
            }
            catch (Exception e)
            {
                var message = e is OperationCanceledException ? "cancelled" : e.Message;
                _post(() => { _busy = false; failed(message); });
            }
        });
    }

    private void ConnectToServer(string account, string password)
    {
        var useLauncher = _options.LauncherAccount.Length > 0 && account.Trim().Equals(_options.LauncherAccount, StringComparison.OrdinalIgnoreCase);
        var host = _options.Host;
        var port = _options.Port;
        _host?.Root.DispatchEvent("SHOW_LOGIN_WINDOW", false);
        Run($"Logging in to {host}:{port} as {account.Trim()}", async ct =>
        {
            var result = await _backend.LoginAsync(host, port, useLauncher ? "" : account, useLauncher ? "" : password, ct).ConfigureAwait(false);
            if (!result.Ok) throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.Error) ? "Login failed." : result.Error);
            return await _backend.GetWorldsAsync(ct).ConfigureAwait(false);
        }, worlds =>
        {
            _worlds = worlds;
            SetStatus($"logged in; {worlds.Count} world(s)");
            Stage = StageServer;
            var root = _host!.Root;
            root.DispatchEvent("LEFT_LOGIN");
            root.DispatchEvent("ENTERED_WORLD_SELECT");
        }, error =>
        {
            SetStatus("login failed: " + error);
            var root = _host!.Root;
            root.DispatchEvent("LOGIN_DENIED");
            ShowMessage(Localize(4, "login", default, 0), error);
        });
    }

    private void RefreshWorlds()
    {
        Run("Refreshing the world list", ct => _backend.GetWorldsAsync(ct), worlds =>
        {
            _worlds = worlds;
            _host?.CallGlobal("RefreshWorldList");
        }, error => SetStatus("world list refresh failed: " + error));
    }

    private object EnterWorld(int worldId)
    {
        var world = _worlds.FirstOrDefault(w => w.Id == worldId);
        if (world == null || !world.Online)
            return new LuaMulti(false, "error_to_connect_world");
        _selectedWorld = worldId;
        SetStatus($"entering world {world.Name}");
        _post(() => _host?.Root.DispatchEvent("READY_TO_CONNECT_WORLD"));
        return true;
    }

    private void ConnectToWorld()
    {
        var world = SelectedWorld;
        if (world == null) return;
        Run($"Connecting to {world.Name}", ct => _backend.SelectWorldAsync(world.Id, ct), characters =>
        {
            _characters = characters;
            _pendingDeletes.Clear();
            _shownCharacter = characters.Count > 0 ? 1 : 0;
            SetStatus($"{characters.Count} character(s) on {world.Name}");
            Stage = StageSelect;
            StageChanged?.Invoke(Stage);
        }, error =>
        {
            SetStatus("world connection failed: " + error);
            _host?.Root.DispatchEvent("ENTER_WORLD_CANCELLED");
            ShowMessage(world.Name, error);
        });
    }

    private void SelectCharacter(int index)
    {
        if (index < 1 || index > _characters.Count) return;
        var character = _characters[index - 1];
        Run($"Entering the world as {character.Name}", async ct =>
        {
            await _backend.EnterWorldAsync(character.Id, ct).ConfigureAwait(false);
            return character;
        }, c =>
        {
            SetStatus($"in the world as {c.Name}");
            Stage = StageWorld;
            EnteredWorld?.Invoke(c);
            if (_options.HudInWorld) StageChanged?.Invoke(Stage);
        }, error =>
        {
            SetStatus("enter world failed: " + error);
            _host?.Root.TopLevel.FirstOrDefault(w => w.Id == "background")?.Enable(true, true);
            ShowMessage(character.Name, error);
        });
    }

    private void BeginCharacterCreate()
    {
        _customizingUnit = null; // a new character starts from the race defaults
        _createRace = "nuian";
        _createGender = "male";
        // A fresh creation has no skillset yet: GetLastAbility answers 0 and the ability page (character_create_ability.lua,
        // UpdateCreateAbilityUI) then picks one at random and calls ShowSkillAction, so the first skillset plays its start clip and
        // weapon exactly like every later pick. Coming back from customization it answers the chosen one and does not restart it.
        _createAbilities.Clear();
        _createAbilities.AddRange([0, 0, 0]);
        _loginAnimationAbility = 0;
        // The skillset page picks its first skillset with Lua's math.random. The original client seeds it (its first pick was not the
        // same on every visit); an unseeded state always yields the same first value here (Battlerage).
        _host?.RunString($"math.randomseed({Environment.TickCount64 % 1000003}) math.random() math.random() math.random()", "=seed");
        ChangeCreateStage(StageCreate);
    }

    private void ChangeCreateStage(int stage)
    {
        Stage = stage;
        StageChanged?.Invoke(stage);
        CharacterPreviewChanged?.Invoke();
    }

    private void GotoCharacterSelect()
    {
        Stage = StageSelect;
        StageChanged?.Invoke(Stage);
        CharacterPreviewChanged?.Invoke();
    }

    private void SetPreviewRaceGender(string? race, string? gender)
    {
        _createRace = string.IsNullOrWhiteSpace(race) ? "nuian" : race.ToLowerInvariant();
        _createGender = gender?.Equals("female", StringComparison.OrdinalIgnoreCase) == true ? "female" : "male";
        _customizingUnit?.SetRaceGender(RaceId(_createRace), _createGender == "female" ? 2 : 1);
        SetLoginAnimation("race", _loginAnimationAbility);
        CharacterPreviewChanged?.Invoke();
    }

    private void SetLoginAnimation(string kind, int ability)
    {
        _loginAnimation = kind;
        _loginAnimationAbility = ability;
        _loginAnimationVersion++;
    }

    private void NewCharacter(LuaTable? info)
    {
        var name = info?["name"]?.ToString()?.Trim() ?? "";
        var appearance = _customizingUnit?.Appearance ?? new LoginCharacterAppearance();
        // The ability stage previews several choices, but a new character starts with one selected skillset.
        var request = new CharacterCreateRequest(name, _createRace, _createGender, [Math.Max(1, _createAbilities[0]), 0, 0],
            PlayerModel(_createRace, _createGender), new Dictionary<int, long>(), appearance,
            _catalog.StartingZone(RaceId(_createRace), _createGender == "female" ? 2 : 1));
        if (System.Environment.GetEnvironmentVariable("X2_TRACE_PREVIEW") == "1")
        {
            // the CSCreateCharacter body the network backend would send, next to the modifier the preview shows
            var body = AAEmu.GodotViewer.Net.CharacterLobbyPacketWriters.CreateCharacter(request, introZoneId: request.IntroZoneId).Body;
            _log($"[x2] create modifier {Convert.ToHexString(appearance.Modifier)} packet {Convert.ToHexString(body)}");
        }
        Run($"Creating {name}", ct => _backend.CreateCharacterAsync(request, ct), character =>
        {
            _characters = _characters.Concat([character]).ToArray();
            _shownCharacter = _characters.Count;
            SetStatus($"created {character.Name}");
            Stage = StageSelect;
            StageChanged?.Invoke(Stage);
            CharacterPreviewChanged?.Invoke();
        }, error =>
        {
            SetStatus("character creation failed: " + error);
            var key = error is "error_char_name_duplicate" or "error_char_name_forbidden" or "error_char_name_pending"
                ? error : "error_char_name_forbidden";
            _host?.Root.DispatchEvent("CREATE_CHARACTER_FAILED", key);
        });
    }

    private void DeleteCharacter(int index, string? name)
    {
        var character = index >= 1 && index <= _characters.Count ? _characters[index - 1] : null;
        if (character == null) return;
        if (!character.Name.Equals(name?.Trim(), StringComparison.Ordinal))
        {
            _host?.Root.DispatchEvent("LOGIN_CHARACTER_UPDATED", "error_char_name_mismatch", (double)index);
            return;
        }
        Run($"Deleting {character.Name}", ct => _backend.DeleteCharacterAsync(character.Id, ct), result =>
        {
            if (result.Status == 1)
            {
                _characters = _characters.Where(c => c.Id != character.Id).ToArray();
                _pendingDeletes.Remove(character.Id);
                _shownCharacter = Math.Min(_shownCharacter, _characters.Count);
            }
            else if (result.Status == 2)
                _pendingDeletes[character.Id] = result.DeleteAt ?? DateTimeOffset.UtcNow;
            else
            {
                _host?.Root.DispatchEvent("LOGIN_CHARACTER_UPDATED", "error_character_delete_failed", (double)index);
                return;
            }
            SetStatus(result.Status == 1 ? $"deleted {character.Name}" : $"scheduled {character.Name} for deletion");
            _host?.Root.DispatchEvent("LOGIN_CHARACTER_UPDATED", "updated", (double)index);
            CharacterPreviewChanged?.Invoke();
        }, error =>
        {
            SetStatus("character deletion failed: " + error);
            _host?.Root.DispatchEvent("LOGIN_CHARACTER_UPDATED", "error_character_delete_failed", (double)index);
        });
    }

    private void CancelCharacterDelete(int index)
    {
        var character = index >= 1 && index <= _characters.Count ? _characters[index - 1] : null;
        if (character == null) return;
        Run($"Cancelling deletion of {character.Name}", ct => _backend.CancelCharacterDeleteAsync(character.Id, ct), result =>
        {
            if (result.Status != 3)
            {
                _host?.Root.DispatchEvent("LOGIN_CHARACTER_UPDATED", "error_cancel_character_delete_failed", (double)index);
                return;
            }
            _pendingDeletes.Remove(character.Id);
            SetStatus($"cancelled deletion of {character.Name}");
            _host?.Root.DispatchEvent("LOGIN_CHARACTER_UPDATED", "updated", (double)index);
        }, error =>
        {
            SetStatus("cancel character deletion failed: " + error);
            _host?.Root.DispatchEvent("LOGIN_CHARACTER_UPDATED", "error_cancel_character_delete_failed", (double)index);
        });
    }

    private void CharacterWasDeleted(ulong characterId)
    {
        var index = _characters.ToList().FindIndex(c => c.Id == characterId);
        if (index < 0) return;
        _characters = _characters.Where(c => c.Id != characterId).ToArray();
        _pendingDeletes.Remove(characterId);
        _shownCharacter = Math.Min(_shownCharacter, _characters.Count);
        SetStatus("character deletion completed");
        _host?.Root.DispatchEvent("LOGIN_CHARACTER_UPDATED", "updated", (double)(index + 1));
        CharacterPreviewChanged?.Invoke();
    }

    private static int RaceId(string race) => race.ToLowerInvariant() switch
    {
        "nuian" => 1, "fairy" => 2, "dwarf" => 3, "elf" => 4, "hariharan" => 5,
        "ferre" => 6, "returned" => 7, "warborn" => 8, "daru" => 9, _ => 1,
    };

    private long PlayerModel(string race, string gender) => _catalog.Model(RaceId(race),
        gender.Equals("female", StringComparison.OrdinalIgnoreCase) ? 2 : 1);

    private static LuaTable RaceList()
    {
        var result = new LuaTable();
        // Creatable rows in the characters table: nuian, dwarf, elf, hariharan, ferre, warborn.
        var races = new[] { 1, 3, 4, 5, 6, 8 };
        var n = 1;
        foreach (var race in races)
            foreach (var gender in new[] { 1, 2 })
                result[(double)n++] = new LuaTable { ["race"] = (double)race, ["gender"] = (double)gender };
        return result;
    }

    private static LuaTable AbilityList()
    {
        var result = new LuaTable();
        // the login_stage_abilities table.  The login-stage atlas has matching regions for these
        // eight names; returning every legacy tree makes ApplyButtonSkin fall back to a 40 px invalid
        // icon and collapses the script's 125 px three-column layout.
        var abilities = new (int Id, string Name, int[] PreviewSkills, double Score, int[] Tendency)[]
        {
            (1, "fight",    [13282, 10455, 23587], 3,   [9, 0, 2, 2, 4]),
            (7, "magic",    [10670, 11314, 10664], 4,   [0, 9, 1, 7, 1]),
            (6, "wild",     [16210, 10694, 23592], 2.5, [9, 0, 1, 2, 4]),
            (10, "love",    [10534, 13286, 10546], 5,   [0, 2, 7, 2, 4]),
            (11, "hatred",  [39017, 39018, 39024], 5,   [0, 9, 1, 4, 4]),
            (12, "assassin",[40333, 40334, 40335], 5,   [9, 0, 1, 4, 3]),
            (13, "madness", [44207, 44201, 44200], 5,   [8, 0, 0, 4, 5]),
            (14, "pleasure",[47961, 47965, 47973], 2,   [0, 5, 6, 5, 3]),
        };
        for (var i = 0; i < abilities.Length; i++)
        {
            var ability = abilities[i];
            result[(double)(i + 1)] = new LuaTable
            {
                ["id"] = (double)ability.Id, ["name"] = ability.Name,
                ["preview_skill_01"] = (double)ability.PreviewSkills[0],
                ["preview_skill_02"] = (double)ability.PreviewSkills[1],
                ["preview_skill_03"] = (double)ability.PreviewSkills[2], ["score"] = ability.Score,
                ["tendency_physical"] = (double)ability.Tendency[0], ["tendency_magical"] = (double)ability.Tendency[1],
                ["tendency_protect"] = (double)ability.Tendency[2], ["tendency_debuff"] = (double)ability.Tendency[3],
                ["tendency_enchant"] = (double)ability.Tendency[4],
            };
        }
        return result;
    }

    private static LuaTable RaceCongestions()
    {
        var result = new LuaTable();
        foreach (var race in new[] { 1, 3, 4, 5, 6, 8 }) result[(double)race] = 0d;
        return result;
    }

    private void InstallCustomizer(X2LuaHost host)
    {
        long Model() => PlayerModel(_createRace, _createGender);
        // Selection grids: counts and entries come from the same catalog lists the unit's setters index into.
        void Count(string method, string kind) => host.Define("X2Customizer", method, _ => (double)_catalog.Values(Model(), kind).Length);
        Count("GetNumCustomHair", "hair"); Count("GetNumCustomHorn", "horn");
        Count("GetNumCustomSkinColor", "skin"); Count("GetNumCustomTail", "tail"); Count("GetNumCustomizingBodyNormal", "body_normal");
        Count("GetNumCustomizingFaceDiffuse", "face"); Count("GetNumCustomizingFaceNormal", "face_normal");
        Count("GetNumCustomizingDeco", "deco"); Count("GetNumCustomizingEyebrow", "eyebrow");
        Count("GetNumCustomizingMakeUp", "makeup"); Count("GetNumCustomizingPupil", "pupil");
        Count("GetNumCustomizingScar", "scar"); Count("GetNumCustomizingTattoo", "tattoo");
        LuaTable? Entry(LoginCustomizingItem? item) => item == null ? null : new LuaTable
        {
            ["iconPath"] = item.IconPath, ["new"] = item.IsNew, ["twoTone"] = item.TwoTone, ["usePallet"] = item.UsePallet,
        };
        void Item(string method, string kind) => host.Define("X2Customizer", method, a => Entry(_catalog.Item(Model(), kind, a.Int(0))));
        Item("GetCustomHairItem", "hair"); Item("GetCustomHornItem", "horn"); Item("GetCustomTailItem", "tail");
        Item("GetCustomFaceNormalItem", "face_normal"); Item("GetCustomSkinColorItem", "skin"); Item("GetCustomBodyNormalItem", "body_normal");
        Item("GetCustomEyebrowItem", "eyebrow"); Item("GetCustomPupilItem", "pupil"); Item("GetCustomMakeUpItem", "makeup");
        Item("GetCustomTattooItem", "tattoo"); Item("GetCustomScarItem", "scar"); Item("GetCustomDecoItem", "deco");
        // hair colours belong to the chosen hair, horn colours to the chosen horn
        host.Define("X2Customizer", "GetNumCustomHairColor", _ => (double)_catalog.HairColors(Model(), _customizingUnit?.Appearance.HairItemId ?? 0).Length);
        host.Define("X2Customizer", "GetNumCustomHornColor", _ => (double)_catalog.HornColors(Model(), _customizingUnit?.Appearance.HornItemId ?? 0).Length);
        host.Define("X2Customizer", "GetCustomHornColorItem", a =>
        {
            var colors = _catalog.HornColors(Model(), _customizingUnit?.Appearance.HornItemId ?? 0);
            var i = a.Int(0);
            return Entry(i >= 1 && i <= colors.Length ? colors[i - 1] : null);
        });
        host.Define("X2Customizer", "GetNumCustomizingPreviewCloth", _ => (double)_catalog.PreviewCloths(Model()).Length);
        host.Define("X2Customizer", "GetCustomPreviewClothItem", a =>
        {
            var cloths = _catalog.PreviewCloths(Model());
            var i = a.Int(0);
            return Entry(i >= 1 && i <= cloths.Length ? cloths[i - 1] : null);
        });

        // Presets: part 5 = whole look (total_character_customs), 1-4 = eyes, nose, mouth, face shape (custom_face_presets).
        host.Define("X2Customizer", "GetPresetCount", a => (double)(a.Int(0) == LoginCustomizationCatalog.PartTotal
            ? _catalog.TotalPresets(Model()).Length : _catalog.FacePresets(Model(), a.Int(0)).Length));
        host.Define("X2Customizer", "GetTotalPresetItem", a => PresetIcon(LoginCustomizationCatalog.PartTotal, a.Int(0)));
        host.Define("X2Customizer", "GetFacePresetEyeItem", a => PresetIcon(LoginCustomizationCatalog.PartEye, a.Int(0)));
        host.Define("X2Customizer", "GetFacePresetNoseItem", a => PresetIcon(LoginCustomizationCatalog.PartNose, a.Int(0)));
        host.Define("X2Customizer", "GetFacePresetLipItem", a => PresetIcon(LoginCustomizationCatalog.PartMouth, a.Int(0)));
        host.Define("X2Customizer", "GetFacePresetShapeItem", a => PresetIcon(LoginCustomizationCatalog.PartShape, a.Int(0)));
        string? PresetIcon(int part, int index)
        {
            var icons = part == LoginCustomizationCatalog.PartTotal
                ? _catalog.TotalPresets(Model()).Select(p => p.IconPath).ToArray()
                : _catalog.FacePresets(Model(), part).Select(p => p.IconPath).ToArray();
            return index >= 1 && index <= icons.Length ? icons[index - 1] : null;
        }

        // Face sliders (modifier.lua): targets of a part from the race's *_targets.xml; a target index is its modifier byte.
        host.Define("X2Customizer", "GetNumFaceTargets", a => (double)_catalog.FaceTargets(Model(), a.Int(0)).Length);
        host.Define("X2Customizer", "GetFaceTargetIndex", a =>
        {
            var targets = _catalog.FaceTargets(Model(), a.Int(0));
            var i = a.Int(1);
            return i >= 0 && i < targets.Length ? (object)(double)targets[i].Index : null;
        }, nullIsNil: true);
        host.Define("X2Customizer", "GetFaceTargetName", a => _catalog.FaceTarget(Model(), a.Int(0))?.Name ?? "");
        host.Define("X2Customizer", "GetFaceTargetMinValue", a => _catalog.FaceTarget(Model(), a.Int(0))?.Min ?? -100d);
        host.Define("X2Customizer", "GetFaceTargetMaxValue", a => _catalog.FaceTarget(Model(), a.Int(0))?.Max ?? 100d);
    }

    /// <summary>
    /// Creation preview clothes: the pack the customization screen's Preview menu tries on (all of its slots, so it replaces the
    /// race's default preview pack). Preview only; the create request carries no equipment.
    /// </summary>
    private Dictionary<int, long> CreationPreviewEquipment() =>
        Stage == StageCustomize && _customizingUnit is { PreviewClothPack: > 0 } unit
            ? _catalog.PreviewClothEquipment(unit.PreviewClothPack) : new Dictionary<int, long>();

    private object LeaveWorld(int target)
    {
        switch (target)
        {
            case 0: // EXIT_CLIENT
                ExitRequested?.Invoke();
                return true;
            case 2 when Stage == StageSelect: // EXIT_TO_WORLD_LIST
                Stage = StageServer;
                StageChanged?.Invoke(Stage);
                return true;
            default:
                return false;
        }
    }

    private void ReturnToLogin()
    {
        CancelPending();
        Stage = StageLogin;
        StageChanged?.Invoke(Stage);
    }

    // ------------------------------------------------------------------ dialogs

    /// <summary>
    /// X2DialogManager:RequestDialog: a native "window" with btnOk/btnCancel is laid out by the script's
    /// DLG_TASK_DEFAULT handler, then filled by the caller's handler, then shown.
    /// </summary>
    private object? RequestDialog(LuaFunctionReference? handler, LuaTable? info)
    {
        var host = _host;
        if (host == null) return "0";
        var id = $"dialog{++_dialogCounter}";
        var root = host.Root;
        var wnd = root.CreateWidget("window", id, null);
        wnd.Show(false);
        var ok = wnd.CreateChildWidget("button", "btnOk", 0, true);
        var cancel = wnd.CreateChildWidget("button", "btnCancel", 0, true);
        host.Lua.SetObjectField(wnd, "btnOk", ok);
        host.Lua.SetObjectField(wnd, "btnCancel", cancel);
        var infoTable = info ?? new LuaTable();
        if (!infoTable.ContainsKey("buttonType")) infoTable["buttonType"] = 0.0;
        if ((infoTable["buttonType"] as double?) == 1.0) cancel.Show(false);
        _dialogs[id] = wnd;
        if (_dialogTaskHandlers.TryGetValue(0.0, out var layout)) host.Call(layout, [wnd, infoTable], "dialog layout");
        if (handler != null) host.Call(handler, [wnd], "dialog handler");
        wnd.Show(true);
        return id;
    }

    private void CloseDialog(string? id)
    {
        if (id == null || !_dialogs.Remove(id, out var wnd)) return;
        _post(() => { if (!wnd.Destroyed) _host?.Root.Destroy(wnd); });
    }

    /// <summary>Shows a message box through the script dialog system.</summary>
    public void ShowMessage(string title, string body)
    {
        var host = _host;
        if (host == null) return;
        host.Lua.SetGlobal("__x2_msg_title", title);
        host.Lua.SetGlobal("__x2_msg_body", body);
        host.RunString("""
            local function handler(dlg)
                dlg:SetTitle(__x2_msg_title)
                local textData = { [W_MODULE.ATTRIBUTE.TEXT] = __x2_msg_body }
                dlg:RegisterStack(W_MODULE:Create("textbox", dlg, W_MODULE.TYPES.TEXTBOX, textData))
            end
            X2DialogManager:RequestDialog(handler, UIParent:GetId(), { buttonType = DBT_OK })
            """, "=x2message");
    }
}
