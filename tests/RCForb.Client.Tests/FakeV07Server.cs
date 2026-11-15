using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace RCForb.Client.Tests;

/// <summary>
/// A two-socket imitator of a v0.7 server. It records every control line and every byte the
/// client writes to the audio socket, which is how the listen-only tests prove the absence of
/// login and uplink traffic.
/// </summary>
internal sealed class FakeV07Server : IAsyncDisposable
{
    private readonly TcpListener _control;
    private readonly TcpListener _audio;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentQueue<string> _lines = new();
    private readonly List<byte> _audioBytes = new();
    private readonly string[] _snapshot;
    private readonly byte[]? _audioPayload;
    private readonly bool _closeControlAfterSnapshot;
    private readonly string? _authSuccessMessage;
    private readonly string? _authFailureMessage;
    private readonly IReadOnlyDictionary<string, string>? _authResponses;
    private readonly Func<string, string?>? _echoResponder;
    private readonly ConcurrentQueue<string> _pendingEchoes = new();

    private Task? _controlTask;
    private Task? _audioTask;
    private StreamWriter? _writer;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    private FakeV07Server(
        string[] snapshot,
        byte[]? audioPayload,
        bool closeControlAfterSnapshot,
        string? authSuccessMessage = null,
        string? authFailureMessage = null,
        IReadOnlyDictionary<string, string>? authResponses = null,
        Func<string, string?>? echoResponder = null)
    {
        _snapshot = snapshot;
        _audioPayload = audioPayload;
        _closeControlAfterSnapshot = closeControlAfterSnapshot;
        _authSuccessMessage = authSuccessMessage;
        _authFailureMessage = authFailureMessage;
        _authResponses = authResponses;
        _echoResponder = echoResponder;

        _control = new TcpListener(IPAddress.Loopback, 0);
        _audio = new TcpListener(IPAddress.Loopback, 0);
        _control.Start();
        _audio.Start();
    }

    public int ControlPort => ((IPEndPoint)_control.LocalEndpoint).Port;
    public int AudioPort => ((IPEndPoint)_audio.LocalEndpoint).Port;

    public IReadOnlyList<string> ControlLines => _lines.ToArray();

    public byte[] AudioBytes
    {
        get
        {
            lock (_audioBytes)
            {
                return _audioBytes.ToArray();
            }
        }
    }

    /// <summary>Releases every echo queued by the echo responder, in order.</summary>
    public async Task EchoQueuedAsync()
    {
        while (_pendingEchoes.TryDequeue(out var line))
        {
            await WriteControlLineAsync(line).ConfigureAwait(false);
        }
    }

    private async Task WriteControlLineAsync(string line)
    {
        var writer = _writer;
        if (writer is null)
        {
            return;
        }

        await _writeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await writer.WriteLineAsync(line).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>
    /// Starts the imitator. <paramref name="authResponses"/> keys a verdict message to a
    /// username, which lets a test drive refusal for one account and acceptance for another.
    /// <paramref name="echoResponder"/> maps a client line to a server echo; echoes are queued
    /// so a test can prove that state moves only when the echo is released.
    /// </summary>
    public static FakeV07Server Start(
        string[]? snapshot = null,
        byte[]? audioPayload = null,
        bool closeControlAfterSnapshot = false,
        string? authSuccessMessage = null,
        string? authFailureMessage = null,
        IReadOnlyDictionary<string, string>? authResponses = null,
        Func<string, string?>? echoResponder = null)
    {
        var server = new FakeV07Server(
            snapshot ?? ["post::radio::K3", "post::frequency::28074000", "post::state-posted"],
            audioPayload,
            closeControlAfterSnapshot,
            authSuccessMessage,
            authFailureMessage,
            authResponses,
            echoResponder);

        server._controlTask = Task.Run(server.ControlLoopAsync);
        server._audioTask = Task.Run(server.AudioLoopAsync);
        return server;
    }

    /// <summary>Five 20 ms Speex frames in one payload, as a real server sends them.</summary>
    public static byte[] FiveFramePayload() => SpeexTestPayload.Frames(5);

    private async Task ControlLoopAsync()
    {
        try
        {
            using var client = await _control.AcceptTcpClientAsync(_cts.Token);
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, new UTF8Encoding(false));
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false))
            {
                NewLine = "\n",
                AutoFlush = true,
            };
            _writer = writer;

            await WriteControlLineAsync("RCForb");

            while (!_cts.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(_cts.Token);
                if (line is null)
                {
                    return;
                }

                _lines.Enqueue(line);

                if (_echoResponder is not null && _echoResponder(line) is { } echo)
                {
                    _pendingEchoes.Enqueue(echo);
                }

                if (line == "get id")
                {
                    await WriteControlLineAsync("RCForb");
                }
                else if (line == "set protocol rcs")
                {
                    foreach (var snapshotLine in _snapshot)
                    {
                        await WriteControlLineAsync(snapshotLine);
                    }

                    if (_closeControlAfterSnapshot)
                    {
                        stream.Close();
                        return;
                    }
                }
                else if (line.StartsWith("login ", StringComparison.Ordinal))
                {
                    var parts = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
                    var user = parts.Length > 1 ? parts[1] : "";

                    if (_authResponses is not null && _authResponses.TryGetValue(user, out var response))
                    {
                        await writer.WriteLineAsync($"post::auth::{response}");
                    }
                    else if (_authSuccessMessage is not null)
                    {
                        await writer.WriteLineAsync($"post::auth::{_authSuccessMessage}");
                    }
                    else if (_authFailureMessage is not null)
                    {
                        await writer.WriteLineAsync($"post::auth::{_authFailureMessage}");
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task AudioLoopAsync()
    {
        try
        {
            using var client = await _audio.AcceptTcpClientAsync(_cts.Token);
            await using var stream = client.GetStream();

            // Four zero bytes are the server's null padding, not a frame.
            await stream.WriteAsync(new byte[4], _cts.Token);

            if (_audioPayload is { Length: > 0 })
            {
                var header = new byte[10];
                BinaryPrimitives.WriteInt32LittleEndian(header, _audioPayload.Length);
                header[4] = 1;
                await stream.WriteAsync(header, _cts.Token);
                await stream.WriteAsync(_audioPayload, _cts.Token);
            }

            var buffer = new byte[512];
            while (!_cts.IsCancellationRequested)
            {
                var read = await stream.ReadAsync(buffer, _cts.Token);
                if (read == 0)
                {
                    return;
                }

                lock (_audioBytes)
                {
                    _audioBytes.AddRange(buffer.AsSpan(0, read).ToArray());
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _control.Stop();
        _audio.Stop();

        foreach (var task in new[] { _controlTask, _audioTask })
        {
            if (task is null)
            {
                continue;
            }

            try
            {
                await task;
            }
            catch (OperationCanceledException)
            {
            }
        }

        _writeGate.Dispose();
        _cts.Dispose();
    }
}
