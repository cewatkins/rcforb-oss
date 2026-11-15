using System.Buffers.Binary;
using System.Net.Sockets;
using System.Threading.Channels;
using RCForb.Protocol.V07;

namespace RCForb.Client.V07;

/// <summary>
/// The v0.7 audio connection: 10-byte header, one payload per packet, no checksum.
/// </summary>
/// <remarks>
/// This class has no method that writes audio. The only write it offers is a short control
/// payload, which the session uses for the <c>IMA</c> liveness packet (SPEC-TRAN-047). A
/// guest therefore cannot transmit even if it wanted to: the capability is absent, not
/// merely unused.
/// </remarks>
public sealed class V07AudioChannel : IAsyncDisposable
{
    private const int HeaderBytes = 10;
    private const int ReadChunkBytes = 4096;

    private readonly V07FrameReader _reader = new();
    private readonly Channel<V07Frame> _frames = Channel.CreateUnbounded<V07Frame>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _pumpCts = new();

    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private Task? _pump;

    public DateTime LastInboundUtc { get; private set; } = DateTime.UtcNow;
    public long BytesReceived { get; private set; }
    public string? Fault { get; private set; }

    public async Task ConnectAsync(string host, int port, CancellationToken cancellationToken)
    {
        _tcp = new TcpClient { NoDelay = true };
        await _tcp.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
        _stream = _tcp.GetStream();
        LastInboundUtc = DateTime.UtcNow;
        _pump = Task.Run(() => PumpAsync(_pumpCts.Token), CancellationToken.None);
    }

    /// <summary>
    /// Sends the liveness control packet. This is the only write this class performs: there
    /// is no audio method, and the header flag is fixed at <c>1</c>, never <c>2</c>.
    /// </summary>
    /// <remarks>
    /// Byte 4 is <c>1</c> for a control packet that does not key the transmitter and <c>2</c>
    /// for one that does (SPEC-TRAN-032). Sending <c>2</c> here would tell the server the
    /// guest is keyed every two seconds, which is exactly what a listen-only client must not
    /// say.
    /// </remarks>
    public async Task SendAliveAsync(CancellationToken cancellationToken)
    {
        const string payload = "IMA";

        var packet = new byte[HeaderBytes + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(packet, payload.Length);
        packet[4] = 1;
        System.Text.Encoding.ASCII.GetBytes(payload, packet.AsSpan(HeaderBytes));

        var stream = _stream ?? throw new InvalidOperationException("The audio socket is not connected.");

        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(packet, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>
    /// Sends the 64-byte session packet that binds the audio socket to the control session.
    /// Required on the dual-TCP path after a successful login (SPEC 02 §4.6, SPEC-SESS-007).
    /// </summary>
    public async Task SendSessionPacketAsync(byte[] packet, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(packet);
        if (packet.Length != 64)
        {
            throw new ArgumentException("Session packet must be exactly 64 bytes.", nameof(packet));
        }

        var stream = _stream ?? throw new InvalidOperationException("The audio socket is not connected.");

        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(packet, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>Waits for the next frame; null means the stream ended.</summary>
    public async Task<V07Frame?> ReadFrameAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (await _frames.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false) &&
                _frames.Reader.TryRead(out var frame))
            {
                return frame;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Caller-supplied windows are handled by the session; propagate real cancellation.
            throw;
        }

        return null;
    }

    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[ReadChunkBytes];

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var read = await _stream!.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                BytesReceived += read;
                // Drain before appending so the 16 KiB cap can never be hit by a burst.
                while (_reader.TryRead(out var pending) == V07ReadResult.Frame)
                {
                    if (!_frames.Writer.TryWrite(pending))
                    {
                        return;
                    }
                }

                _reader.Append(buffer.AsSpan(0, read));
                LastInboundUtc = DateTime.UtcNow;

                while (_reader.TryRead(out var frame) == V07ReadResult.Frame)
                {
                    if (!_frames.Writer.TryWrite(frame))
                    {
                        return;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or InvalidOperationException)
        {
            Fault = ex.Message;
        }
        finally
        {
            _frames.Writer.TryComplete();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _pumpCts.Cancel();
        try
        {
            _stream?.Close();
            _tcp?.Close();
        }
        catch (ObjectDisposedException)
        {
        }

        if (_pump is not null)
        {
            try
            {
                await _pump.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _pumpCts.Dispose();
        _sendLock.Dispose();
    }
}
