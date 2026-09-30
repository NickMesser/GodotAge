#nullable enable

using System.Numerics;
using AAEmu.GodotViewer.Net;

namespace AAEmu.GodotViewer.Client;

/// <summary>Housing requests whose generated native body and AAEmu reader have the same wire layout.</summary>
/// <remarks>
/// CSConstructHouseTax (0x08B), CSCreateHouse (0x08C), CSRequestHouseTax (0x090),
/// CSSellHouse (0x093), and CSBuyHouse (0x095) are intentionally unsupported because their generated
/// bodies conflict with AAEmu's readers. Requests absent from PacketBodies.g.cs are also omitted.
/// </remarks>
public sealed partial class ClientActions
{
    private const ushort CSDecorateHouse = 0x08D;
    private const ushort CSChangeHouseName = 0x08E;
    private const ushort CSChangeHousePermission = 0x08F;
    private const ushort CSPrepayHouseTax = 0x091;
    private const ushort CSShowCommonFarmArea = 0x15E;
    private const ushort CSPlaceCommonFarm = 0x164;

    /// <summary>Places one decoration using house-relative position and quaternion rotation.</summary>
    public void DecorateHouse(
        ushort houseTimelineId,
        uint designId,
        Vector3 relativePosition,
        Quaternion rotation,
        uint parentObjectId,
        ulong itemId)
    {
        if (parentObjectId > 0x00FF_FFFF)
            throw new ArgumentOutOfRangeException(nameof(parentObjectId), parentObjectId, "bc object ids are 24-bit");

        Send(CSDecorateHouse, new WireWriter()
            .U16(houseTimelineId)
            .U32(designId)
            .F32(relativePosition.X).F32(relativePosition.Y).F32(relativePosition.Z)
            .F32(rotation.X).F32(rotation.Y).F32(rotation.Z).F32(rotation.W)
            .Bc(parentObjectId)
            .U64(itemId)
            .ToArray());
    }

    /// <summary>Changes the name of the house identified by its timeline id.</summary>
    public void ChangeHouseName(ushort houseTimelineId, string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        Send(CSChangeHouseName, new WireWriter().U16(houseTimelineId).Str(name).ToArray());
    }

    /// <summary>Changes the house access permission byte.</summary>
    public void ChangeHousePermission(ushort houseTimelineId, byte permission) =>
        Send(CSChangeHousePermission, new WireWriter().U16(houseTimelineId).U8(permission).ToArray());

    /// <summary>Changes the prepaid-tax option for a house timeline id.</summary>
    public void PrepayHouseTax(short houseTimelineId, bool ausp) =>
        Send(CSPrepayHouseTax, new WireWriter().S16(houseTimelineId).Bool(ausp).ToArray());

    /// <summary>Requests display of the common-farm area for the native signed type selector.</summary>
    public void ShowCommonFarmArea(int type) =>
        Send(CSShowCommonFarmArea, new WireWriter().S32(type).ToArray());

    /// <summary>Requests placement of common-farm entries at an uncompressed f32 point.</summary>
    public void PlaceCommonFarm(int type, uint count, Vector3 point) =>
        Send(CSPlaceCommonFarm, new WireWriter()
            .S32(type).U32(count).F32(point.X).F32(point.Y).F32(point.Z).ToArray());
}
