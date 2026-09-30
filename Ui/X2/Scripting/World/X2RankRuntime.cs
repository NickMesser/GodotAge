#nullable enable
using AAEmu.GodotViewer.Lua;
using System.Buffers.Binary;

namespace AAEmu.GodotViewer.Ui.X2.Scripting.World;

/// <summary>
/// Live state and exact wire binding for the rank window. The host feeds the three ranking responses
/// from its main-thread packet callback and supplies its normal game-channel sender.
/// </summary>
public sealed class X2RankRuntime
{
    public const ushort CSRankSnapshot = 0x189;
    public const ushort CSRankRewardSnapshot = 0x18A;
    public const ushort CSRankPersonalData = 0x18B;
    public const ushort SCRankSnapshot = 0x27A;
    public const ushort SCRankRewardSnapshot = 0x27B;
    public const ushort SCRankPersonalData = 0x27C;

    private readonly Action<ushort, byte[]> _send;
    private readonly Action<string, object?[]>? _fire;
    private readonly Dictionary<(int Type, int Division, bool Reward), LuaTable> _snapshots = [];
    private readonly Dictionary<int, LuaTable> _personal = [];
    private readonly Dictionary<(int Type, int Division, bool Reward), long> _times = [];

    public X2RankRuntime(Action<ushort, byte[]> send, Action<string, object?[]>? fireEvent = null)
    {
        _send = send ?? throw new ArgumentNullException(nameof(send));
        _fire = fireEvent;
    }

    public void RequestPersonal() => _send(CSRankPersonalData, []);

