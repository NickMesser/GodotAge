#nullable enable
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AAEmu.GodotViewer;

/// <summary>Read-only ArcheAge game_pak reader with a persistent file index.</summary>
public sealed class FastPak : IDisposable
{
    private const int HeaderSize = 0x200;
    private const int EntrySize = 0x150;
    private const int CacheVersion = 1;
    private const int EntriesPerChunk = 8192;
    private static readonly byte[] Key = [0x32, 0x1f, 0x2a, 0xee, 0xaa, 0x58, 0x4a, 0xb4,
        0x9a, 0x6c, 0x9e, 0x09, 0xd5, 0x9e, 0x9c, 0x6f];
    private static readonly byte[] CacheMagic = "AAFASTPK"u8.ToArray();

    private readonly FileStream _stream;
    private readonly Dictionary<string, Entry> _entries;
    private bool _disposed;

    private readonly record struct Entry(long Offset, long Size);
    private enum PakType { A, B, F }

    private FastPak(FileStream stream, Dictionary<string, Entry> entries)
    {
        _stream = stream;
        _entries = entries;
    }

    /// <summary>Open a pak, loading a validated index cache when available.</summary>
    public static FastPak Open(string pakPath, string indexCacheDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pakPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(indexCacheDirectory);

        string fullPath = Path.GetFullPath(pakPath);
        var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            bufferSize: 1, options: FileOptions.RandomAccess);
        try
        {
            long pakLength = stream.Length;
            long modifiedTicks = File.GetLastWriteTimeUtc(fullPath).Ticks;
            string pathHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fullPath.ToUpperInvariant())));
            string cachePath = Path.Combine(indexCacheDirectory,
                $"fastpak-v{CacheVersion}-{pathHash[..16]}-{pakLength:X}-{modifiedTicks:X}.idx");

            Dictionary<string, Entry>? entries = TryLoadCache(cachePath, pakLength, modifiedTicks);
            if (entries is null)
            {
                entries = ReadFileTable(stream);
                TryWriteCache(cachePath, pakLength, modifiedTicks, entries);
            }
            return new FastPak(stream, entries);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>True if the normalized path is in the pak.</summary>
    public bool Exists(string path)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(path);
        return _entries.ContainsKey(Normalize(path));
    }

    /// <summary>Read one file; returns null if its path is absent.</summary>
    public byte[]? Read(string path)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(path);
        if (!_entries.TryGetValue(Normalize(path), out Entry entry))
            return null;
        if (entry.Size > int.MaxValue)
            throw new IOException($"File is too large for a byte array: {path}");

        byte[] data = new byte[checked((int)entry.Size)];
        ReadExactly(_stream.SafeFileHandle, data, entry.Offset);
        return data;
    }

    /// <summary>Size of one file in bytes, or -1 if its path is absent.</summary>
    public long GetSize(string path)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(path);
        return _entries.TryGetValue(Normalize(path), out Entry entry) ? entry.Size : -1;
    }

    /// <summary>Streams one file into <paramref name="destination"/> without holding it in memory; false if its path is absent.</summary>
    public bool CopyTo(string path, Stream destination)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(destination);
        if (!_entries.TryGetValue(Normalize(path), out Entry entry))
            return false;
        byte[] buffer = new byte[1 << 20];
        long done = 0;
        while (done < entry.Size)
        {
            int count = (int)Math.Min(buffer.Length, entry.Size - done);
            ReadExactly(_stream.SafeFileHandle, buffer.AsSpan(0, count), entry.Offset + done);
            destination.Write(buffer, 0, count);
            done += count;
        }
        return true;
    }

    /// <summary>Enumerate normalized paths starting with the normalized prefix.</summary>
    public IEnumerable<string> List(string prefix)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(prefix);
        string normalized = Normalize(prefix);
        // Materialize so callers can enumerate independently of the reader's lifetime.
        var result = new List<string>();
        foreach (string path in _entries.Keys)
            if (path.StartsWith(normalized, StringComparison.OrdinalIgnoreCase))
                result.Add(path);
        return result;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stream.Dispose();
    }

    private static string Normalize(string path) => path.Replace('\\', '/').TrimStart('/').ToLowerInvariant();

    private static void ReadExactly(SafeFileHandle handle, Span<byte> buffer, long offset)
    {
        while (!buffer.IsEmpty)
        {
            int count = RandomAccess.Read(handle, buffer, offset);
            if (count == 0) throw new EndOfStreamException("Unexpected end of pak");
            buffer = buffer[count..];
            offset += count;
        }
    }

    private static Dictionary<string, Entry> ReadFileTable(FileStream stream)
    {
        long pakLength = stream.Length;
        if (pakLength < HeaderSize) throw new InvalidDataException("Pak is too short");
        byte[] encryptedHeader = new byte[HeaderSize];
        ReadExactly(stream.SafeFileHandle, encryptedHeader, pakLength - HeaderSize);

        using Aes aes = Aes.Create();
        aes.Key = Key;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;
        aes.IV = new byte[16];
        using ICryptoTransform headerDecryptor = aes.CreateDecryptor();
        byte[] header = headerDecryptor.TransformFinalBlock(encryptedHeader, 0, HeaderSize);
        PakType type;
        uint fileCount, extraCount;
        if (header.AsSpan(0, 4).SequenceEqual("WIBO"u8))
        {
            type = PakType.A;
            fileCount = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8));
            extraCount = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12));
        }
        else if (header.AsSpan(8, 4).SequenceEqual("IDEJ"u8))
        {
            type = PakType.B;
            fileCount = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12));
            extraCount = BinaryPrimitives.ReadUInt32LittleEndian(header);
        }
        else if (header.AsSpan(0, 4).SequenceEqual("ZERO"u8))
        {
            type = PakType.F;
            fileCount = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8));
            extraCount = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12));
        }
        else throw new InvalidDataException("Unknown ArcheAge pak header");

        long totalCount = (long)fileCount + extraCount;
        long tableBytes = checked(totalCount * EntrySize);
        long tableStart = (pakLength - HeaderSize - tableBytes) & ~0x1ffL;
        if (tableStart < 0 || totalCount > int.MaxValue || tableStart + tableBytes > pakLength - HeaderSize)
            throw new InvalidDataException("Invalid pak file table bounds");

        var entries = new Dictionary<string, Entry>(checked((int)fileCount), StringComparer.OrdinalIgnoreCase);
        byte[] encrypted = new byte[EntriesPerChunk * EntrySize];
        byte[] decrypted = new byte[encrypted.Length];
        // CBC restarts with a zero IV for each 0x150 record. Decrypt all AES blocks
        // in ECB mode, then apply the preceding ciphertext block within each record.
        aes.Mode = CipherMode.ECB;
        using ICryptoTransform blockDecryptor = aes.CreateDecryptor();
        long index = 0;
        while (index < totalCount)
        {
            int count = (int)Math.Min(EntriesPerChunk, totalCount - index);
            int byteCount = count * EntrySize;
            ReadExactly(stream.SafeFileHandle, encrypted.AsSpan(0, byteCount), tableStart + index * EntrySize);
            if (blockDecryptor.TransformBlock(encrypted, 0, byteCount, decrypted, 0) != byteCount)
                throw new CryptographicException("Incomplete FAT decryption");

            for (int record = 0; record < count; record++)
            {
                int start = record * EntrySize;
                for (int block = 16; block < EntrySize; block += 16)
                    for (int b = 0; b < 16; b++)
                        decrypted[start + block + b] ^= encrypted[start + block - 16 + b];

                long absoluteIndex = index + record;
                bool active = type == PakType.B
                    ? absoluteIndex >= extraCount
                    : absoluteIndex < fileCount;
                if (!active) continue;

                ReadOnlySpan<byte> raw = decrypted.AsSpan(start, EntrySize);
                int nameOffset = type switch { PakType.A => 0, PakType.B => 0x20, _ => 8 };
                int offsetField = type switch { PakType.A => 0x108, PakType.B => 0x130, _ => 0x110 };
                int sizeField = type == PakType.B ? 0x18 : offsetField + 8;
                ReadOnlySpan<byte> nameBytes = raw.Slice(nameOffset, 0x108);
                int nameLength = nameBytes.IndexOf((byte)0);
                if (nameLength < 0) nameLength = nameBytes.Length;
                string path = Normalize(Encoding.Latin1.GetString(nameBytes[..nameLength]));
                long offset = BinaryPrimitives.ReadInt64LittleEndian(raw[offsetField..]);
                long size = BinaryPrimitives.ReadInt64LittleEndian(raw[sizeField..]);
                if (path.Length == 0 || offset < 0 || size < 0 || offset > tableStart || size > tableStart - offset)
                    throw new InvalidDataException($"Invalid pak entry at index {absoluteIndex}");
                // AAPak's active dictionary cannot represent duplicate names. Normalization
                // can coalesce spelling variants, so keep the first encountered entry.
                entries.TryAdd(path, new Entry(offset, size));
            }
            index += count;
        }
        return entries;
    }

    private static Dictionary<string, Entry>? TryLoadCache(string path, long pakLength, long modifiedTicks)
    {
        try
        {
            if (!File.Exists(path)) return null;
            byte[] bytes = File.ReadAllBytes(path);
            if (bytes.Length < 8 + 4 + 8 + 8 + 4 + 32) return null;
            int payloadLength = bytes.Length - 32;
            if (!SHA256.HashData(bytes.AsSpan(0, payloadLength)).AsSpan().SequenceEqual(bytes.AsSpan(payloadLength)))
                return null;
            using var reader = new BinaryReader(new MemoryStream(bytes, 0, payloadLength, false), Encoding.UTF8);
            if (!reader.ReadBytes(8).AsSpan().SequenceEqual(CacheMagic) ||
                reader.ReadInt32() != CacheVersion || reader.ReadInt64() != pakLength ||
                reader.ReadInt64() != modifiedTicks) return null;
            int count = reader.ReadInt32();
            if (count < 0 || count > 10_000_000) return null;
            var entries = new Dictionary<string, Entry>(count, StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < count; i++)
            {
                string name = reader.ReadString();
                long offset = reader.ReadInt64();
                long size = reader.ReadInt64();
                if (name.Length == 0 || name != Normalize(name) || offset < 0 || size < 0 ||
                    offset > pakLength - HeaderSize || size > pakLength - HeaderSize - offset ||
                    !entries.TryAdd(name, new Entry(offset, size))) return null;
            }
            return reader.BaseStream.Position == payloadLength ? entries : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or OutOfMemoryException)
        {
            return null;
        }
    }

    private static void TryWriteCache(string path, long pakLength, long modifiedTicks,
        Dictionary<string, Entry> entries)
    {
        string? temp = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var memory = new MemoryStream();
            using (var writer = new BinaryWriter(memory, Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(CacheMagic);
                writer.Write(CacheVersion);
                writer.Write(pakLength);
                writer.Write(modifiedTicks);
                writer.Write(entries.Count);
                foreach (var (name, entry) in entries)
                {
                    writer.Write(name);
                    writer.Write(entry.Offset);
                    writer.Write(entry.Size);
                }
            }
            byte[] digest = SHA256.HashData(memory.GetBuffer().AsSpan(0, checked((int)memory.Length)));
            memory.Write(digest);
            temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                memory.WriteTo(output);
            File.Move(temp, path, overwrite: true);
            temp = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The pak remains usable if its optional cache cannot be written.
        }
        finally
        {
            if (temp is not null)
                try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
