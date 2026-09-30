#nullable enable
using System.Buffers.Binary;
using System.Net.Sockets;

namespace AAEmu.GodotViewer.Net;

/// <summary>
/// A TCP connection carrying u16-length-prefixed frames (the length excludes itself). Both the login
/// (:1237) and game (:1239) channels use this outer framing.
/// </summary>
internal sealed class FrameSocket : IDisposable
{
    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;
    private readonly object _writeLock = new();
    private int _closed;

    private FrameSocket(TcpClient tcp)
    {
        _tcp = tcp;
        _tcp.NoDelay = true;
        _stream = tcp.GetStream();
    }

    public string RemoteEndPoint => _tcp.Client.RemoteEndPoint?.ToString() ?? "?";

    public static async Task<FrameSocket> ConnectAsync(string host, int port, CancellationToken ct)
    {
        var tcp = new TcpClient();
        try
        {
            await tcp.ConnectAsync(host, port, ct).ConfigureAwait(false);
            return new FrameSocket(tcp);
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    /// <summary>Reads one frame payload; null when the peer closed the connection cleanly.</summary>
    public async Task<byte[]?> ReadFrameAsync(CancellationToken ct)
    {
        var header = new byte[2];
        var got = 0;
        while (got < 2)
        {
            var n = await _stream.ReadAsync(header.AsMemory(got), ct).ConfigureAwait(false);
            if (n == 0)
                return null;
            got += n;
        }
        var len = BinaryPrimitives.ReadUInt16LittleEndian(header);
        var payload = new byte[len];
        await _stream.ReadExactlyAsync(payload, ct).ConfigureAwait(false);
        return payload;
    }

    /// <summary>Writes one frame (length prefix added here). Thread-safe.</summary>
    public void WriteFrame(ReadOnlySpan<byte> payload)
    {
        if (payload.Length > ushort.MaxValue)
            throw new ArgumentException($"frame of {payload.Length} bytes exceeds the u16 length field");
        var frame = new byte[payload.Length + 2];
        BinaryPrimitives.WriteUInt16LittleEndian(frame, (ushort)payload.Length);
        payload.CopyTo(frame.AsSpan(2));
        lock (_writeLock)
            _stream.Write(frame);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
            return;
        try { _tcp.Client.Shutdown(SocketShutdown.Both); } catch { /* already gone */ }
        _tcp.Dispose();
    }
}
