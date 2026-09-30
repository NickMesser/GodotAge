#nullable enable
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace AAEmu.GodotViewer.Net;

/// <summary>
/// Game-channel (World :1239) ciphers, the client-side mirror of AAEmu.Commons EncryptionManager.
/// </summary>
/// <remarks>
/// S->C level 5: keyless XOR stream seeded by the payload length, so decrypting is the same operation as
/// encrypting. C->S level 5: AES-128-CBC (key chosen by the client, IV chained from the previous
/// packet's last cipher block, zero at start) followed by a XOR stream keyed by the client's XOR head
/// and a per-packet sequence. Both client keys travel RSA-encrypted in CSAesXorKey (opcode 0x047).
/// </remarks>
public static class GameCrypto
{
    /// <summary>checksum = checksum * 0x13 + b over every byte (low byte kept).</summary>
    public static byte Crc8(ReadOnlySpan<byte> data)
    {
        uint c = 0;
        foreach (var b in data)
            c = unchecked(c * 0x13 + b);
        return (byte)c;
    }

    private static byte Step(ref uint cry)
    {
        cry = unchecked(cry + 0x2FCBD5u);
        var n = (byte)((cry >> 16) & 0xF7);
        return n == 0 ? (byte)0xFE : n;
    }

    /// <summary>S->C level-5 transform (its own inverse).</summary>
    public static byte[] StoC(ReadOnlySpan<byte> body)
    {
        var length = body.Length;
        var cry = (uint)(length ^ 0x1F2175A0);
        var result = new byte[length];
        var n = 4 * (length / 4);
        for (var i = n - 1; i >= 0; i--)
            result[i] = (byte)(body[i] ^ Step(ref cry));
        for (var i = n; i < length; i++)
            result[i] = (byte)(body[i] ^ Step(ref cry));
        return result;
    }
}

/// <summary>
/// Client half of the C->S level-5 cipher. Stateful: frames must be encrypted and sent in order.
/// </summary>
public sealed class ClientCipher
{
    private readonly byte[] _aesKey;
    private readonly uint _xorKey1;
    private byte[] _iv = new byte[16];
    private byte _seq;
    private uint _mSeq;
    private readonly Aes _aes;

    private ClientCipher(byte[] aesKey, byte[] xorRaw)
    {
        _aesKey = aesKey;
        var head = BinaryPrimitives.ReadUInt32LittleEndian(xorRaw);
        _xorKey1 = unchecked((head * (head ^ 0x15A02403u)) ^ 0x070F1F23u);
        _aes = Aes.Create();
        _aes.Key = _aesKey;
    }

    /// <summary>RSA-encrypted AES key (128 bytes) for CSAesXorKey.</summary>
    public byte[] EncryptedAesKey { get; private set; } = [];

    /// <summary>RSA-encrypted XOR key (128 bytes) for CSAesXorKey.</summary>
    public byte[] EncryptedXorKey { get; private set; } = [];

    /// <summary>
    /// Creates fresh random session keys and RSA-encrypts them with the server's public key blob from
    /// X2EnterWorldResponse: i32 keyBits (1024) | modulus (128, big-endian) | 125 zero bytes | exponent (3).
    /// </summary>
    public static ClientCipher Create(ReadOnlySpan<byte> publicKeyBlob)
    {
        if (publicKeyBlob.Length < 4 + 128 + 125 + 3)
            throw new WireException($"public key blob is {publicKeyBlob.Length} bytes, expected 260");
        var bits = BinaryPrimitives.ReadInt32LittleEndian(publicKeyBlob);
        var modLen = bits / 8;
        var parameters = new RSAParameters
        {
            Modulus = publicKeyBlob.Slice(4, modLen).ToArray(),
            Exponent = publicKeyBlob.Slice(publicKeyBlob.Length - 3, 3).ToArray(),
        };
        var aesKey = RandomNumberGenerator.GetBytes(16);
        var xorRaw = RandomNumberGenerator.GetBytes(16);
        using var rsa = RSA.Create();
        rsa.ImportParameters(parameters);
        return new ClientCipher(aesKey, xorRaw)
        {
            EncryptedAesKey = rsa.Encrypt(aesKey, RSAEncryptionPadding.Pkcs1),
            EncryptedXorKey = rsa.Encrypt(xorRaw, RSAEncryptionPadding.Pkcs1),
        };
    }

    private byte MakeSeq()
    {
        _mSeq = unchecked(_mSeq + 0x2FA245u);
        var r = (byte)((_mSeq >> 14) & 0x73);
        return r == 0 ? (byte)0xFE : r;
    }

    private static int SeqOffset(byte seq)
    {
        if (seq == 0) return 9;
        if (seq % 3 == 0) return 5;
        if (seq % 5 == 0) return 2;
        if (seq % 7 == 0) return 11;
        if (seq % 9 == 0) return 3;
        if (seq % 11 == 0) return 7;
        return 4;
    }

    private static byte KeyStep(ref uint cry)
    {
        cry = unchecked(cry + 0x2FCBD5u);
        var n = (byte)((cry >> 16) & 0xF7);
        return n == 0 ? (byte)0xFE : n;
    }

    /// <summary>
    /// Encrypts one plaintext [crc8][count][opcode u16][body] and returns the frame payload that follows
    /// [0xDD][0x05]: [hash][cipher], where hash = 0x2F + (length of the plaintext's last 16-byte block).
    /// </summary>
    public byte[] Encrypt(ReadOnlySpan<byte> plain)
    {
        var realLen = plain.Length;
        var blocks = Math.Max(1, (realLen + 15) / 16);
        var tail = realLen - (blocks - 1) * 16; // 1..16
        var padded = new byte[blocks * 16];
        plain.CopyTo(padded);

        var cipher = _aes.EncryptCbc(padded, _iv, PaddingMode.None);
        _iv = cipher.AsSpan(cipher.Length - 16, 16).ToArray();

        var msgKey = (uint)realLen;
        var xorKey = unchecked(_xorKey1 * _xorKey1);
        var cry = unchecked((msgKey * xorKey) ^ ((uint)MakeSeq() + 0x75A02419u) ^ 0x68BEF515u);
        var offset = SeqOffset(_seq);
        var n = offset * (cipher.Length / offset);
        for (var i = n - 1; i >= 0; i--)
            cipher[i] ^= KeyStep(ref cry);
        for (var i = n; i < cipher.Length; i++)
            cipher[i] ^= KeyStep(ref cry);
        _seq = unchecked((byte)(_seq + MakeSeq() + 1));

        var result = new byte[1 + cipher.Length];
        result[0] = (byte)(0x2F + tail);
        cipher.CopyTo(result, 1);
        return result;
    }
}
