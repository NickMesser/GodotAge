# Textures with baked-in non-English text

Scope: every texture the original login, server-select and character-select screens load (dumped with
`UiTest.tscn -- dumptex=<dir>`): `ui/login_stage/{000_login,00_server,04_character,btn,circle_btn,login_bg}.dds`,
`ui/login_stage/en_us/login_text.dds`, `ui/common/default.dds`, `ui/common_new/default.dds`, `ui/common/tab_list.dds`.

Overrides live in `Ui/X2/Overrides/game/<pak path with .png>`; `X2TextureCache` loads them instead of the pak texture.
Regenerate them with `godot --path <project> res://Ui/X2/Test/MakeOverrides.tscn -- out=<Ui/X2/Overrides>`.

| Pak path | Baked text | Status |
|---|---|---|
| `game/ui/login_stage/000_login.dds` (region `logo`, login screen) | 上古世纪 归来 ("ArcheAge Returns", the Chinese service title) under the "A" emblem | Replaced: `Overrides/game/ui/login_stage/000_login.png`. The Chinese band is cleared and "ARCHEAGE" is rendered with the pak's LibreBaskerville Bold by `MakeOverrides.cs` (no image generation tool was used). |
| `game/ui/login_stage/login_text.dds` | Korean titles | Not used: the loader picks the locale copy `ui/login_stage/en_us/login_text.dds` (English). |
| `game/ui/login_stage/zh_cn/*.dds`, `zh_tw/*.dds` | Chinese | Not used with the en_us locale. |

No other texture in these stages carries text: button, window, list and background art only.

Still to check when more stages are ported (character creation, HUD): `ui/login_stage/01_race.dds`,
`02_ability.dds`, `03_customizing.dds` and anything under `ui/login_stage/background/` (`game_grade.dds` and
`game_information.dds` are rating/health notices shown only when those stages are enabled).
