#nullable enable

namespace AAEmu.GodotViewer.Ui.X2.Scripting.Api;

/// <summary>
/// UI events consumed by the system-family X2UI windows.
/// </summary>
/// <remarks>
/// Argument descriptions record only values that the Lua handlers actually read. Events described
/// as having no arguments may still be raised with payload values that the handlers ignore.
/// </remarks>
public static class X2SystemEvents
{
    // System menu and options.

    /// <summary>No arguments are read. Opens the system configuration window.</summary>
    public const string OpenConfig = "OPEN_CONFIG";

    /// <summary>
    /// Reads <c>waitTime</c>, <c>exitTarget</c>, and <c>idleKick</c> to configure the leave-world dialog.
    /// </summary>
    public const string LeavingWorldStarted = "LEAVING_WORLD_STARTED";

    /// <summary>No arguments are read. Closes the leave-world dialog.</summary>
    public const string LeavingWorldCanceled = "LEAVING_WORLD_CANCELED";

    /// <summary>No arguments are read. Hides system UI while loading.</summary>
    public const string EnteredLoading = "ENTERED_LOADING";

    /// <summary>No arguments are read. Refreshes system windows after loading.</summary>
    public const string LeftLoading = "LEFT_LOADING";

    /// <summary>No arguments are read by the system-family handlers.</summary>
    public const string EnteredWorld = "ENTERED_WORLD";

    /// <summary>No arguments are read. Re-evaluates whether an ESC-menu entry is enabled.</summary>
    public const string UiPermissionUpdate = "UI_PERMISSION_UPDATE";

    /// <summary>No arguments are read. Refreshes the damaged-equipment badge.</summary>
    public const string UnitEquipmentChanged = "UNIT_EQUIPMENT_CHANGED";

    /// <summary>No arguments are read. Refreshes the empty-bag-slot badge.</summary>
    public const string BagUpdate = "BAG_UPDATE";

    /// <summary>No arguments are read. Refreshes the empty-bag-slot badge.</summary>
    public const string BagExpanded = "BAG_EXPANDED";

    /// <summary>No arguments are read. Refreshes the empty-bag-slot badge.</summary>
    public const string ChangeEmptyBagSlotCounterDisplay = "CHANGE_EMPTY_BAG_SLOT_COUNTER_DISPLAY";

    /// <summary>No arguments are read. Refreshes the available-skill-point badge.</summary>
    public const string SkillLearned = "SKILL_LEARNED";

    /// <summary>No arguments are read. Refreshes the available-skill-point badge.</summary>
    public const string SkillChanged = "SKILL_CHANGED";

    /// <summary>No arguments are read. Refreshes the available-skill-point badge.</summary>
    public const string AbilityChanged = "ABILITY_CHANGED";

    /// <summary>No arguments are read. Refreshes the available-skill-point badge.</summary>
    public const string LevelChanged = "LEVEL_CHANGED";

    /// <summary>No arguments are read. Refreshes the available-skill-point badge.</summary>
    public const string SkillsReset = "SKILLS_RESET";

    /// <summary>No arguments are read. Refreshes the available-skill-point badge.</summary>
    public const string AbilitySetChanged = "ABILITY_SET_CHANGED";

    /// <summary>No arguments are read. Refreshes the available-skill-point badge.</summary>
    public const string UpdateSkillPoint = "UPDATE_SKILL_POINT";

    /// <summary>No arguments are read. Refreshes the heir-level alarm.</summary>
    public const string HeirLevelUp = "HEIR_LEVEL_UP";

    /// <summary>No arguments are read. Refreshes the heir-level alarm.</summary>
    public const string ExpChanged = "EXP_CHANGED";

    /// <summary>No arguments are read. Refreshes the heir-level alarm.</summary>
    public const string HeirSkillUpdate = "HEIR_SKILL_UPDATE";

    /// <summary>
    /// Reads <c>questType</c> and <c>status</c>; status is compared with <c>started</c> and <c>updated</c>.
    /// </summary>
    public const string QuestContextUpdated = "QUEST_CONTEXT_UPDATED";

    /// <summary>No arguments are read. Re-evaluates the main-quest alarm.</summary>
    public const string UiReloaded = "UI_RELOADED";

    /// <summary>No arguments are read. Re-evaluates the pending-friend alarm.</summary>
    public const string WaitFriendAddAlarm = "WAIT_FRIEND_ADD_ALARM";

    /// <summary>No arguments are read. Refreshes the unread commercial-mail badge.</summary>
    public const string GoodsMailInboxUpdate = "GOODS_MAIL_INBOX_UPDATE";

    /// <summary>No arguments are read. Refreshes the optimization state badge.</summary>
    public const string OptimizationResultMessage = "OPTIMIZATION_RESULT_MESSAGE";

    /// <summary>Reads <c>count</c> and displays it on the web-messenger badge when positive.</summary>
    public const string SetWebMessengeCount = "SET_WEB_MESSENGE_COUNT";

    /// <summary>
    /// Reads <c>messageType</c> and the third payload value <c>countText</c>; the second payload value is ignored.
    /// </summary>
    public const string SetUiMessage = "SET_UI_MESSAGE";

    /// <summary>
    /// Reads <c>overridden</c>, ignores <c>oldAction</c>, and reads <c>newAction</c> when showing the override tooltip.
    /// </summary>
    public const string UpdateOptionBindings = "UPDATE_OPTION_BINDINGS";

