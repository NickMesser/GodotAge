# Original client settings reference (10.0.2.13)

Sources were extracted read-only from the client's `game_pak`. Lua option IDs below are the identifiers passed to `X2Option:GetOptionItemValue` / `SetItem*Value`; the binding declarations are in `X2Game.Net/.../Generated/LuaBindings.g.cs` (`X2Option.GetOptionInfo`, `GetOptionItemValue`, `GetOptionItemValueByName`, `GetHotkeyInfo`, `Save`). The native engine supplies values for many `OIT_*` IDs; the Lua defaults are not present in packaged scripts. “native” therefore means that a reliable literal default is not recoverable from these source files.

## Graphics

| Window/control | Original option ID / values | Default evidence |
|---|---|---|
| Basic display | `OIT_R_DESIREWIDTH`, `OIT_R_DESIREHEIGHT`; choices are `X2Option:GetResolution(1..GetResolutionCount())` | Dynamic monitor list; Lua does not hard-code a default. |
| Mode | `OIT_R_FULLSCREEN`; radio values supplied by option metadata | Native. Godot mapping assumes 0 windowed, 1 fullscreen, 2 exclusive fullscreen. |
| VSync | `OIT_R_VSYNC` (0/1) | Native. |
| Render thread | `OIT_NEXT_R_MULTITHREADED` (0/1) | Native; control disabled if unsupported. |
| Driver | `OIT_NEXT_R_DRIVER` (`DX9`/`DX10`) | Native; UI disables it without DX11 support. |
| Pixel sync | `OIT_R_PIXELSYNC` (0 off, 6 on) | Native; shown only if supported. |
| Gamma | `OIT_R_GAMMA` (0.5–1.5; UI 0–100) | Native. |
| FPS cap | `OIT_SYS_MAX_FPS` (30–150) and `OIT_SYS_USE_LIMIT_FPS` (0/1) | Slider UI initializes at 120, clamps to 30–150; limit checkbox native. |
| UI scale | `OIT_UI_SCALE`; available range/unit comes from `UIParent:GetUIScaleRange()` | UI fallback scale is 0.85 below 1280×864, otherwise 1.0; current scale is selected on open. |
| Camera (in world) | `OIT_OPTION_CAMERA_FOV_SET`; custom `OIT_CUSTOM_FOV` 30–100, `OIT_CUSTOM_CAMERA_MAX_DIST` 10–100, `OIT_CUSTOM_ZOOM_SENSITIVITY` 0.5–5 | Native. |
| Advanced master | `OIT_MASTERGRAHICQUALITY` (1–5; 5 = user-defined) | UI/default quality starts at 4; CVar profile default is 4. |
| Quality selectors (all 1–4) | `OIT_OPTION_TEXTURE_BG`, `_TEXTURE_CHARACTER`, `_VIEW_DISTANCE`, `_TERRAIN_DETAIL`, `_TERRAIN_LOD`, `_VIEW_DIST_RATIO`, `_VIEW_DIST_RATIO_VEGETATION`, `_CHARACTER_LOD`, `_ANIMATION`, `_SHADOW_DIST`, `_SHADOW_VIEW_DIST_RATIO_CHARACTER`, `_SHADOW_VIEW_DIST_RATIO`, `_SHADER_QUALITY`, `_VOLUMETRIC_EFFECT`, `_EFFECT`, `_WATER` | CVar profile default is 4 for each. `VIEW_DISTANCE` levels map to 600/1000/1500/2000 m. |
| Advanced toggles | `OIT_OPTION_USE_SHADOW`, `_USE_CLOUD`, `OIT_E_ZONEWEATHEREFFECT`, `OIT_OPTION_WEAPON_EFFECT`, `_USE_WATER_REFLECTION`, `_USE_HDR`, `_USE_DOF` | CVar-group defaults: shadow/cloud/reflection/HDR/DoF on; weapon effects on (`option_weapon_effect.cfg` default 1; [0] disables). Weather option literal default not recovered. |
| AA | `OIT_OPTION_ANTI_ALIASING`, values 1–13 | `option_anti_aliasing.cfg`: default 1; 2/3 FXAA, 4 PostAA, 5–11 MSAA presets, 12/13 TXAA. |

Other graphics CVar-group IDs: animation, character LOD, effect, shader quality, shadow distance and shadow view ratios, terrain detail/LOD, background/character texture, cloud, DoF, HDR, shadow, water reflection, view ratios/distance, volumetric effect, water, weapon effect. Files in `game/config64/cvargroups/option_*.cfg` enumerate each actual CVar and presets. SSAO has no standalone checkbox in these windows: shader profile default sets `r_SSAO=0`. Fog follows the `VIEW_DISTANCE` CVar group. Render scale is not an original setting.

## Sound

