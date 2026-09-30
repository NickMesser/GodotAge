#nullable enable
using System.Collections.ObjectModel;

namespace AAEmu.GodotViewer.Ui.X2.Scripting.Api;

/// <summary>A saved client option, including the text metadata used to build the options window.</summary>
public sealed record X2OptionInfo
{
    public int Id { get; init; }
    public string Title { get; init; } = "";
    public string Tooltip { get; init; } = "";
    public bool Restart { get; init; }
    public string FeatureSet { get; init; } = "";
    public int FeatureSetCondition { get; init; }
    public object? Value { get; init; }
}

/// <summary>A hotkey entry exposed by X2Option:GetHotkeyInfo.</summary>
public sealed record X2HotkeyInfo
{
    public string HotkeyActionName { get; init; } = "";
    public string Title { get; init; } = "";
    public string Tooltip { get; init; } = "";
    public bool Restart { get; init; }
    public string FeatureSet { get; init; } = "";
    public int FeatureSetCondition { get; init; }
}

public sealed record X2Resolution(int Width, int Height, int BitsPerPixel = 32);
public sealed record X2AaFormat(int Samples, int Quality, bool Txaa);
public sealed record X2InputState(double MouseX, double MouseY, bool Alt, bool Control, bool Shift, string Language = "English");

/// <summary>The item currently carried by the client cursor. A zero bag slot means that no item is picked.</summary>
public sealed record X2CursorState(string? Info = null, int BagSlot = 0, long Amount = 0, string Icon = "");

public enum X2HotkeyKind { Action, Button, Item, Spell }

/// <summary>Second-password state sent by the login/world service.</summary>
public sealed record X2SecurityState
{
    public bool Created { get; init; }
    public bool Locked { get; init; }
    public bool Passed { get; init; }
    public int FailedCount { get; init; }
    public int MaximumFailedCount { get; init; }
    public DateTimeOffset? UnlockTime { get; init; }
    public DateTimeOffset? ClearReservationTime { get; init; }
}

public sealed record X2CompositionLimit(int Grade, string Name, int CompositionLimit);
public sealed record X2SurveySummary(int Type, string Title, int Progress);
public sealed record X2SurveyReward(int ItemType, int ItemNum);

public sealed record X2SurveyData
{
    public int Type { get; init; }
    public int Progress { get; init; }
    public DateTimeOffset StartsAt { get; init; }
    public DateTimeOffset EndsAt { get; init; }
    public string Title { get; init; } = "";
    public string Description { get; init; } = "";
    public X2SurveyReward? Item { get; init; }
}

/// <summary>Question data is deliberately extensible: question kinds have different native fields.</summary>
public sealed record X2SurveyQuestion(int Kind, IReadOnlyDictionary<string, object?> Fields);
public sealed record X2SurveyReply(int SurveyType, IReadOnlyList<object?> Answers);

public sealed record X2BookPageContent(string Text, string Illust = "");
public sealed record X2BookPage(int Type, string Title, IReadOnlyList<X2BookPageContent> Contents);
public sealed record X2BookChapter(string Name, IReadOnlyList<int> Pages, int StartPage = 0);
public sealed record X2BookInfo(int Type, string Name, bool UseContent, IReadOnlyList<X2BookChapter> Contents);

/// <summary>A native mini-scoreboard row. Extra carries module-specific scalar fields.</summary>
public sealed record X2MiniScoreboardRow
{
    public string Type { get; init; } = "";
    public int VisibleOrder { get; init; }
    public string ModuleType { get; init; } = "";
    public string Name { get; init; } = "";
    public long CurrentHp { get; init; }
    public long MaximumHp { get; init; }
    public IReadOnlyDictionary<string, object?> Extra { get; init; } = new ReadOnlyDictionary<string, object?>(new Dictionary<string, object?>());
}

public sealed record X2MiniScoreboardSection
{
    public string Name { get; init; } = "";
    public string Footer { get; init; } = "";
    public string FooterGuide { get; init; } = "";
    public int VisibleOrder { get; init; }
    public string Type { get; init; } = "";
    public IReadOnlyList<X2MiniScoreboardRow> Rows { get; init; } = [];
}

