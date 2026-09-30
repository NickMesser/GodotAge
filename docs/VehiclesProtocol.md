# Vehicles, ships, slaves, and mates

## Evidence and send policy

Wire layouts below were checked against `X2Game.Net/src/X2Game.Protocol/Generated/PacketBodies.g.cs`,
the AAEmu packet readers/writers under `AAEmu.Game/Core/Packets/{C2G,G2C}`, and the movement types
under `AAEmu.Game/Models/Game/Units/Movements`. The native movement summary is also in
`AAEmuResearch/PacketLayouts/README.md` (movement rows around lines 165–177) and
`PacketLayouts/ship-simulation.md`.

The recorded capture set (`packets-*.jsonl`) contains 2,242 outbound `CSMoveUnit` records,
all move type 1, each 41 bytes. It has no type 2 or 5 movement and no slave/mate action requests.
The 93–125 ms cadence in `w3_movement/REPORT.md` is therefore evidence for captured type 1 only.
The native research notes confirm that a driving client sends type 5 for ships, but do not recover
the keyboard/controller-to-byte mapping or its cadence. Accordingly the writer accepts raw signed
throttle and steering values from its caller. No new automatic key mapping, ship cadence, or vehicle
physics is guessed or sent. Type 2 also requires a complete simulated pose and wheel state; this
viewer currently has no matching vehicle physics simulation.

## `CSMoveUnit` body

`PacketBodies.g.cs` models the C2S envelope as `CSMoveUnitPacketBody(byte[] Field1, sbyte ExtraFlags)`.
`Field1` is the move-type payload; the trailing signed byte is `ExtraFlags`.

| Field | Width / condition | Meaning |
| --- | --- | --- |
| target object id | `bc` (3 bytes) | Unit whose movement is reported |
| move type | `u8` | 1 unit, 2 vehicle, 3 vehicle alias, 4 ship state, 5 ship request, 6 transfer |
| time | `u32` | Client physics time |
| flags | `u8` | `MoveTypeFlags`; when bit `0x10` is set, the next two fields exist |
| `scType`, `phase` | `u32`, `u8`, only for flag `0x10` | Optional state/phase pair |
| extra flags | trailing `s8` | Generated wrapper field following `Field1` |

Move-type-specific fields follow the common header:

* **Type 2 / 3, `VehicleMoveType`:** 11-byte packed position; velocity X/Y/Z as three `s16`;
  rotation X/Y/Z as three `s16`; angular velocity X/Y/Z as three `f32`; steering `f32` (AAEmu
  comments describe `-1..+1`); throttle `u8`; wheel velocity count `u8` (native maximum `0x12`);
  that many wheel angular velocities as `f32`.
* **Type 4, `ShipMoveType`:** 11-byte packed position; velocity and rotation as three `s16` each;
  angular velocity as three `f32`; steering `s8`; throttle `s8`; RPM `u8`; zone ID `u16`;
  stuck flag `bool`. This is the server-reported ship state.
* **Type 5, `ShipRequestMoveType`:** throttle `s8`, steering `s8`. This is input only; the server
  owns ship pose and physics.

The viewer exposes exact type 2 and type 5 body writers and a caller-supplied raw type 5 action.
It does not schedule or synthesize either move type. Received type 4 movement follows the existing
server movement path: the unit target is interpolated in `OnlineSession._Process` with
`1 - exp(-10 * delta)`.

## Confirmed C2S slave and mate requests

All multi-byte values are little-endian. `timelineId` is read as `u16` by the AAEmu C2G readers;
the viewer stores the same 16 wire bits in a `short` where existing model APIs use signed timeline
IDs.

| Opcode / packet | Body fields |
| --- | --- |
| `0x05B CSSpawnSlave` | `u32 slaveTemplateId`; fixed-point X/Y `s64`; Z and yaw `f32`; item ID `u64`; if item ID is nonzero, slot type and slot `u8` each; `bool hideSpawnEffect` |
| `0x05C CSDespawnSlave` | slave object id `bc` |
| `0x05E CSBindSlave` | slave timeline `u16`; occupy skill type `s32` (not an attach-point id) |
| `0x05F CSDiscardSlave` | slave timeline `u16` |
| `0x060 CSChangeSlaveTarget` | target object id `bc`; slave object id `bc` |
| `0x061 CSChangeSlaveName` | slave timeline `u16`; length-prefixed string |
| `0x062 CSRepairSlaveItems` | unit object id `bc` |
| `0x064 CSChangeSlaveEquipment` | character ID `u64`; slave timeline `u16`; DB slave ID `u32`; `bts bool`; count `u8` (maximum 3); entries; each entry is item 1, item 2, two `(slotType u8, slot u8)` pairs, expiry `s64` |
| `0x09D CSBoardingTransfer` | timeline `u16`; attach point `u8` |
| `0x0E5 CSRemoveMate` | mate timeline `u16` |
| `0x0E6 CSChangeMateTarget` | mate timeline `u16`; target object id `bc` |
| `0x0E7 CSChangeMateName` | mate timeline `u16`; length-prefixed string |
| `0x0E8 / 0x0E9 CSMountMate / CSUnMountMate` | mate timeline `u16`; attach point `u8`; reason `u8` |
| `0x0EA CSChangeMateEquipment` | character ID `u64`; mate timeline `u16`; passenger ID `u32`; `bts bool`; count `u8` (maximum 2); same equipment entry layout; expiry `s64` |
| `0x0EB CSChangeMateUserState` | mate timeline `u16`; user state `s8` |
| `0x0F6 CSRepairPetItems` | unit object id `bc` |