    /// <summary>
    /// Reads <c>optionType</c> and <c>infoTable.display</c> when <c>optionType</c> is the show-FPS option.
    /// </summary>
    public const string ChangeOption = "CHANGE_OPTION";

    // Second password.

    /// <summary>No arguments are read. Hides all second-password windows and refreshes the ESC-menu state.</summary>
    public const string SecondPasswordCreationCompleted = "SECOND_PASSWORD_CREATION_COMPLETED";

    /// <summary>No arguments are read. Hides all second-password windows.</summary>
    public const string SecondPasswordChangeCompleted = "SECOND_PASSWORD_CHANGE_COMPLETED";

    /// <summary>No arguments are read. Hides all second-password windows and refreshes the ESC-menu state.</summary>
    public const string SecondPasswordClearCompleted = "SECOND_PASSWORD_CLEAR_COMPLETED";

    /// <summary>No arguments are read. Hides all second-password windows.</summary>
    public const string SecondPasswordCheckCompleted = "SECOND_PASSWORD_CHECK_COMPLETED";

    /// <summary>No arguments are read. Hides all second-password windows and refreshes the ESC-menu state.</summary>
    public const string SecondPasswordCheckOverFailed = "SECOND_PASSWORD_CHECK_OVER_FAILED";

    /// <summary>No arguments are read. Shows the recommendation dialog.</summary>
    public const string ShowRecommendUsingSecondPassword = "SHOW_RECOMMEND_USING_SECOND_PASSWORD";

    // User music.

    /// <summary>
    /// Reads <c>isShow</c>, <c>itemIdString</c>, and a third value named <c>isWide</c> by the handler.
    /// Guessed signature: the third value is a numeric score-length limit because it is passed to
    /// <c>CreateScoreWindow</c> as <c>itemLimit</c> and used in numeric comparisons.
    /// </summary>
    public const string OpenMusicSheet = "OPEN_MUSIC_SHEET";

    /// <summary>No arguments are read. Closes the music-sheet editor.</summary>
    public const string CloseMusicSheet = "CLOSE_MUSIC_SHEET";

    // Survey form.

    /// <summary>No arguments are read. Refreshes the survey list, detail, and alarm state.</summary>
    public const string SurveyFormUpdate = "SURVEY_FORM_UPDATE";

    // Book and paper windows.

    /// <summary>No arguments are read. Closes an open page or book when its interaction ends.</summary>
    public const string InteractionEnd = "INTERACTION_END";

    /// <summary>
    /// Reads <c>type</c> (<c>page</c> or <c>book</c>) and <c>index</c> to choose and populate the paper window.
    /// </summary>
    public const string OpenPaper = "OPEN_PAPER";

    /// <summary>No arguments are read. Closes open page and book windows.</summary>
    public const string DestroyPaper = "DESTROY_PAPER";

    // Clock and camera-mode HUD.

    /// <summary>Reads <c>zoneId</c> and refreshes the current zone-state display.</summary>
    public const string HpwZoneStateChange = "HPW_ZONE_STATE_CHANGE";

    /// <summary>Reads <c>zoneId</c> and refreshes the current zone-state display.</summary>
    public const string EnterAnotherZonegroup = "ENTER_ANOTHER_ZONEGROUP";

    /// <summary>No arguments are read. Opens and refreshes the screenshot-camera-mode window.</summary>
    public const string EnteredScreenShotCameraMode = "ENTERED_SCREEN_SHOT_CAMERA_MODE";

    /// <summary>No arguments are read. Closes the screenshot-camera-mode window.</summary>
    public const string LeftScreenShotCameraMode = "LEFT_SCREEN_SHOT_CAMERA_MODE";

    /// <summary>No arguments are read. Refreshes the screenshot-camera-mode hotkey labels.</summary>
    public const string UpdateBindings = "UPDATE_BINDINGS";

    // System mini scoreboard.

    /// <summary>
    /// The scoreboard receiver reads <c>status</c> and <c>info</c>. For <c>update</c>, <c>info</c> is iterated as
    /// section records; for <c>remove</c>, it is iterated as section identifiers; <c>inactive</c> ignores it.
    /// The UIParent handler also receives this event but ignores its arguments and reloads current scoreboard info.
    /// </summary>
    public const string MiniScoreboardChanged = "MINI_SCOREBOARD_CHANGED";

    // GM console.

    /// <summary>Reads <c>logType</c> and <c>log</c>, colorizes the message, and appends it to the GM console.</summary>
    public const string WriteGmConsole = "WRITE_GM_CONSOLE";

    /// <summary>No arguments are read. Toggles the GM console.</summary>
    public const string ToggleGmConsole = "TOGGLE_GM_CONSOLE";

    /// <summary>Reads the first payload value as the console text; later payload values are ignored.</summary>
    public const string ConsoleWrite = "CONSOLE_WRITE";

    /// <summary>
    /// Reads <c>command</c>. For <c>add</c>, reads <c>arg1</c> fields <c>slotIdx</c>, <c>itemType</c>,
    /// <c>itemGrade</c>, <c>stackSize</c>, and <c>itemId</c>. For <c>type</c>, reads <c>arg1</c> and
    /// <c>arg2</c> as title values. For <c>clear</c>, no additional arguments are read.
    /// </summary>
    public const string GmSearchedBagInfo = "GM_SEARCHED_BAG_INFO";

    /// <summary>Reads <c>command</c> as the character-search text; later payload values are ignored.</summary>
    public const string GmSearchCharacter = "GM_SEARCH_CHARACTER";
}
