# Vehicles, ships, slaves, and mates

## Evidence and send policy

Wire layouts below were checked against `X2Game.Net/src/X2Game.Protocol/Generated/PacketBodies.g.cs`,
the AAEmu packet readers/writers under `AAEmu.Game/Core/Packets/{C2G,G2C}`, and the movement types
under `AAEmu.Game/Models/Game/Units/Movements`.

On 2026-09-29/30 the real 10.0.2.13 client was driven against the local AAEmu stack (farm wagon, slave 60,
scroll 18660; rowboat, slave 15, item 1199) and its packets were captured with the harness packet monitor.
Everything the viewer now sends for vehicles reproduces those captures; one captured type 2 body was rebuilt
byte-for-byte by `VehiclePacketWriters.MoveVehicle` from its decoded values.

## The vehicle life cycle as the real client does it

| Step | Real-client request (captured) | Server answer |
| --- | --- | --- |
| Use the summon scroll | `CSStartSkill` skill = the item's `use_skill_id` (15802, target type 12 summon_pos): caster type 2 (item: player bc, item id u64, template u32, 0 u8, 0 u64), target type 1 (fixed-point X/Y s64, Z f32, rotation f32, three zero bc), then two zero bytes. The point is `slaves.spawn_x_offset` ahead of the character (5.0 m for the wagon at the terrain height; 2.5 m for the rowboat at the water surface, offset 1.5) with rotation = heading + π/2 | `SCSkillStarted/Fired`, then `SCSlaveCreated`, `SCUnitState`, `SCSlaveState` (0x21B), `SCDoodadCreated` (0x14E, one per bound doodad, parent-local position), `SCMySlave`, `SCUpdatedSlaveSourceItem` (0x296) |
| Use the same scroll while its slave is out | `CSDespawnSlave` (0x05C) with the slave bc - no skill cast | `SCSlaveDespawn`, `SCSlaveRemoved`, `SCDoodadRemoved` (0x14F) per doodad, `SCUnitsRemoved` |
| Board a slave that has an interaction skill (wagon) | interaction key with the slave targeted, within the skill's max_range (nothing at 4.9 m, bound at 2.4 m): `CSBindSlave` (0x05E) tl u16 + skill s32 = 75, 12076 | `SCUnhung`, `SCUnitAttached` (point 1, reason 6), `SCSlaveBound` |
| Board a ship (rowboat) | use of its helm doodad (template 2384, func `DoodadFuncAttachment`, func skill 14916): `CSStartSkill` 14916 on the doodad (AAEmu binds the driver seat). The real client's pick on the helm could not be reproduced by the harness; GodotAge's request was accepted live | as above |
| Drive a land vehicle | `CSMoveUnit` type 2 for the slave, every 98-130 ms including idle, never type 1 for the player while seated | World relays to the zone, echoes `SCOneUnitMovement` |
| Mode-bar skill (horn) | `CSStartSkill` 15622 with caster type 3 (mount: slave bc + `mount_skills.id` u32 = 65) and the slave as unit target (18-byte body) | World: caster 902, target 902 |
| Dismount | the mode bar's dismount function: `CSStartSkill` 35837 (`내리기`, target type parent) from the player (unit caster) on the slave: `fd8b0000 00 830300 00 860300 00 00` | `SCUnitDetached` (reason 5), `SCUnhung`, skill fired/ended |
| After any `SCTeleportUnit` | `CSTeleportEnded` (0x0F5): X/Y fixed-point s64, Z f32, quaternion (0, 0, sin(yaw/2), cos(yaw/2)) | World drops every `CSMoveUnit` silently until this arrives |

### Type 2 as the farm wagon produces it

`throttle` stays 0 and the wheel count 0 (the wagon is not a wheeled-simulation vehicle). Velocity shorts use a
30 m/s full scale (4369 = 4.0 m/s). The rotation shorts are the x, y, z of the full orientation quaternion (terrain
pitch and roll included, w implied). Cruise speed is 4.0 m/s forward and in reverse; acceleration and braking are
2.5 m/s² (= 1 / `lin_inertia`, 1 / `lin_deaccel_inertia`). `steering` follows A/D (+1 A, -1 D) over
`rot_inertia` seconds, the angular velocity z is steering × `angVel` (±0.40 rad/s), mirrored while backing up;
the wagon also turns at a standstill. The trailing extra-flags byte is 1 on the first packet after the throttle
key is released, otherwise 0.

### Ships

Ships are simulated by the zone; the driver sends only `CSMoveUnit` type 5 (throttle s8, steering s8). The real
client's type 5 stream could not be captured (see above); GodotAge sends throttle ±127 (W/S) and steering +127 for
D / -127 for A (AAEmu `BoatWaterlineDriveRules`: "+127 is starboard") on every change and at the land-vehicle
cadence. The zone streams the hull back as type 4 in `SCUnitMovements`; live, W moved the rowboat 7.8 m north in
4 s and W+D turned it to -56°.

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

`OnlineSession.Vehicles.cs` drives both: land vehicles are simulated client-side and streamed as type 2
(see the captured behaviour above); at a ship's helm only type 5 requests are sent. Received vehicle/ship
movement keeps the full orientation quaternion and is slerped in `OnlineSession._Process`; the driver
ignores the server's echo of its own land vehicle.

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
ladders, rudders, and bells. Seats come from the hull itself: CGF hulls carry `$driver` / `$passengerN`
helper nodes (the rowboat's `smallboat_body.cgf` has `$driver` and `$passenger0`), and skinned AnimObject bodies
carry them as bones (the farm wagon's `transfers_trailer_a_body.chr` has a `$driver` bone). The rider is parented to
that node, oriented with the hull.

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

## Live verification (2026-09-30)

Both life cycles were run live in GodotAge (scenario steps `useitem`, `targetslave`, `key F`, `usedoodad 2384`,
`hold W+D`, `modeaction`, `vehicle`, `burst`) with the World's `/slave info` confirming the server-side pose after
each leg: the wagon's World pose followed the client stream (W 3 s: y +9.8 m; W+D: yaw -58°; S 2 s: back 2.8 m; A at
a standstill: yaw -58° to -26°), and the rowboat's zone-simulated hull answered the type 5 requests (W 4 s: 7.8 m
north; W+D 3 s: yaw -57°). The horn (mode slot 3), slave info (SHOW_SLAVE_INFO), dismount and despawn were all
accepted by the server.