Each equipment `Item` begins with template ID `u32`; zero is the empty-item sentinel. Otherwise it
contains item ID `u64`, grade `u8`, flags `u8`, count `s32`, detail type `u8`, detail bytes selected
by detail type, create time `s64`, lifespan minutes `s32`, made-unit ID `u64`, world ID `u8`, and
three `s64` times (unsecure, unpack, charge-use-skill). Writers preserve the decoded detail bytes.

`CSBoardingTransfer` is only logged by the checked-in AAEmu reader; the actual bind-to-helm request
is `CSBindSlave`. No `CSBoardSlave` or `CSSummonMate` packet exists in the generated protocol or
AAEmu packet registry. Mate item use runs through the existing item-caster `CSStartSkill` route; the
AAEmu `SpawnPet` effect calls the owner’s mate manager. Slave items can also use `CSStartSkill` and
the `SpawnSlave` effect. The distinct `CSSpawnSlave` request remains available for the placement
path the AAEmu reader explicitly accepts.

## Server-to-client slave, mate, and seat state

| Opcode / packet | Body fields |
| --- | --- |
| `0x08E SCSlaveCreated` | owner object `bc`; timeline `s16`; slave object `bc`; unknown `u64`; length-prefixed creator name |
| `0x08F SCSlaveRemoved` | owner object `bc`; timeline `s16` |
| `0x090 SCSlaveDespawn` | slave object `bc`; success `bool` |
| `0x091 SCSlaveBound` | master ID `u64`; master world ID `s8`; slave object `bc` |
| `0x092 SCMySlave` | object `bc`; timeline `s16`; name; template ID `u32`; HP and max HP `u64`; fixed-point X/Y `s64`; Z `f32` |
| `0x093 SCEscapeSlave` | object `bc`; fixed-point X/Y `s64`; Z and rotation `f32` |
| `0x094 / 0x096 SCSlaveEquipmentExpired / FlagsChanged` | timeline `s16`; type `s8`; index `s8`; flags packet adds flags `s8` |
| `0x095 SCSlaveEquipmentChanged` | character `u64`; timeline `s16`; DB slave ID `u32`; `bts bool`; count `u8` (server currently sends one, client max 3); equipment entries; success `bool` |
| `0x16A SCMateSpawned` | timeline `s16`; mate type `s8`; ID `s32`; item ID `u64`; user state `s8`; experience `u32`; spawn delay `s32`; exactly ten mount-skill IDs `s32` |
| `0x16B SCMateEquipmentChanged` | character `u64`; timeline `s16`; passenger ID `u32`; `bts bool`; count `u8`; equipment entries; success `bool` |
| `0x16C / 0x16D SCMateEquipmentExpired / FlagsChanged` | timeline `s16`; type `s8`; index `s8`; flags packet adds flags `s8` |
| `0x21B SCSlaveState` | object `bc`; timeline `s16`; type `u64`; then skill, tag, and charge lists; each list is a `u32` count followed by that many triples of `s32`; creator string; owner `u64`; DB ID `s32` |
| `0x21C SCMateState` | object `bc`; same skill, tag, and charge counted lists |

The existing seat relation packets are `SCUnitAttached` (`child bc, point u8, parent bc unless point
is `0xFF`, reason u8`) and `SCUnitDetached` (`child bc, reason u8`). `AttachPointKind` values 1 and
2–8 identify driver and passenger seats; 9–82 include equipment points such as cannons, sails,
ladders, rudders, and bells. Static slave prefabs in the inspected rowboat, speedboat, galleon, and
merchant-ship members contain no stable `$driver` or `$passengerN` helper transform. The viewer
retains the parent relation so the player/camera follows a moving hull, but does not invent seat
offsets; only actor-mount bone attachments are used.