`sound_option.lua` shows quality (`OIT_NEXT_OPTION_SOUND`, 1–4; `option_sound.cfg` default 4), master `OIT_S_GAMEMASTERVOLUME`, music `OIT_S_MUSICVOLUME`, effects `OIT_S_SFXVOLUME`, cinematic `OIT_S_CINEMAVOLUME`, user music `OIT_S_MIDIVOLUME`, and vehicle music `OIT_S_VEHCLEMUSICVOLUME`. Each volume slider displays 0–10 and stores 0.0–1.0 in 0.1 increments. Lua's temporary initial control value is 10/10; `Init()` replaces it with the native option value (or 0 if absent), so that is not proof of the saved default. Checkboxes: `OIT_SOUND_MOOD_COMBAT_ENABLE`, `OIT_USER_MUSIC_DISABLE_SELF`, and `OIT_USER_MUSIC_DISABLE_OTHERS`. Distinct ambient/voice/UI sliders and master mute are not original controls.

## Game/interface

Options from `interface_option.lua`, `interface_actionbar_option.lua`:

- Name tags: `OIT_NAME_TAG_MODE` (profile default 0, modes 0–3), `OIT_NAME_TAG_APPELLATION_SHOW`, `_FACTION_SHOW`, `_HP_SHOW`, `_SELF_ENABLE`, `_NPC_SHOW`, `_PARTY_SHOW`, `_EXPEDITION_SHOW`, `_FRIENDLY_SHOW`, `_HOSTILE_SHOW`, `_MY_MATE_SHOW`, `_FRIENDLY_MATE_SHOW`, `_HOSTILE_MATE_SHOW`, `_FACTION_SELECTION`, `OIT_OPTION_CHARACTER_PRIVACY_STATUS`.
- Game info: `OIT_SHOWHEATLTHNUMBER`, `OIT_SHOWMAGICPOINTNUMBER`, `OIT_SHOWBUFFDURATION`, `OIT_SHOWTARGETCASTINGBAR`, `OIT_SHOWTARGETTOTARGETCASTINGBAR`, `OIT_VISIBLEMYEQUIPINFO`, `OIT_SKILL_SYNERGY_INFO_SHOW_TOOLTIP`, `OIT_SKILL_DETAIL_DAMAGE_SHOW_TOOLTIP`, `OIT_ITEM_MAKER_INFO_SHOW_TOOLTIP`, `OIT_FIXEDTOOLTIPPOSITION`, `OIT_SHOWEMPTYBAGSLOTCOUNTER`, `OIT_SHOWCHATBUBBLE`, `OIT_G_HIDE_TUTORIAL`, `OIT_SHOWFPS`, `OIT_OPTION_SKILL_ALERT_ENABLE`, `_SKILL_ALERT_POSITION`, `OIT_COMBAT_MSG_LEVEL` (1–5), `_COMBAT_MSG_DISPLAY_SHIP_COLLISION`, `_COMBAT_MSG_VISIBILITY` (0–100), `OIT_GIVEN_QUEST_DISTANCE_DISPLAY_MODE`, `OIT_OPTION_MAP_GIVEN_QUEST_DISTANCE` (1–4).
- Game functions: `OIT_CLICK_TO_MOVE`, `OIT_FIRE_ACTION_ON_BUTTON_DOWN`, `OIT_AUTO_ENEMY_TARGETING` (checkbox writes value 2 when enabled), `OIT_SMART_GROUND_TARGETING`, `OIT_USE_CELERITY_WITH_DOUBLE_FORWARD`, `OIT_GLIDER_START_WITH_DOUBLE_JUMP`, `OIT_DOODAD_SMART_POSITIONING`, `OIT_DECORATION_SMART_POSITIONING`, `OIT_OPTION_ITEM_MOUNT_ONLY_MY_PET`, `OIT_CAMERA_USE_SHAKE`, `OIT_SHOWPLAYERFRAMELIFEALERTEFFECT`, `OIT_USEQUESTDIRECTINGCLOSEUPCAMERA`, `OIT_SHOW_GUIDEDECAL`, `OIT_G_SHOW_LOOT_WINDOW`, `OIT_AUTO_USE_ONLY_MY_PORTAL`, `OIT_OPTION_SHOW_COMBAT_RESOURCE_WINDOW`, `OIT_USE_AUTO_REGIST_DISTRICT`, `OIT_E_CUSTOM_CLONE_MODE`, `OIT_E_CUSTOM_MAX_CLONE_MODEL`, `OIT_E_CUSTOM_MAX_MODEL` (model slider labels define the range; default native), `OIT_G_IGNORE_PARTY_INVITE`, `_RAID_INVITE`, `_RAID_JOINT`, `_SQUAD_INVITE`, `_EXPEDITION_INVITE`, `_FAMILY_INVITE`, `_JURY_INVITE`, `_TRADE_INVITE`, `_DUEL_INVITE`, `_WHISPER_INVITE`, `_IGNORE_CHAT_FILTER`, `_USE_CHAT_TIME_STAMP`, `OIT_SHOW_RAID_COMMAND_MESSAGE`, `OIT_CR_SENSITIVITY` (native min/max), `OIT_CR_INVERT_X_AXIS`, `_INVERT_Y_AXIS`, `OIT_CURSOR_SIZE`, `OIT_BASIC_CURSOR_SHAPE`.
- Action bar: `OIT_SHOWACTIONBAR_1` through `_6`, `OIT_SLOT_COOLDOWN_VISIBLE`. Explicit named options also registered in Lua: `OptionBackCarryingOrder1st..4th` defaults 2/3/4/1; `ShowPlayerHelmet`, `ShowIpnir`, `ShowMyBackHoldable`, `ShowMyCosplay` default 1; `ShowMyBackPackWithCosplay`, `ChangeMyCosplayVisual` default 0.

