# AAEmu Godot audio layer

`Fsb5Decoder` parses FSB5 banks, reconstructs Ogg Vorbis, rebuilds codec-11 MPEG streams, and
converts supported PCM codecs to PCM16. `SoundLibrary`, `SoundPlayer`, and `AudioDirector` adapt
those streams to Godot. The built-in Vorbis registry contains all **28** setup packets recovered
from the FMOD Ex descriptor table; registration verifies each packet's CRC-32.

Typical scene setup after opening the client pak:

```csharp
var director = new AudioDirector();
director.Configure(ClientPaths.Database, PakFiles.Read);
AddChild(director); // Configure before AddChild

director.OnZoneChanged(zoneKey: 258, subZoneId: 688); // DB zone id 179
director.PlayUi("submenu_show");
director.PlayEvent("sounds/x2interface:interface:submenu_show");
director.PlaySkillFx(1415, effectNode);
director.PlayParticleFx("particles.lightning_hit", effectNode);
director.StopMusic();
```

`AudioContentDatabase` opens SQLite in read-only mode and imports `sounds`, `sound_packs`,
`sound_pack_items`, `zone_groups`, `sub_zones`, `fx_items`, and `fx_sounds`. It reads the matching
FEV project through the pak callback, indexes declared FSBs by sample name, and only returns exact
bank/sample/index matches. The particle reader also resolves explicit `Sound` attributes from
`game/libs/particles/x2_sounds.xml`.

`AudioTest.tscn` runs DB zone 179, UI names `crime_records_show`, `ruling_status_show`, and
`submenu_show`, and FxSound rows 1415, 1449, and 1533. Configure its `GamePakPath` and
`DatabasePath` exports if the defaults do not match the local client.

Zone 179 resolves to zone key 258, sub-zone 688, and sound pack 122. No zone ambience event is
identified by the DB mappings: its pack items use `music_*` names, and `soundmoods.xml` only defines
bus categories/filters. `OnZoneChanged` crossfades music and stops director-owned ambience when
there is no resolved ambience. Footstep event families can be played with `PlayEvent`; choosing a
surface-appropriate event remains movement-system integration.

The examined corpus has 3,958 MPEG banks / 6,870 samples, all observed as MPEG Layer II. The
adapter rebuilds complete frame streams and passes them to `AudioStreamMP3`. Some Godot builds
include Layer II only when `minimp3_extra_formats=yes`; run the supplied scene in the target engine
to confirm its decoder accepts these voice banks. Codec 12 CELT is not part of the Godot adapter
(2 samples in this corpus). Other unsupported FSB codecs fail with `NotSupportedException`.