public sealed record X2AddonInfo(string Name, bool Enabled, IReadOnlyDictionary<string, object?> Fields);
public sealed record X2EscMenuItem(int UiContentType, int VisibleOrder, string IconKey, string BadgeColorKey, IReadOnlyList<string> FeatureSet);
public sealed record X2EscMenuCategory(int Id, string Name, int VisibleOrder, string IconKey, IReadOnlyList<X2EscMenuItem> Menus);

/// <summary>
/// Live state and commands needed by the client-side system namespaces. Implementations adapt these operations to the
/// network session, settings store, input/audio services and local UI persistence; no Godot type crosses this boundary.
/// </summary>
public interface IX2SystemData
{
    bool IsWorldEntered { get; }
    int InstanceIndex { get; }
    bool IsDeveloperMode { get; }
    bool IsGameMaster { get; }
    X2InputState Input { get; }
    X2CursorState Cursor { get; }
    X2SecurityState Security { get; }
    DateTimeOffset ServerTime { get; }
    TimeSpan GameTime { get; }
    long UiMilliseconds { get; }

    object? GetCharacterUiData(string key);
    void SetCharacterUiData(string key, object? value);
    object? GetUiData(string store, string key);
    void SetUiData(string store, string key, object? value);
    IReadOnlyList<X2AddonInfo> GetAddons();
    IReadOnlyList<X2EscMenuCategory> EscMenuCategories { get; }
    void SetAddonEnabled(string name, bool enabled);
    string? GetFeatureSet(string name);
    void ExecuteSystemCommand(string name, IReadOnlyList<object?> arguments);

    object? GetOption(int id);
    object? GetOption(string name);
    X2OptionInfo? GetOptionInfo(int id);
    IReadOnlyList<X2HotkeyInfo> GetHotkeyInfo(int actionType);
    IReadOnlyList<int> GetSubOptionItemList(int optionId, int selected);
    IReadOnlyList<X2Resolution> Resolutions { get; }
    IReadOnlyList<X2AaFormat> AntiAliasingFormats { get; }
    IReadOnlyList<int> CursorSizes { get; }
    IReadOnlyList<int> CursorShapes { get; }
    void CreateOption(string name, object? defaultValue, int saveLevel);
    void SetOption(int id, object? value, bool markModified = true);
    void SetOption(string name, object? value, bool markModified = true);
    void SetOptionDefault(int id, object? value);
    void SetOptionDefault(string name, object? value);
    bool HasModifiedRestartOption { get; }
    void ResetOptions();
    void RevertOptions();
    void SaveOptions();

    string? GetHotkey(X2HotkeyKind kind, string action, int index, int argument = 0, bool option = false, bool temporary = false);
    void SetHotkey(X2HotkeyKind kind, string action, string? key, int index, int argument = 0, bool option = false, bool temporary = false);
    bool IsValidHotkeyAction(string action);
    bool IsOverridableHotkeyAction(string action);
    void EnableHotkeys(bool enabled);
    void ExecuteHotkeyAction(string action);
    void CopyHotkeysToOptions();
    void CopyOptionsToHotkeys();
    void SaveHotkeys();

    long PlayUiSound(string name, bool duplicable);
    bool IsSoundPlaying(long soundId);
    void StopSound(long soundId, int mode);
    void PlayMusic(string name);
    void StopMusic();
    void SetSiegeMusic(bool enabled);

    void ClearCursor();
    void SetCursorImage(string path, int hotX, int hotY);
    void SetInputLanguage(string language);
    void SetUnitCameraAngles(object? angles);
    void ShakeCamera(object? shakeInfo);
    bool IsScreenshotCameraMode { get; }

    string? GetConsoleAttribute(string name);
    void ExecuteConsole(string command);
    IReadOnlyList<string> GmConsoleCommands { get; }
    IReadOnlyList<string> GmBookmarks { get; }
    void ExecuteGmCommand(string name, IReadOnlyList<object?> arguments);

