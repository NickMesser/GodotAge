#nullable enable
using System.Buffers.Binary;
using AAEmu.GodotViewer.Client;
using AAEmu.GodotViewer.Net;

namespace AAEmu.GodotViewer.Ui.X2.Online;

/// <summary>Server owned action slots. Script indices are the server array indices.</summary>
public sealed class X2ActionSlots : IDisposable
{
    public const int SnapshotLength = 217;
    public const int SavedLength = 85;
    public const ushort SnapshotOpcode = 0x170;
    public const ushort SaveOpcode = 0x0f7;

    public enum Kind : byte { Empty = 0, ItemType = 1, Skill = 2, Macro = 3, ItemId = 4, RidePetSpell = 5 }
    public readonly record struct Entry(Kind Type, ulong Id)
    {
        public bool IsEmpty => Type == Kind.Empty;
    }

    private readonly Entry[] _entries = new Entry[SnapshotLength];
    private OnlineSession? _session;
    public event Action? Changed;
    public IReadOnlyList<Entry> Entries => _entries;

    public void Attach(OnlineSession session)
    {
        if (ReferenceEquals(_session, session)) return;
        Detach();
        _session = session;
        session.RawPacket += OnRawPacket;
    }

    public void Detach()
    {
        if (_session != null) _session.RawPacket -= OnRawPacket;
        _session = null;
        Array.Clear(_entries);
        Changed?.Invoke();
    }

    public Entry Get(int slot) => slot is >= 1 and < SnapshotLength ? _entries[slot] : default;

    private void OnRawPacket(RawPacketEvent packet)
    {
        if (packet.Opcode == SnapshotOpcode && TryParse(packet.Body, out var entries))
        {
            Array.Copy(entries, _entries, SnapshotLength);
            Changed?.Invoke();
        }
    }

    public static bool TryParse(ReadOnlySpan<byte> body, out Entry[] entries)
    {
        entries = new Entry[SnapshotLength];
        var position = 0;
        for (var i = 0; i < entries.Length; i++)
        {
            if (position >= body.Length) return false;
            var type = (Kind)body[position++];
            ulong id;
            switch (type)
            {
                case Kind.Empty:
                case Kind.Macro:
                    id = 0;
                    break;
                case Kind.ItemType:
                case Kind.Skill:
                case Kind.RidePetSpell:
                    if (body.Length - position < 4) return false;
                    id = BinaryPrimitives.ReadUInt32LittleEndian(body[position..]);
                    position += 4;
                    break;
                case Kind.ItemId:
                    if (body.Length - position < 8) return false;
                    id = BinaryPrimitives.ReadUInt64LittleEndian(body[position..]);
                    position += 8;
                    break;
                default:
                    return false;
            }
            entries[i] = new Entry(type, id);
        }
        return position == body.Length;
    }

    public static byte[]? EncodeSave(int slot, Entry entry)
    {
        if (slot is < 1 or >= SavedLength || entry.Type == Kind.Macro) return null;
        var body = entry.Type switch
        {
            Kind.Empty => new byte[2],
            Kind.ItemType or Kind.Skill or Kind.RidePetSpell when entry.Id <= uint.MaxValue => new byte[6],
            Kind.ItemId => new byte[10],
            _ => null,
        };
        if (body == null) return null;
        body[0] = checked((byte)slot);
        body[1] = (byte)entry.Type;
        if (body.Length == 6) BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(2), (uint)entry.Id);
        if (body.Length == 10) BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(2), entry.Id);
        return body;
    }

    public bool Set(int slot, Entry entry)
    {
        var body = EncodeSave(slot, entry);
        if (_session == null || body == null) return false;
        _session.Client.SendGame(SaveOpcode, body);
        _entries[slot] = entry;
        Changed?.Invoke();
        return true;
    }

    public bool Swap(int first, int second)
    {
        if (first == second || _session == null || EncodeSave(first, Get(second)) == null ||
            EncodeSave(second, Get(first)) == null) return false;
        var a = Get(first);
        var b = Get(second);
        Set(first, b);
        Set(second, a);
        return true;
    }

    public void Dispose() => Detach();
}
