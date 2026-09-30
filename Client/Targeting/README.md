# Targeting integration

Create a `TargetingController`, set its `Registry` and `Camera`, and add it to the scene tree. The registry is intentionally independent of network models. A live unit wrapper only needs to implement `ITargetable`; `ITargetableNameplate` and `ITargetOfTargetSource` add health/title and target-of-target data.

The controller registers `target_next` (Tab), `target_prev` (Shift+Tab), `target_self` (F1), `target_party_1` through `target_party_4` (F2-F5), and `target_clear` (Esc) if absent. Existing InputMap mappings are preserved. Party/self delegates are optional.

Tab candidates are living hostiles in the recovered 50 m, 60 degree camera-forward fan and 20 m vertical limit. Candidates are nearest first, with stable ID tie breaking, and the cycle is rebuilt only when membership/order changes. Line of sight is not tested because no supporting client script/config evidence was found. Nearest-first ordering is an implementation inference: the recovered CVars define the fan but not native ranking.

World click picking checks every live registered unit and takes the closest ray hit. Explicit local AABBs are transformed with the unit; targets without one use an upright capsule. Clicking empty ground keeps the current target. This policy matches typical observed ArcheAge interaction but remains unproven by the recovered UI Lua because world picking is native.

The ring loads original pak textures (`target_enemy04_red/yellow/green.dds` and `target_pointer_ring.dds`) through `PakFiles`; a procedural ring is used when the pak has not been opened. Nameplates load `game/ui/font/roboto.ttf` (Tahoma fallback) through `PakFiles`, are pooled, distance-scaled and faded, and update no more than `NameplateUpdatesPerFrame` entries each frame.

The recovered `game/default_binding.g` maps Esc to `open_config`, not target clearing. `target_clear` on Esc is a deliberate task-required viewer policy divergence.
