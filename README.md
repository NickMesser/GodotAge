# GodotAge

GodotAge is a game client for **ArcheAge 10.x** (client 10.0.2.13, r575) written in C# on **Godot 4.7**. It reads the
original client's `game_pak` directly and talks to an [AAEmu](https://github.com/AAEmu/AAEmu) server. The goal is a
client that looks and behaves like the original, without changing gameplay: the server decides everything, and the client
only shows and requests.

The original user interface is not redrawn by hand. GodotAge runs the client's own UI scripts (the `x2ui` Lua in the
pak) on a Godot re-implementation of the original widget and scripting API, so windows, layouts, text and behaviour come
from the game itself.

> This repository contains no game files. You need your own copy of the ArcheAge 10.x client; GodotAge reads its
> `game_pak` read-only and never modifies or redistributes it.

## What works

- **World:** terrain, placed models and materials, vegetation, decals, roads, water and ocean, sky and time of day,
  zone lighting and weather, streamed around the camera or the player, with collision for walking.
- **Characters:** player and NPC models with equipment and costumes, skin tone, eyes and face decals, hair colours,
  face shapes, and animations (movement, combat, emotes).
- **Original UI:** login, server select, character select and creation (race page, skillset previews with their class
  animations, cutscenes and particles, full appearance editor), HUD, action bars, bag, character window with stats,
  skills, quests and NPC dialogs, map and minimap, chat and emotes, mail, auction house, crafting, achievements,
  rankings, options and key bindings, death and resurrection.
- **Online play against AAEmu:** login, world and zone entry, movement, NPCs, other players and doodads, tab targeting
  with nameplates, skill casts with their particle effects, combat text, looting, quests.
- **Sound:** footsteps and ambient sound.

Still missing or incomplete: housing placement, vehicles and ships, some screen-space effects (bloom), and a number of
smaller visual differences. Treat it as a work in progress.

## Requirements

