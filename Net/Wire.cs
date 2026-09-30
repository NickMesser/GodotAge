#nullable enable
using System.Buffers.Binary;
using System.Text;

namespace AAEmu.GodotViewer.Net;

/// <summary>Thrown when a packet body is shorter than its layout requires.</summary>
public sealed class WireException(string message) : Exception(message);

/// <summary>
/// Little-endian reader over one packet body. Unlike the server's PacketStream it throws on overrun,
/// so a layout mismatch surfaces as an error instead of silently decoding zeros.
/// </summary>
public sealed class WireReader(byte[] buffer, int offset, int count)
{
    private readonly int _end = offset + count;

    public WireReader(byte[] buffer) : this(buffer, 0, buffer.Length) { }

    public byte[] Buffer { get; } = buffer;
    public int Pos { get; set; } = offset;
    public int Remaining => _end - Pos;

    private int Take(int n)
    {
        if (n < 0 || Pos + n > _end)
            throw new WireException($"read of {n} bytes at {Pos} overruns body end {_end}");
        var p = Pos;
        Pos += n;
        return p;
    }

    public byte U8() => Buffer[Take(1)];
    public sbyte S8() => (sbyte)Buffer[Take(1)];
    public bool Bool() => Buffer[Take(1)] != 0;
    public short S16() => BinaryPrimitives.ReadInt16LittleEndian(Buffer.AsSpan(Take(2), 2));
    public ushort U16() => BinaryPrimitives.ReadUInt16LittleEndian(Buffer.AsSpan(Take(2), 2));
    public int S32() => BinaryPrimitives.ReadInt32LittleEndian(Buffer.AsSpan(Take(4), 4));
    public uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(Buffer.AsSpan(Take(4), 4));
    public long S64() => BinaryPrimitives.ReadInt64LittleEndian(Buffer.AsSpan(Take(8), 8));
    public ulong U64() => BinaryPrimitives.ReadUInt64LittleEndian(Buffer.AsSpan(Take(8), 8));
    public float F32() => BinaryPrimitives.ReadSingleLittleEndian(Buffer.AsSpan(Take(4), 4));

    /// <summary>24-bit object id ("bc").</summary>
    public uint Bc()
    {
        var p = Take(3);
        return (uint)(Buffer[p] | (Buffer[p + 1] << 8) | (Buffer[p + 2] << 16));
    }

    public byte[] Bytes(int n)
    {
        var p = Take(n);
        return Buffer.AsSpan(p, n).ToArray();
    }

    /// <summary>u16 length followed by that many bytes.</summary>
    public byte[] Blob() => Bytes(U16());

    /// <summary>u16 byte length followed by UTF-8 text (trailing NULs trimmed).</summary>
    public string Str()
    {
        var n = U16();
        var p = Take(n);
        return Encoding.UTF8.GetString(Buffer, p, n).TrimEnd('\0');
    }

    /// <summary>11-byte quantized world position, see <see cref="WorldPosition"/>.</summary>
    public System.Numerics.Vector3 Position() => WorldPosition.Decode(Buffer.AsSpan(Take(11), 11));

    /// <summary>
    /// Packed unsigned ints: groups of up to four values, each group led by a byte holding
    /// 2 bits per value (byte length - 1), then the values little-endian in that many bytes.
    /// </summary>
    public uint[] Pisc(int count)
    {
        var result = new uint[count];
        var read = 0;
        while (read < count)
        {
            var group = Math.Min(4, count - read);
            var lengths = U8();
            for (var j = 0; j < group; j++)
            {
                var len = ((lengths >> (2 * j)) & 3) + 1;
                uint v = 0;
                for (var b = 0; b < len; b++)
                    v |= (uint)U8() << (8 * b);
                result[read++] = v;
            }
        }
        return result;
    }

    public byte[] Rest() => Bytes(Remaining);
}

/// <summary>Little-endian packet body builder.</summary>
public sealed class WireWriter
{
    private byte[] _buf = new byte[64];

    public int Length { get; private set; }

    private Span<byte> Grow(int n)
    {
        if (Length + n > _buf.Length)
            Array.Resize(ref _buf, Math.Max(_buf.Length * 2, Length + n));
        var span = _buf.AsSpan(Length, n);
        Length += n;
        return span;
    }

    public WireWriter U8(byte v) { Grow(1)[0] = v; return this; }
    public WireWriter S8(sbyte v) { Grow(1)[0] = (byte)v; return this; }
    public WireWriter Bool(bool v) { Grow(1)[0] = v ? (byte)1 : (byte)0; return this; }
    public WireWriter S16(short v) { BinaryPrimitives.WriteInt16LittleEndian(Grow(2), v); return this; }
    public WireWriter U16(ushort v) { BinaryPrimitives.WriteUInt16LittleEndian(Grow(2), v); return this; }
    public WireWriter S32(int v) { BinaryPrimitives.WriteInt32LittleEndian(Grow(4), v); return this; }
    public WireWriter U32(uint v) { BinaryPrimitives.WriteUInt32LittleEndian(Grow(4), v); return this; }
    public WireWriter S64(long v) { BinaryPrimitives.WriteInt64LittleEndian(Grow(8), v); return this; }
    public WireWriter U64(ulong v) { BinaryPrimitives.WriteUInt64LittleEndian(Grow(8), v); return this; }
    public WireWriter F32(float v) { BinaryPrimitives.WriteSingleLittleEndian(Grow(4), v); return this; }

    public WireWriter Bc(uint v)
    {
        var s = Grow(3);
        s[0] = (byte)v;
        s[1] = (byte)(v >> 8);
        s[2] = (byte)(v >> 16);
        return this;
    }

    public WireWriter Bytes(ReadOnlySpan<byte> v) { v.CopyTo(Grow(v.Length)); return this; }

    public WireWriter Str(string v)
    {
        var bytes = Encoding.UTF8.GetBytes(v);
        return U16((ushort)bytes.Length).Bytes(bytes);
    }

    public WireWriter Position(System.Numerics.Vector3 p)
    {
        WorldPosition.Encode(p, Grow(11));
        return this;
    }

    public byte[] ToArray() => _buf.AsSpan(0, Length).ToArray();
}
