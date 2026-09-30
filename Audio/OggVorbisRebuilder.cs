using System.Buffers.Binary;

namespace AAEmu.GodotViewer.Audio;

public sealed record VorbisSetupHeader(byte[] Packet, byte ShortBlockExponent, byte LongBlockExponent);

/// <summary>CRC-keyed collection of complete Vorbis setup packets (packet type 5).</summary>
public sealed class VorbisSetupHeaderRegistry
{
    private readonly Dictionary<uint, VorbisSetupHeader> _headers = [];

    public VorbisSetupHeaderRegistry(bool includeBuiltIns = true)
    {
        if (includeBuiltIns) BuiltInVorbisSetupHeaders.RegisterInto(this);
    }

    public int Count => _headers.Count;

    /// <summary>Registers a packet under the CRC stored in FSB5 metadata.</summary>
    public void Register(uint fsbCrc, ReadOnlySpan<byte> setupPacket,
        byte shortBlockExponent = 8, byte longBlockExponent = 11)
    {
        ValidateSetupPacket(setupPacket);
        ValidateBlockSizes(shortBlockExponent, longBlockExponent);
        var actualCrc = Crc32.ComputeReflected(setupPacket);
        if (actualCrc != fsbCrc)
            throw new InvalidDataException(
                $"Vorbis setup packet CRC is 0x{actualCrc:X8}, not registry key 0x{fsbCrc:X8}.");
        _headers[fsbCrc] = new VorbisSetupHeader(setupPacket.ToArray(), shortBlockExponent, longBlockExponent);
    }

    /// <summary>Computes the standard reflected CRC32 and uses it as the lookup key.</summary>
    public uint Register(ReadOnlySpan<byte> setupPacket,
        byte shortBlockExponent = 8, byte longBlockExponent = 11)
    {
        ValidateSetupPacket(setupPacket);
        ValidateBlockSizes(shortBlockExponent, longBlockExponent);
        var crc = Crc32.ComputeReflected(setupPacket);
        _headers[crc] = new VorbisSetupHeader(setupPacket.ToArray(), shortBlockExponent, longBlockExponent);
        return crc;
    }

    public bool TryGet(uint fsbCrc, out VorbisSetupHeader? setupHeader) =>
        _headers.TryGetValue(fsbCrc, out setupHeader);

    private static void ValidateSetupPacket(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 8 || packet[0] != 5 || !packet.Slice(1, 6).SequenceEqual("vorbis"u8))
            throw new InvalidDataException("A Vorbis setup packet must begin with 05 'vorbis'.");
    }

    private static void ValidateBlockSizes(byte shortExponent, byte longExponent)
    {
        if (shortExponent is < 6 or > 13 || longExponent is < 6 or > 13 || shortExponent > longExponent)
            throw new ArgumentOutOfRangeException(nameof(shortExponent),
                "Vorbis block-size exponents must be 6..13, with the short size first.");
    }
}

internal static class OggVorbisRebuilder
{
    public static byte[] Build(Fsb5Sample sample, ReadOnlySpan<byte> fsbData, VorbisSetupHeader setupHeader)
    {
        var packets = ReadFsbPackets(fsbData);
        if (packets.Count == 0)
            throw new InvalidDataException("The FSB Vorbis payload contains no packets.");
        var modes = VorbisSetupParser.Parse(setupHeader.Packet, sample.Channels);
        var granules = VorbisSetupParser.CalculateGranules(packets, modes,
            setupHeader.ShortBlockExponent, setupHeader.LongBlockExponent, sample.SampleCount);

        using var output = new MemoryStream();
        var serial = sample.VorbisSetupCrc.GetValueOrDefault(0x4141454d) ^ (uint)sample.Index;
        uint sequence = 0;
        WritePage(output, MakeIdentification(sample.Channels, sample.SampleRate,
            setupHeader.ShortBlockExponent, setupHeader.LongBlockExponent), 0, 0x02, serial, sequence++);
        WritePage(output, MakeComment(), 0, 0, serial, sequence++);
        WritePage(output, setupHeader.Packet, 0, 0, serial, sequence++);
        for (var i = 0; i < packets.Count; i++)
        {
            var final = i == packets.Count - 1;
            WritePage(output, packets[i], granules[i], final ? (byte)0x04 : (byte)0,
                serial, sequence++);
        }
        return output.ToArray();
    }