    public void RequestSnapshot(int type, int division, bool reward)
    {
        var key = (type, division, reward);
        var heldTime = _times.GetValueOrDefault(key);
        var body = new byte[16];
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(0, 4), type);
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(4, 4), division);
        BinaryPrimitives.WriteInt64LittleEndian(body.AsSpan(8, 8), heldTime);
        _send(reward ? CSRankRewardSnapshot : CSRankSnapshot, body);
    }

    public LuaTable Snapshot(int type, int division, bool reward) =>
        _snapshots.TryGetValue((type, division, reward), out var rows) ? Copy(rows) : new LuaTable();

    public LuaTable Personal(int type) =>
        _personal.TryGetValue(type, out var row) ? Copy(row) : EmptyPersonal();

    /// <summary>Consumes one response. Returns false for packets outside this protocol family.</summary>
    public bool Apply(ushort opcode, byte[] body)
    {
        if (opcode == SCRankPersonalData)
        {
            ReadPersonal(body);
            _fire?.Invoke("RANK_PERSONAL_DATA", []);
            return true;
        }
        if (opcode is not (SCRankSnapshot or SCRankRewardSnapshot)) return false;

        var reward = opcode == SCRankRewardSnapshot;
        var reader = new RankReader(body);
        var type = checked((int)reader.U32());
        var division = checked((int)reader.U32());
        var count = CheckedCount(reader.U32(), 100, "rank entries");
        var rows = new LuaTable();
        for (var i = 1; i <= count; i++) rows[(double)i] = ReadEntry(reader, ordered: true);

        // AAEmu currently sends zero tier scopes. Reject a nonzero list until its native element layout
        // is bound rather than guessing widths and desynchronizing the packet.
        var scopes = CheckedCount(reader.U32(), 20, "rank scopes");
        if (scopes != 0) throw new InvalidDataException("rank scope layout is not bound");
        _ = reader.S64(); // board id, equal to the requested type in AAEmu
        var snapTime = reward ? 0L : checked((long)reader.U64());
        if (reader.Remaining != 0) throw new InvalidDataException($"rank response has {reader.Remaining} trailing byte(s)");

        _snapshots[(type, division, reward)] = rows;
        _times[(type, division, reward)] = snapTime;
        _fire?.Invoke(reward ? "RANK_REWARD_SNAPSHOTS" : "RANK_SNAPSHOTS",
            [(double)type, (double)division]);
        return true;
    }

    private void ReadPersonal(byte[] body)
    {
        var reader = new RankReader(body);
        _ = reader.S64(); // character id this complete set belongs to
        var count = CheckedCount(reader.U32(), 256, "personal rank entries");
        _personal.Clear();
        for (var i = 0; i < count; i++)
        {
            var type = checked((int)reader.U32());
            _personal[type] = ReadEntry(reader, ordered: false);
        }
        if (reader.Remaining != 0) throw new InvalidDataException($"personal rank response has {reader.Remaining} trailing byte(s)");
    }

    private static LuaTable ReadEntry(RankReader reader, bool ordered)
    {
        var v1 = reader.S64();
        var v2 = reader.S64();
        var timestamp = reader.S64();
        var world = reader.U8();
        var id = reader.U64();
        var account = reader.U64();
        var type = reader.S64();
        var privacy = reader.U8();
        uint itemId = 0;
        var counts = Array.Empty<int>();
        if (reader.Bool())
        {
            var kind = reader.U8();
            switch (kind)
            {
                case 1: itemId = reader.U32(); _ = reader.U8(); break;
                case 2: counts = [reader.S32(), reader.S32()]; break;
                case 3: counts = [reader.S32()]; break;
                case 4: counts = [reader.S32(), reader.S32(), reader.S32(), reader.S32(), reader.S32()]; break;
                case 5: counts = [reader.S32(), reader.S32(), reader.S32()]; break;
                default: throw new InvalidDataException($"unknown rank sub-data kind {kind}");
            }
        }
        var ranking = ordered ? reader.U32() : 0;
        var row = new LuaTable
        {
            ["v1"] = (double)v1, ["v2"] = (double)v2, ["score"] = (double)v1,
            ["bareScore"] = (double)v2, ["timestamp"] = (double)timestamp,
            ["worldId"] = (double)world, ["worldName"] = $"World {world}",
            ["charId"] = id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["nameCacheQueryId"] = id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["abilityCacheQueryId"] = id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["accountId"] = account.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["type"] = (double)type, ["privacyStatus"] = (double)privacy,
            ["ranking"] = (double)ranking, ["tier"] = 0d, ["isMine"] = false,
            ["name"] = "", ["ability"] = new LuaTable(), ["itemType"] = (double)itemId,
            ["itemName"] = "", ["rating"] = (double)v1, ["heirLevel"] = (double)v1,
            ["heirExpPercent"] = "0", ["value"] = (double)v1, ["weight"] = (double)v1,
            ["length"] = (double)v1, ["totalGearScore"] = (double)v1,
            ["memberCount"] = (double)(counts.Length > 0 ? Math.Max(1, counts[0]) : 1),
            ["win"] = (double)(counts.Length > 0 ? counts[0] : 0),
            ["lose"] = (double)(counts.Length > 1 ? counts[1] : 0),
            ["draw"] = (double)(counts.Length > 2 ? counts[2] : 0),
            ["kill"] = (double)(counts.Length > 3 ? counts[3] : 0),
            ["death"] = (double)(counts.Length > 4 ? counts[4] : 0),
        };
        var games = counts.Length > 2 ? counts[0] + counts[1] + counts[2] : 0;
        row["winRatio"] = games > 0 ? counts[0] * 100d / games : 0d;
        return row;
    }

    private static int CheckedCount(uint value, int maximum, string name) => value <= maximum
        ? checked((int)value) : throw new InvalidDataException($"{name} count {value} exceeds {maximum}");

    private static LuaTable EmptyPersonal() => new()
    {
        ["v1"] = 0d, ["v2"] = 0d, ["ranking"] = 0d, ["expBonusPercent"] = 0d,
    };

    private static LuaTable Copy(LuaTable source)
    {
        var result = new LuaTable();
        foreach (var (key, value) in source)
            result[key] = value is LuaTable child ? Copy(child) : value;
        return result;
    }

    private sealed class RankReader(byte[] bytes)
    {
        private int _position;
        public int Remaining => bytes.Length - _position;
        private ReadOnlySpan<byte> Take(int length)
        {
            if (length < 0 || _position + length > bytes.Length)
                throw new InvalidDataException($"rank read of {length} byte(s) at {_position} overruns {bytes.Length}");
            var result = bytes.AsSpan(_position, length);
            _position += length;
            return result;
        }
        public byte U8() => Take(1)[0];
        public bool Bool() => U8() != 0;
        public int S32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));
        public uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
        public long S64() => BinaryPrimitives.ReadInt64LittleEndian(Take(8));
        public ulong U64() => BinaryPrimitives.ReadUInt64LittleEndian(Take(8));
    }
}
