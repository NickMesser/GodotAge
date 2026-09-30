#nullable enable

using System.Numerics;

namespace AAEmu.GodotViewer.Net;

/// <summary>A fixed world position used by the housing serializers.</summary>
public sealed record HousingWorldPosition(long RawX, long RawY, float Z)
{
    /// <summary>Position in world metres, decoded with AAEmu's ConvertLongX/Y representation.</summary>
    public Vector3 Value => new(DecodeAxis(RawX), DecodeAxis(RawY), Z);

    private static float DecodeAxis(long value) => (value >> 32) / 4096f;
}

/// <summary>One of the five fixed UCC descriptors in an SCHouseState body.</summary>
public sealed record HouseUccSlot(int HouseId, ulong DataId, uint Kind, uint Position);

/// <summary>Complete native SCHouseState state for one spatial house unit.</summary>
public sealed record HouseStateEvent(
    ushort TimelineId,
    uint DatabaseId,
    uint ObjectId,
    uint TemplateId,
    uint AllSteps,
    uint CurrentStep,
    ulong PayMoneyAmount,
    uint ModelId,
    ulong OriginalOwnerId,
    ulong OwnerId,
    string OwnerName,
    long AccountId,
    byte Permission,
    HousingWorldPosition Position,
    string HouseName,
    bool AllowRecover,
    ulong SalePrice,
    string SellToName,
    uint ExpandedDecorationLimit,
    int UnknownField80,
    bool IsPublic,
    bool IsBoundButler,
    int UnknownField82,
    IReadOnlyList<HouseUccSlot> UccSlots,
    IReadOnlyList<HousingWorldPosition> TrailingPositions) : GameEvent;

public sealed record HouseBuildProgressEvent(
    ushort TimelineId, uint ModelId, int AllSteps, int CurrentStep) : GameEvent;

public sealed record HousePermissionChangedEvent(ushort TimelineId, byte Permission) : GameEvent;
public sealed record HouseDemolishedEvent(ushort TimelineId) : GameEvent;

/// <summary>One entry in the client's owned/access-list SCHouseData state.</summary>
public sealed record OwnedHouseRecord(
    ushort TimelineId,
    ulong OwnerId,
    uint ObjectId,
    long AccountId,
    string OwnerName,
    HousingWorldPosition Position,
    int TemplateId,
    byte Permission,
    string HouseName);

public sealed record HouseDataEvent(IReadOnlyList<OwnedHouseRecord> Houses) : GameEvent;
public sealed record HouseRemovedEvent(ushort TimelineId) : GameEvent;
public sealed record HouseFarmSummaryEvent(string Name, int Total, int Harvestable) : GameEvent;

public sealed record HouseTaxInfoEvent(
    ushort TimelineId,
    uint DominionTaxRate,
    uint HostileTaxRate,
    ulong MoneyAmount,
    ulong SecondaryMoneyAmount,
    long DueUnixTime,
    bool IsAlreadyPaid,
    sbyte WeeksWithoutPay,
    byte WeeksPrepay,
    bool IsHeavyTaxHouse,
    byte TaxType) : GameEvent;

/// <summary>Strict inbound housing decoders for the 10.0.2.13 game protocol.</summary>
public static class HousingProtocol
{
    public const ushort SCHouseState = 0x0F0;
    public const ushort SCHouseBuildProgress = 0x0F1;
    public const ushort SCHousePermissionChanged = 0x0F2;
    public const ushort SCHouseDemolished = 0x0F3;
    public const ushort SCHouseData = 0x0F4;
    public const ushort SCHouseRemoved = 0x0F5;
    public const ushort SCHouseFarm = 0x0F6;
    public const ushort SCHouseTaxInfo = 0x0F7;

    /// <summary>Parses one supported server housing packet.</summary>
    public static GameEvent Parse(ushort opcode, byte[] body) => opcode switch
    {
        SCHouseState => ParseHouseState(body),
        SCHouseBuildProgress => ParseBuildProgress(body),
        SCHousePermissionChanged => ParsePermissionChanged(body),
        SCHouseDemolished => ParseDemolished(body),
        SCHouseData => ParseHouseData(body),
        SCHouseRemoved => ParseRemoved(body),
        SCHouseFarm => ParseFarmSummary(body),
        SCHouseTaxInfo => ParseTaxInfo(body),
        _ => throw new ArgumentOutOfRangeException(nameof(opcode), opcode, "not a supported housing opcode"),
    };