    private static List<byte[]> ReadFsbPackets(ReadOnlySpan<byte> data)
    {
        var packets = new List<byte[]>();
        var cursor = 0;
        while (cursor < data.Length)
        {
            if (data.Length - cursor < 2)
            {
                if (IsZeroPadding(data[cursor..])) break;
                throw new InvalidDataException("The final FSB Vorbis packet length is truncated.");
            }
            var size = BinaryPrimitives.ReadUInt16LittleEndian(data[cursor..]);
            cursor += 2;
            if (size == 0)
            {
                if (IsZeroPadding(data[(cursor - 2)..])) break;
                throw new InvalidDataException("The FSB Vorbis payload contains an empty packet before nonzero data.");
            }
            if (size > data.Length - cursor)
                throw new InvalidDataException("An FSB Vorbis packet extends outside its sample data extent.");
            packets.Add(data.Slice(cursor, size).ToArray());
            cursor += size;
        }
        return packets;
    }

    private static byte[] MakeIdentification(int channels, int sampleRate,
        byte shortBlockExponent, byte longBlockExponent)
    {
        if (channels is < 1 or > 255 || sampleRate <= 0)
            throw new InvalidDataException("The sample has invalid Vorbis stream parameters.");
        var packet = new byte[30];
        packet[0] = 1;
        "vorbis"u8.CopyTo(packet.AsSpan(1));
        packet[11] = (byte)channels;
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(12), (uint)sampleRate);
        packet[28] = (byte)(shortBlockExponent | (longBlockExponent << 4));
        packet[29] = 1;
        return packet;
    }

    private static byte[] MakeComment()
    {
        var vendor = "AAEmu FSB5"u8;
        var packet = new byte[1 + 6 + 4 + vendor.Length + 4 + 1];
        packet[0] = 3;
        "vorbis"u8.CopyTo(packet.AsSpan(1));
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(7), (uint)vendor.Length);
        vendor.CopyTo(packet.AsSpan(11));
        packet[^1] = 1;
        return packet;
    }

    private static void WritePage(Stream stream, ReadOnlySpan<byte> packet, long granule,
        byte headerType, uint serial, uint sequence)
    {
        var fullSegments = packet.Length / 255;
        var remainder = packet.Length % 255;
        var segmentCount = fullSegments + 1; // An exact multiple needs a zero lacing terminator.
        if (segmentCount > 255)
            throw new NotSupportedException("A single Vorbis packet is too large for this one-packet Ogg page writer.");

        var page = new byte[27 + segmentCount + packet.Length];
        "OggS"u8.CopyTo(page);
        page[4] = 0;
        page[5] = headerType;
        BinaryPrimitives.WriteInt64LittleEndian(page.AsSpan(6), granule);
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(14), serial);
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(18), sequence);
        page[26] = (byte)segmentCount;
        for (var i = 0; i < fullSegments; i++) page[27 + i] = 255;
        page[27 + fullSegments] = (byte)remainder;
        packet.CopyTo(page.AsSpan(27 + segmentCount));
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(22), OggCrc.Compute(page));
        stream.Write(page);
    }

    private static bool IsZeroPadding(ReadOnlySpan<byte> bytes)
    {
        foreach (var value in bytes)
            if (value != 0) return false;
        return true;
    }
}

internal static class Crc32
{
    public static uint ComputeReflected(ReadOnlySpan<byte> bytes)
    {
        uint crc = 0xffffffff;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0);
        }
        return ~crc;
    }
}

internal static class OggCrc
{
    public static uint Compute(ReadOnlySpan<byte> bytes)
    {
        uint crc = 0;
        foreach (var value in bytes)
        {
            crc ^= (uint)value << 24;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc << 1) ^ ((crc & 0x80000000) != 0 ? 0x04c11db7u : 0);
        }
        return crc;
    }
}
