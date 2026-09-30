#nullable enable

using System.Numerics;

namespace AAEmu.GodotViewer.Net;

/// <summary>
/// Client-to-server housing request bodies. Layouts follow what the real 10.0.2.13 client sent in a captured
/// placement cycle (2026-09-30: Scarecrow Garden design 267 placed at 15344/14632 against the local AAEmu World)
/// where one exists, otherwise AAEmu's readers.
/// </summary>
public static class HousingRequests
{
    public const ushort CSConstructHouseTax = 0x08B;
    public const ushort CSCreateHouse = 0x08C;
    public const ushort CSRequestHouseTax = 0x090;
    public const ushort CSAllowHousingRecover = 0x092;
    public const ushort CSStartSkill = 0x086;

    /// <summary>
    /// CSConstructHouseTax: the construction quote for a design at a world point, sent by the builder's rotation
    /// click. Captured 24-byte body: u32 design, i64 X, i64 Y, f32 Z
    /// (0b010000 00000000000000bf03 000000000000009203... for 267 at 15344/14632/171.54).
    /// </summary>
    public static byte[] ConstructHouseTax(uint designId, Vector3 position) => new WireWriter()
        .U32(designId).S64(ToAxis(position.X)).S64(ToAxis(position.Y)).F32(position.Z)
        .ToArray();

    /// <summary>
    /// CSCreateHouse, sent by X2House:NextStepFromBuildCheck. Native serializer (x2game FUN_39c80640), 48 bytes:
    /// i32 designId, u64 x, u64 y, f32 z, f32 zRot (raw yaw), i64 item, u64 moneyAmount, u32 ht. The captured
    /// garden placement carried moneyAmount 0 and ht 0. (AAEmu reads money and ht as two i32 and the last byte as
    /// an auto-use-AA-point flag; the bytes are the same.)
    /// </summary>
    public static byte[] CreateHouse(uint designId, Vector3 position, float yaw, ulong itemId, ulong moneyAmount,
        uint ht) => new WireWriter()
        .U32(designId).S64(ToAxis(position.X)).S64(ToAxis(position.Y)).F32(position.Z)
        .F32(yaw).U64(itemId).U64(moneyAmount).U32(ht)
        .ToArray();

    /// <summary>
    /// CSRequestHouseTax (x2game serializer FUN_39c68ae0): i16 tl, bc doodad (the nameplate that opened the window,
    /// or 0 when it was opened from the house unit).
    /// </summary>
    public static byte[] RequestHouseTax(ushort timelineId, uint nameplateDoodadId = 0) =>
        new WireWriter().U16(timelineId).Bc(nameplateDoodadId).ToArray();

    /// <summary>
    /// X2House:Demolish (x2game FUN_39998130): CSStartSkill is_demolish on the house - u32 skill, caster {u8 0,
    /// bc player}, target {u8 0, bc house}, skill object {u8 0x11, bool package, u32 sealCount}, u8 inputDirection.
    /// </summary>
    public static byte[] Demolish(uint skillId, uint playerUnitId, uint houseUnitId, bool package, uint sealCount) =>
        new WireWriter()
            .U32(skillId).U8(0).Bc(playerUnitId).U8(0).Bc(houseUnitId)
            .U8(0x11).Bool(package).U32(sealCount).U8(0)
            .ToArray();

    /// <summary>CSAllowHousingRecover: u16 house timeline id; the request toggles the flag (AAEmu reader).</summary>
    public static byte[] AllowHousingRecover(ushort timelineId) => new WireWriter().U16(timelineId).ToArray();

    /// <summary>World X/Y as the housing packets carry them: metres * 4096 in the high 32 bits (ConvertLongX inverse).</summary>
    public static long ToAxis(float metres) => (long)(metres * 4096f) << 32;
}