    void RequestSecurityAction(string action, IReadOnlyList<string> arguments);
    IReadOnlyList<X2CompositionLimit> CompositionLimits { get; }
    void PlayMusicSheet(string musicSheet);
    void StopMusicSheet();
    bool PrepareMusicSheet(string itemId, string title, string musicSheet);
    void SavePreparedMusicSheet();

    IReadOnlyList<X2SurveySummary> Surveys { get; }
    X2SurveyData? GetSurvey(int type);
    IReadOnlyList<X2SurveyQuestion> GetSurveyQuestions(int type);
    bool CanSurvey(int status);
    void SendSurveyReply(X2SurveyReply reply);

    X2BookInfo? GetBook(int type);
    X2BookPage? GetBookPage(int type);
    IReadOnlyList<X2MiniScoreboardSection> MiniScoreboard { get; }
    void RefreshNameTags();
}

/// <summary>
/// Offline/fresh-character system state. It deliberately has no surveys, books, scoreboards, cursor item, GM access or
/// second password, but keeps locally-created options and hotkeys so the stock options window remains usable.
/// </summary>
public class NullSystemData : IX2SystemData
{
    private readonly Dictionary<int, object?> _options = [];
    private readonly Dictionary<int, object?> _optionDefaults = [];
    private readonly Dictionary<int, object?> _savedOptions = [];
    private readonly Dictionary<string, object?> _namedOptions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, object?> _namedDefaults = new(StringComparer.Ordinal);
    private readonly Dictionary<string, object?> _savedNamedOptions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, object?> _characterUi = new(StringComparer.Ordinal);
    private readonly Dictionary<string, object?> _ui = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _hotkeys = new(StringComparer.Ordinal);
    private long _nextSoundId;

    // English values from the client database (ui_esc_menu_categories,
    // ui_esc_menus, localized_texts.en_us). The Lua system menu uses this static content before
    // any server state is available.
    private static readonly IReadOnlyList<X2EscMenuCategory> BuiltInEscMenuCategories =
    [
        new(1, "Characters", 1, "character", [
            new(21, 1, "info", "situation_03", []), new(22, 2, "bag", "situation_03", []),
            new(26, 3, "skill", "situation_01", []), new(23, 4, "quest", "", []),
            new(114, 5, "chronicle", "", []), new(25, 6, "achievement", "", []),
            new(120, 7, "community", "", []), new(66, 8, "uthtin", "", [])]),
        new(2, "Combat", 2, "combat", [
            new(4, 1, "ranking", "", []), new(33, 2, "raid", "", []), new(48, 3, "hero", "", [])]),
        new(3, "Shop/Quality of Life", 3, "shop", [
            new(58, 1, "auction", "", []), new(78, 2, "purchase", "situation_01", []),
            new(51, 3, "item_encyclopedia", "", []), new(115, 4, "butler", "", []), new(79, 5, "mail", "", [])]),
        new(4, "Vocation", 4, "convenience", [
            new(27, 1, "map", "", []), new(28, 2, "folio", "", []), new(31, 3, "public_farm", "", []),
            new(41, 4, "trade", "", [])]),
        new(5, "System", 5, "system", [
            new(49, 1, "guide", "", []), new(121, 2, "optimizer", "", []), new(37, 3, "message", "situation_03", []),
            new(38, 4, "dairy", "", []), new(39, 5, "wiki", "", []), new(40, 6, "faq", "situation_03", []),
            new(122, 7, "lock", "", [])]),
    ];