    /// <summary>
    /// SCHouseState (0x0F0), including the fixed five UCC slots and two trailing world positions.
    /// </summary>
    public static HouseStateEvent ParseHouseState(byte[] body)
    {
        var r = new WireReader(body);
        var timelineId = r.U16();
        var databaseId = r.U32();
        var objectId = r.Bc();
        var packed = r.Pisc(3);
        var payMoneyAmount = r.U64();
        var modelId = r.U32();
        var originalOwnerId = r.U64();
        var ownerId = r.U64();
        var ownerName = r.Str();
        var accountId = r.S64();
        var permission = r.U8();
        var position = ReadPosition(r);
        var houseName = r.Str();
        var allowRecover = r.Bool();
        var salePrice = r.U64();
        var sellToName = r.Str();
        var expandedDecorationLimit = r.U32();
        var unknownField80 = r.S32();
        var isPublic = r.Bool();
        var isBoundButler = r.Bool();
        var unknownField82 = r.S32();

        var uccSlots = new List<HouseUccSlot>(5);
        for (var i = 0; i < 5; i++)
            uccSlots.Add(new HouseUccSlot(r.S32(), r.U64(), r.U32(), r.U32()));

        var trailingPositions = new List<HousingWorldPosition>(2);
        for (var i = 0; i < 2; i++)
            trailingPositions.Add(ReadPosition(r));

        RequireConsumed(r, nameof(ParseHouseState));
        return new HouseStateEvent(
            timelineId, databaseId, objectId, packed[0], packed[1], packed[2], payMoneyAmount,
            modelId, originalOwnerId, ownerId, ownerName, accountId, permission, position, houseName,
            allowRecover, salePrice, sellToName, expandedDecorationLimit, unknownField80, isPublic,
            isBoundButler, unknownField82, uccSlots, trailingPositions);
    }

    public static HouseBuildProgressEvent ParseBuildProgress(byte[] body)
    {
        var r = new WireReader(body);
        var result = new HouseBuildProgressEvent(r.U16(), r.U32(), r.S32(), r.S32());
        RequireConsumed(r, nameof(ParseBuildProgress));
        return result;
    }

    public static HousePermissionChangedEvent ParsePermissionChanged(byte[] body)
    {
        var r = new WireReader(body);
        var result = new HousePermissionChangedEvent(r.U16(), r.U8());
        RequireConsumed(r, nameof(ParsePermissionChanged));
        return result;
    }

    public static HouseDemolishedEvent ParseDemolished(byte[] body)
    {
        var r = new WireReader(body);
        var result = new HouseDemolishedEvent(r.U16());
        RequireConsumed(r, nameof(ParseDemolished));
        return result;
    }

    public static HouseDataEvent ParseHouseData(byte[] body)
    {
        var r = new WireReader(body);
        var count = r.U8();
        if (count > 20)
            throw new WireException($"invalid owned-house count {count}");

        var houses = new List<OwnedHouseRecord>(count);
        for (var i = 0; i < count; i++)
        {
            houses.Add(new OwnedHouseRecord(
                r.U16(), r.U64(), r.Bc(), r.S64(), r.Str(), ReadPosition(r), r.S32(), r.U8(), r.Str()));
        }

        RequireConsumed(r, nameof(ParseHouseData));
        return new HouseDataEvent(houses);
    }

    public static HouseRemovedEvent ParseRemoved(byte[] body)
    {
        var r = new WireReader(body);
        var result = new HouseRemovedEvent(r.U16());
        RequireConsumed(r, nameof(ParseRemoved));
        return result;
    }

    public static HouseFarmSummaryEvent ParseFarmSummary(byte[] body)
    {
        var r = new WireReader(body);
        var result = new HouseFarmSummaryEvent(r.Str(), r.S32(), r.S32());
        RequireConsumed(r, nameof(ParseFarmSummary));
        return result;
    }

    /// <summary>
    /// SCHouseTaxInfo (0x0F7), using AAEmu's native-verified writer including both u64 money values.
    /// </summary>
    public static HouseTaxInfoEvent ParseTaxInfo(byte[] body)
    {
        var r = new WireReader(body);
        var result = new HouseTaxInfoEvent(
            r.U16(), r.U32(), r.U32(), r.U64(), r.U64(), r.S64(), r.Bool(), r.S8(), r.U8(), r.Bool(), r.U8());
        RequireConsumed(r, nameof(ParseTaxInfo));
        return result;
    }

    private static HousingWorldPosition ReadPosition(WireReader r) =>
        new(r.S64(), r.S64(), r.F32());

    private static void RequireConsumed(WireReader r, string parser)
    {
        if (r.Remaining != 0)
            throw new WireException($"{parser} left {r.Remaining} trailing bytes");
    }
}

/// <summary>Parser-family adapter for the contiguous 0x0F0-0x0F7 housing packet range.</summary>
public sealed class HousingPacketParserFamily : IPacketParserFamily
{
    public bool TryParse(ushort opcode, byte[] body, out IReadOnlyList<GameEvent> events)
    {
        if (opcode is < HousingProtocol.SCHouseState or > HousingProtocol.SCHouseTaxInfo)
        {
            events = [];
            return false;
        }

        events = ParserFamilyResult.One(HousingProtocol.Parse(opcode, body));
        return true;
    }
}