For the OIT interface items above, Lua declares checkbox/radio types and the few explicit ranges shown, but not their default values; `X2Option`/native profile data is required. Controls omitted from this delivery's default catalog should not be treated as recovered defaults.

## Chat

Four per-tab pages (`normal_page_view.lua`, `alarm_page_view.lua`, `combat_page_view.lua`, `etc_page_view.lua`) store through `X2Chat`, not OIT cvars. Normal page filters/colors include `CMF_SAY`, `WHISPER`, `PARTY`, `RAID`, `EXPEDITION`, `FACTION`, `LOCALE_SERVER`, `ALL_SERVER`, `ZONE`, `TRADE`, `FIND_PARTY`, `SQUAD`, `TRIAL`, `FAMILY`, `RACE`, `OTHER_CONTINENT`, and `CMF_KO` (feature-gated). Alarm groups include notice/system, quest/channel, connection friend/family/expedition, item/loot, money/honor/labor, party/raid, trade/store, emotion/skill; combat includes source/destination, melee damage/miss, spell damage/miss/heal/energize, aura, environmental damage, death. “Etc” has font 12–17 pt, background color, leave alpha 0–100 (default 50), and hover alpha 0–100 (default 70). Filter defaults come from `X2Chat:GetDefaultChatTabInfoTable(tabId)`.

## Keybindings

Modern key UI (`key_binding_option_new.lua`) exposes these action IDs: `HA_MOVEFORWARD`, `HA_MOVEBACK`, `HA_MOVELEFT`, `HA_MOVERIGHT`, `HA_TURNLEFT`, `HA_TURNRIGHT`, `HA_JUMP`, `HA_AUTORUN`, `HA_DOWN`, `HA_TOGGLE_WALK`, `HA_ACTIVATE_WEAPON`, `HA_SWAP_PRELIMINARY_EQUIPMENT`, `HA_FRONT_CAMERA`, `HA_RIGHT_CAMERA`, `HA_LEFT_CAMERA`, `HA_BACK_CAMERA`, `HA_CYCLE_CAMERA_COUNTER_CLOCKWISE`, `HA_CYCLE_CAMERA_CLOCKWISE`, `HA_ZOOM_IN`, `HA_ZOOM_OUT`. Seven keybinding submenus in `option.lua` cover character, mount, game, camera, interface, shortcuts, and additional actions. `X2Hotkey` supports primary and secondary binding slots.

The only packaged default bindings are the active entries in `game/libs/config/defaultprofile.xml`; sibling `profiles/default/actionmaps.xml` is empty. The active XML map rows are:

| XML action(s) | Default key(s) |
|---|---|
| `moveforward`, `moveback`, `moveleft`, `moveright`, `turnleft`, `turnright` | W, S, Q, E, A, D |
| `jump`, `autorun`, `toggle_walk`, `special`, `special1..6`, `reload`, `drop` | Space, NumLock, NumpadPeriod, T, 5/6/7/8/9/0, R, J |
| `change_weapon`, `change_weapon_anim`; `open_chat`, `open_config` | Z, Alt+Z; Enter, Escape |
| `toggle_bag`, `toggle_character`, `toggle_spellbook`, `toggle_raidteam` | B, C, P, G |
| `cycle_hostile_forward/backward`; `cycle_friendly_forward/backward` | Tab / Shift+Tab; Ctrl+Tab / Ctrl+Shift+Tab |
| `zoom_in`, `zoom_out`, `horn`, `lights` | Mouse wheel up/down, NumpadAdd, L |
| `use`, `zoom`, `firemode`, `skip_cutscene`; spectator previous/next | F, NumpadSubtract, NumpadDivide, Ctrl+Space; Alt+Left/Right |
| `godmode`, `ulammo`, `flymode`, `debug`, `debug_ag_step`, `tweak_enable/inc/dec`, `tweak_up/down/left/right` | Ctrl+F1, Alt+F1, Shift+F1, Ctrl+F2, Alt+F2, Ctrl+F3, Alt+F3, Shift+F3, Ctrl+arrows |
| multiplayer radio groups 0..3; singleplayer `save`, `loadLastSave`, `load` | Ctrl+F4..F7; Alt+F4..F6 |

The modern UI exposes `HA_*` names above, but packaged data does not formally map those IDs to legacy action names. HA-to-key correspondence in `KeyBindings.Defaults` is therefore an explicit bridge assumption. Commented XML bindings are excluded.