    public static NullSystemData Instance { get; } = new();
    public virtual bool IsWorldEntered => false;
    public virtual int InstanceIndex => 0;
    public virtual bool IsDeveloperMode => false;
    public virtual bool IsGameMaster => false;
    public virtual X2InputState Input { get; private set; } = new(0, 0, false, false, false);
    public virtual X2CursorState Cursor { get; private set; } = new();
    public virtual X2SecurityState Security { get; } = new();
    public virtual DateTimeOffset ServerTime => DateTimeOffset.UtcNow;
    public virtual TimeSpan GameTime => DateTimeOffset.Now.TimeOfDay;
    public virtual long UiMilliseconds => Environment.TickCount64;
    public virtual IReadOnlyList<X2Resolution> Resolutions { get; } = [new(1280, 720), new(1600, 900), new(1920, 1080), new(2560, 1440), new(3840, 2160)];
    public virtual IReadOnlyList<X2AaFormat> AntiAliasingFormats { get; } = [new(2, 0, false), new(4, 0, false), new(8, 0, false)];
    public virtual IReadOnlyList<int> CursorSizes { get; } = [0, 1, 2];
    public virtual IReadOnlyList<int> CursorShapes { get; } = [0, 1];
    public virtual bool HasModifiedRestartOption => false;
    public virtual bool IsScreenshotCameraMode => false;
    public virtual IReadOnlyList<string> GmConsoleCommands => [];
    public virtual IReadOnlyList<string> GmBookmarks => [];
    public virtual IReadOnlyList<X2CompositionLimit> CompositionLimits => [];
    public virtual IReadOnlyList<X2SurveySummary> Surveys => [];
    public virtual IReadOnlyList<X2MiniScoreboardSection> MiniScoreboard => [];

    public virtual object? GetCharacterUiData(string key) => _characterUi.GetValueOrDefault(key);
    public virtual void SetCharacterUiData(string key, object? value) => _characterUi[key] = value;
    public virtual object? GetUiData(string store, string key) => _ui.GetValueOrDefault(store + "\0" + key);
    public virtual void SetUiData(string store, string key, object? value) => _ui[store + "\0" + key] = value;
    public virtual IReadOnlyList<X2AddonInfo> GetAddons() => [];
    public virtual IReadOnlyList<X2EscMenuCategory> EscMenuCategories => BuiltInEscMenuCategories;
    public virtual void SetAddonEnabled(string name, bool enabled) { }
    public virtual string? GetFeatureSet(string name) => null;
    public virtual void ExecuteSystemCommand(string name, IReadOnlyList<object?> arguments) { }

    // The stock action bar's InitOption reads OIT_SHOWACTIONBAR_1 (135) at load time.
    // An unset option is the client's visible main-bar default; the remaining bars
    // still default to hidden until explicitly enabled by the player.
    public virtual object? GetOption(int id) => _options.GetValueOrDefault(id, id == 135 ? 1d : 0d);
    public virtual object? GetOption(string name) => _namedOptions.GetValueOrDefault(name);
    public virtual X2OptionInfo? GetOptionInfo(int id) => X2UiOptionCatalog.Option(id) is { } o
        ? new() { Id = id, Value = GetOption(id), Title = o.Title, Tooltip = o.Tooltip, FeatureSet = o.FeatureSet, FeatureSetCondition = o.FeatureSetCondition ? 1 : 0 }
        : new() { Id = id, Value = GetOption(id) };
    public virtual IReadOnlyList<X2HotkeyInfo> GetHotkeyInfo(int actionType) => X2UiOptionCatalog.Hotkey(actionType) is { } h
        ? [new() { HotkeyActionName = X2UiOptionCatalog.ActionName(actionType) ?? $"hotkey_{actionType}", Title = h.Title, Tooltip = h.Tooltip,
            FeatureSet = h.FeatureSet, FeatureSetCondition = h.FeatureSetCondition ? 1 : 0 }]
        : [new() { HotkeyActionName = X2UiOptionCatalog.ActionName(actionType) ?? $"hotkey_{actionType}" }];
    public virtual IReadOnlyList<int> GetSubOptionItemList(int optionId, int selected) => [];
    public virtual void CreateOption(string name, object? defaultValue, int saveLevel)
    {
        if (!_namedDefaults.ContainsKey(name)) _namedDefaults[name] = defaultValue;
        if (!_namedOptions.ContainsKey(name)) _namedOptions[name] = defaultValue;
        if (!_savedNamedOptions.ContainsKey(name)) _savedNamedOptions[name] = defaultValue;
    }
    public virtual void SetOption(int id, object? value, bool markModified = true) => _options[id] = value;
    public virtual void SetOption(string name, object? value, bool markModified = true) => _namedOptions[name] = value;
    public virtual void SetOptionDefault(int id, object? value) { _optionDefaults[id] = value; _options.TryAdd(id, value); _savedOptions.TryAdd(id, value); }
    public virtual void SetOptionDefault(string name, object? value) { _namedDefaults[name] = value; _namedOptions.TryAdd(name, value); _savedNamedOptions.TryAdd(name, value); }
    public virtual void ResetOptions()
    {
        _options.Clear(); foreach (var pair in _optionDefaults) _options[pair.Key] = pair.Value;
        _namedOptions.Clear(); foreach (var pair in _namedDefaults) _namedOptions[pair.Key] = pair.Value;
    }
    public virtual void RevertOptions()
    {
        _options.Clear(); foreach (var pair in _savedOptions) _options[pair.Key] = pair.Value;
        _namedOptions.Clear(); foreach (var pair in _savedNamedOptions) _namedOptions[pair.Key] = pair.Value;
    }
    public virtual void SaveOptions()
    {
        _savedOptions.Clear(); foreach (var pair in _options) _savedOptions[pair.Key] = pair.Value;
        _savedNamedOptions.Clear(); foreach (var pair in _namedOptions) _savedNamedOptions[pair.Key] = pair.Value;
    }