## Models, equipment, and pet UI state

The read-only content relationship is `slaves.model_id -> models.(sub_type, sub_id) ->
vehicle_models.<state>` or `ship_models.<state>`. Mates are ActorModels reached through the summon
item/NPC/model tables and continue through the viewer’s `BuildNpc` path. The new slave resolver
supports direct CGF models and prefab members whose parts are CGFs. It selects the confirmed
`normal` state, uses the existing static CGF renderer, and leaves a placeholder plus diagnostic for
unsupported assets. CGA animation playback, damaged-state transitions, and ship gear meshes are not
implemented. Gear item exchanges and expiry/flag packets are decoded and published, but they do not
yet add/remove sail, cannon, or pet gear meshes.

`OnlineSession.MateSlaveState` exposes mate/slave units and typed events. Mate/slave level, current HP,
and learned skills come from the same object ID’s `UnitSnapshot`/`CombatState`; the ten mount-skill
IDs, experience, user state, and spawn delay join to a mate only by the identical timeline ID carried
by both `SCMateSpawned` and the Mate identity union in `SCUnitState`. `SCMySlave.MaxHp` is used only
with its matching object ID. Timeline-only values remain in timeline-keyed collections. No
`SCMateUserStateChanged` packet is present; `CSChangeMateUserState` is the request packet.

### Equipment and attachment evidence

AAEmu resolves slave gear through `item_slave_equipments.item_id`, then checks the slave family in
`item_slave_equipment_slave_equipslot_packs`, allowed slave IDs in `allow_to_equip_slaves`, allowed
slot IDs in `allow_to_equip_slots`, and the actual `attach_point_id` in `slave_equip_slots`. The
visual is usually the bound doodad model (`doodad_almighties.model`); grade-specific visuals come
from `item_slave_equipment_grade_spawns`. For example, item `29723` resolves to doodad `8767` and
uses attach points 28/29 (front/rear lamps) on slave template 14. Item `30306` can resolve to
cannon slots on ship template 21, whose slot mappings use attach points 9 through 16. Built-in hull
doodads are separate rows in `slave_doodad_bindings`.

The authoritative transform is not a guessed prefab offset: AAEmu maps attach-point IDs through
`model_attach_point_strings.prefab` to `$` helper nodes inside the hull CGF, then combines the CGF
helper transform with its containing prefab brush transform. The viewer's CGF reader does not expose
helper nodes, so this delivery decodes equipment state but does not render installed sails, cannons,
or lamp doodads. A follow-up visual implementation needs the CGF helper reader and must use the
resolved attach point and orientation. CGA gear and animated hull state also need an animation-capable
model path.

## Runtime test plan (not run here)

1. From the viewer root, run `git -c core.autocrlf=false apply --check -p1 <path-to-deliver/patches/viewer.patch>`,
   then apply it with the same command minus `--check`. Copy the two delivered source files to the paths
   in `deliver/README.md` and build against the viewer's existing AAEmu.Game project reference.
2. Start the viewer against a dedicated test World/Zone and the configured `game_pak` plus decrypted
   database. Summon one mate and one slave with known template IDs; capture C2S and S2C traffic.
   Confirm mate use is `CSStartSkill`, slave use follows the item’s skill path or the observed
   `CSSpawnSlave` placement path, and dismissal uses `CSRemoveMate` or `CSDespawnSlave`/`CSDiscardSlave`
   as appropriate.
3. Confirm `SCMateSpawned` timeline, `SCUnitState` mate timeline, HP, level, skills, and mount-skill
   IDs appear together in `MateSlaveState`; confirm `SCMySlave` max HP joins only its object ID.
   Verify a mate uses its ActorModel and a rowboat/speedboat resolves the expected static hull.
4. Board a mate and each supported vessel. Verify `SCUnitAttached` moves the player parent and camera
   with the mount/hull, `SCUnitDetached` restores the player, and attach points above 8 are not treated
   as generic passenger bones. Record actual local seat transforms before implementing seat offsets.
5. Drive a ship with the original client and capture type 5 plus server type 4. Use the captured raw
   bytes and timing to validate the writer and only then derive input mapping/cadence. Capture type 2
   from a wheeled slave before validating its full pose/wheel writer. Compare position smoothness and
   packet cadence with the original client.
6. Equip/unequip ship and mate gear; verify the packet entry count, item snapshots, slot pairs,
   expiry, success, and subsequent equipment state. Capture model anchors for sails/cannons before
   adding gear visuals. Verify state/damage visuals separately for each confirmed database state.