- Windows 10/11 x64 (the bundled Lua runtime is a Windows DLL).
- [Godot 4.7.2, .NET edition](https://godotengine.org/download) (the "mono" build).
- [.NET 10 SDK](https://dotnet.microsoft.com/download).
- The **ArcheAge 10.x client** (10.0.2.13). Only its `game_pak` file is used: the single large archive (about 68 GB)
  in the client's install folder.
- For online play: an AAEmu server for the 10.0.2.13 client, e.g. the `client_version/zone-10.0.2_r575` branch of
  [AAEmu/AAEmu](https://github.com/AAEmu/AAEmu).

## Setup

1. Clone this repository.

2. Tell GodotAge where your client's `game_pak` is. Create a file named `godotage.cfg` in the repository folder:

   ```ini
   # Path to the game_pak file of your ArcheAge 10.x client (the client's install folder works too)
   pak=D:/Games/ArcheAge/game_pak

   # Recommended: the compact.sqlite3 of your AAEmu server (see "Game database and English text" below)
   db=D:/AAEmu/AAEmu.Game/Data/compact.sqlite3
   ```

3. Build once: open `project.godot` in the Godot .NET editor and press **Build**, or run
   `dotnet build AAEmu.GodotViewer.csproj`.

`godotage.cfg` is ignored by git, so your paths stay local. It is read from the working directory, the folder of the
executable or the project folder (when you start Godot with `--path <repo>`, put it in the repo folder).

### Game database and English text

GodotAge needs the game database for names, items, skills, appearance data and so on:

- **Without `db=`**, it uses the client's own database: on the first start it copies `game/db/compact.sqlite3` out of
  your pak (about 240 MB, a few seconds, once per client version) into
  `%APPDATA%\Godot\app_userdata\AAEmu World Viewer\gamedb\`. The 10.x client's database has **no English text**, so
  the interface shows Korean, and it lacks items, NPCs and skills the server adds.
- **With `db=`** pointing at the `compact.sqlite3` your AAEmu server uses for the 10.0.2.13 client, you get English
  text and the server's complete item, NPC and skill data. This is what you want for normal use.

### All settings

Each setting can go in `godotage.cfg`, in an environment variable `GODOTAGE_<NAME>` (for example `GODOTAGE_PAK`) or on
the command line as `--<name>=<value>`. The command line wins over the environment, which wins over the file.

| Setting | Default | Meaning |
|---|---|---|
| `pak` | `C:/AA/game_pak` | The client's `game_pak` file or install folder. |
| `db` | `<client folder>/game/db/compact.sqlite3` if it exists, otherwise extracted from the pak | Game database to use. |
| `host`, `port` | `127.0.0.1`, `1237` | AAEmu login server. `--login=host:port` works too. |
| `launcher` | `<client folder>/launch_aaemu.bat` | A client launcher `.bat` whose `-StrUserName`/`-strUserToken` arguments `--autologin` uses. |

## Running

From the Godot editor, **Play** starts the world viewer. From a terminal (the arguments after `--` go to GodotAge):

```bat
:: Offline world viewer: fly around a region of the main continent (F toggles fly / walk)
Godot_v4.7.2-stable_mono_win64.exe --path . -- --world=main_world --cell=15,14 --radius=1

:: Client: the original login screen, then server select, character select and the world
Godot_v4.7.2-stable_mono_win64.exe --path . -- --online

:: Client against another server address (default 127.0.0.1:1237)
Godot_v4.7.2-stable_mono_win64.exe --path . -- --online --host=192.168.1.10 --port=1237

:: Log straight into the world as a character (uses the launcher account, see launcher= above)
Godot_v4.7.2-stable_mono_win64.exe --path . -- --autologin=MyCharacter
```

At the login screen, enter your AAEmu account name. GodotAge signs in the way the 10.x client's launcher does (web
authentication with the account name and a launcher token); whatever you type as the password is sent as that token.
The world viewer's full list of options is in the comment at the top of `WorldViewer.cs`.

## Files that stay on your machine

Some things the client uses are taken from or generated out of the game files, so they are ignored by git and never
published. GodotAge works without them:

- `Ui/X2/Overrides/game/`: your own replacements for files of the pak. A `.png` here replaces the pak texture with the
  same path (used for English versions of textures with baked-in Chinese text; generate them from your pak with
  `--path . res://Ui/X2/Test/MakeOverrides.tscn`). Compiled UI scripts under `scriptsbin64/` replace the pak's scripts,
  for example to re-enable features the 10.x client's scripts switch off.
- `Ui/X2/Translations/*.json`: extra English translations for game text that has no English entry in the database.
  Without them that text is shown as it is in the database.
- `Assets/Cursors/`: the original mouse cursors. You normally don't need these: GodotAge extracts the cursors from
  `Bin64\archeage.exe` next to your `game_pak` on the first start. Without either, the system cursor is used.

## Project layout

| Folder / file | Contents |
|---|---|
| `WorldViewer.cs`, `WorldStreamer.cs` | Main scene: world streaming, camera, offline viewer and online entry. |
| `PakFiles.cs`, `FastPak.cs` | Reader for the client's `game_pak`. |
| `*Reader.cs`, `ModelLibrary*.cs`, `Terrain*.cs`, `WaterRendering.cs`, `DynamicSky.cs` | CryEngine file formats and rendering (models, materials, terrain, water, sky). |
| `Character*.cs`, `Characters/` | Character assembly, materials (skin, eyes, hair, equipment) and animation. |
| `Effects/` | Particle effects. |
| `Net/` | Network protocol to the AAEmu login, world and zone servers. |
| `Client/` | Online session: units, movement, combat, targeting, housing, overheads. |
| `Ui/Lua/`, `Ui/X2/` | Lua host and the re-implemented X2 UI that runs the client's own UI scripts. |
| `Audio/`, `Maps/`, `Settings/`, `Data/` | Sound, world map data, options and key bindings, game database access. |

## Legal

GodotAge is an unofficial fan project and is not affiliated with or endorsed by XLGames, Kakao Games or any ArcheAge
publisher. ArcheAge and its assets belong to their respective owners. This repository only contains original code; it
does not include or distribute any game files.

GodotAge is licensed under the [GNU General Public License v3.0](LICENSE). Third-party components are listed in
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