    public virtual string? GetHotkey(X2HotkeyKind kind, string action, int index, int argument = 0, bool option = false, bool temporary = false)
        => _hotkeys.GetValueOrDefault(HotkeyKey(kind, action, index, argument, option, temporary));
    public virtual void SetHotkey(X2HotkeyKind kind, string action, string? key, int index, int argument = 0, bool option = false, bool temporary = false)
    {
        var id = HotkeyKey(kind, action, index, argument, option, temporary);
        if (string.IsNullOrEmpty(key)) _hotkeys.Remove(id); else _hotkeys[id] = key;
    }
    public virtual bool IsValidHotkeyAction(string action) => !string.IsNullOrWhiteSpace(action);
    public virtual bool IsOverridableHotkeyAction(string action) => IsValidHotkeyAction(action);
    public virtual void EnableHotkeys(bool enabled) { }
    public virtual void ExecuteHotkeyAction(string action) { }
    public virtual void CopyHotkeysToOptions() { }
    public virtual void CopyOptionsToHotkeys() { }
    public virtual void SaveHotkeys() { }

    public virtual long PlayUiSound(string name, bool duplicable) => Interlocked.Increment(ref _nextSoundId);
    public virtual bool IsSoundPlaying(long soundId) => false;
    public virtual void StopSound(long soundId, int mode) { }
    public virtual void PlayMusic(string name) { }
    public virtual void StopMusic() { }
    public virtual void SetSiegeMusic(bool enabled) { }
    public virtual void ClearCursor() => Cursor = new();
    public virtual void SetCursorImage(string path, int hotX, int hotY) { }
    public virtual void SetInputLanguage(string language) => Input = Input with { Language = language };
    public virtual void SetUnitCameraAngles(object? angles) { }
    public virtual void ShakeCamera(object? shakeInfo) { }

    public virtual string? GetConsoleAttribute(string name) => null;
    public virtual void ExecuteConsole(string command) { }
    public virtual void ExecuteGmCommand(string name, IReadOnlyList<object?> arguments) { }
    public virtual void RequestSecurityAction(string action, IReadOnlyList<string> arguments) { }
    public virtual void PlayMusicSheet(string musicSheet) { }
    public virtual void StopMusicSheet() { }
    public virtual bool PrepareMusicSheet(string itemId, string title, string musicSheet) => false;
    public virtual void SavePreparedMusicSheet() { }
    public virtual X2SurveyData? GetSurvey(int type) => null;
    public virtual IReadOnlyList<X2SurveyQuestion> GetSurveyQuestions(int type) => [];
    public virtual bool CanSurvey(int status) => false;
    public virtual void SendSurveyReply(X2SurveyReply reply) { }
    public virtual X2BookInfo? GetBook(int type) => null;
    public virtual X2BookPage? GetBookPage(int type) => null;
    public virtual void RefreshNameTags() { }

    private static string HotkeyKey(X2HotkeyKind kind, string action, int index, int argument, bool option, bool temporary)
        => $"{(int)kind}:{action}:{index}:{argument}:{option}:{temporary}";
}
