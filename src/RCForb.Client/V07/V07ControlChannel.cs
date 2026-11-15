using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;

namespace RCForb.Client.V07;

public enum ControlReadStatus
{
    /// <summary>A non-empty or empty line arrived.</summary>
    Line,

    /// <summary>Nothing arrived within the caller's window.</summary>
    Timeout,

    /// <summary>The peer closed the stream or the read failed.</summary>
    Closed,
}

/// <summary>
/// The v0.7 control connection: UTF-8 lines, <c>LF</c>-terminated on send, no length prefix.
/// One socket belongs to one attempt and is never reused (SPEC-TRAN-057).
/// </summary>
/// <remarks>
/// The class exposes a byte-level <see cref="SendAsync"/>; it does not know which commands a
/// guest may send. Keeping that policy in the session is what makes "listen only" auditable.
/// </remarks>
public sealed class V07ControlChannel : IAsyncDisposable
{
    private const int ReadChunkBytes = 4096;

    private readonly ControlLineReader _reader = new();
    private readonly Channel<string> _lines = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly List<string> _sent = new();
    private readonly CancellationTokenSource _pumpCts = new();

    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private Task? _pump;

    public DateTime LastInboundUtc { get; private set; } = DateTime.UtcNow;
    public long BytesReceived { get; private set; }
    public string? Fault { get; private set; }

    /// <summary>Every command written, in order. Diagnostic only; no credentials are ever sent.</summary>
    public IReadOnlyList<string> SentCommands
    {
        get
        {
            lock (_sent)
            {
                return _sent.ToArray();
            }
        }
    }

    public async Task ConnectAsync(string host, int port, CancellationToken cancellationToken)
    {
        _tcp = new TcpClient { NoDelay = true };
        await _tcp.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
        _stream = _tcp.GetStream();
        LastInboundUtc = DateTime.UtcNow;
        _pump = Task.Run(() => PumpAsync(_pumpCts.Token), CancellationToken.None);
    }

    public async Task SendAsync(string command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.Contains('\r') || command.Contains('\n'))
        {
            throw new ArgumentException("A control command may not contain CR or LF.", nameof(command));
        }

        var bytes = Encoding.UTF8.GetBytes(command + "\n");
        if (bytes.Length > 8192)
        {
            throw new ArgumentException("The control line exceeds the 8 KiB read buffer.", nameof(command));
        }

        var stream = _stream ?? throw new InvalidOperationException("The control socket is not connected.");

        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            lock (_sent)
            {
                _sent.Add(command);
            }
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>
    /// Waits for one line. The timeout is the caller's liveness window, not a protocol timer;
    /// the session decides what a timeout means (SPEC-TRAN-052).
    /// </summary>
    public async Task<(ControlReadStatus Status, string Line)> ReadLineAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var window = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        window.CancelAfter(timeout);

        try
        {
            if (await _lines.Reader.WaitToReadAsync(window.Token).ConfigureAwait(false) &&
                _lines.Reader.TryRead(out var line))
            {
                return (ControlReadStatus.Line, line);
            }

            return (ControlReadStatus.Closed, string.Empty);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (ControlReadStatus.Timeout, string.Empty);
        }
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
                _reader.Append(buffer.AsSpan(0, read));

                while (_reader.TryReadLine(out var line))
                {
                    if (line.Length > 0)
                    {
                        LastInboundUtc = DateTime.UtcNow;
                    }

                    if (!_lines.Writer.TryWrite(line))
                    {
                        return;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            Fault = ex.Message;
        }
        finally
        {
            _lines.Writer.TryComplete();
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
