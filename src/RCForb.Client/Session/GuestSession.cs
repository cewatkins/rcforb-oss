using System.Runtime.InteropServices;
using RCForb.Client.Audio;
using RCForb.Client.State;
using RCForb.Client.V07;
using RCForb.Protocol.V07;

namespace RCForb.Client.Session;

/// <summary>Where a guest session connects. No credentials exist in this type by design.</summary>
public sealed record GuestTarget(string Host, int ControlPort, int AudioPort)
{
    public const int DefaultControlPort = 4525;
    public const int DefaultAudioPort = 4524;

    public static GuestTarget FromHost(string host, int controlPort = DefaultControlPort, int audioPort = DefaultAudioPort) =>
        new(host, controlPort, audioPort);

    public string Display => $"{Host}:{ControlPort} (audio {AudioPort})";
}

public sealed class GuestSessionOptions
{
    public required GuestTarget Target { get; init; }

    /// <summary>
    /// Where decoded receive audio goes: a recorder, a player, both, or nothing.
    /// The session writes to it and never disposes it, so one sink can span reconnects.
    /// </summary>
    public IGuestAudioSink? AudioSink { get; init; }

    /// <summary>How long to wait for the snapshot marker before reporting a degraded session.</summary>
    public TimeSpan SynchronisationTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>v0.7 control liveness deadline (SPEC-TRAN-052).</summary>
    public TimeSpan ControlDeadline { get; init; } = TimeSpan.FromSeconds(15);
}

/// <summary>Counters shared between the pumps and the console.</summary>
public sealed class GuestStats
{
    private long _controlLines;
    private long _audioPackets;
    private long _audioPayloadBytes;
    private long _decodedSamples;
    private long _decodeErrors;
    private long _chatMessages;
    private long _framesInLastPacket;
    private long _rmsBits;

    public long ControlLines => Interlocked.Read(ref _controlLines);
    public long AudioPackets => Interlocked.Read(ref _audioPackets);
    public long AudioPayloadBytes => Interlocked.Read(ref _audioPayloadBytes);
    public long DecodedSamples => Interlocked.Read(ref _decodedSamples);
    public long DecodeErrors => Interlocked.Read(ref _decodeErrors);
    public long ChatMessages => Interlocked.Read(ref _chatMessages);
    public long FramesInLastPacket => Interlocked.Read(ref _framesInLastPacket);

    /// <summary>RMS of the most recent decoded packet, normalised to 0..1.</summary>
    public double LastRms => BitConverter.Int64BitsToDouble(Interlocked.Read(ref _rmsBits));

    public TimeSpan AudioDuration => TimeSpan.FromSeconds((double)DecodedSamples / SpeexPcmDecoder.SampleRate);

    internal void AddControlLine() => Interlocked.Increment(ref _controlLines);

    internal void AddChat() => Interlocked.Increment(ref _chatMessages);

    internal void AddDecodeError() => Interlocked.Increment(ref _decodeErrors);

    internal void AddAudio(int payloadBytes, int frames, double rms)
    {
        Interlocked.Increment(ref _audioPackets);
        Interlocked.Add(ref _audioPayloadBytes, payloadBytes);
        Interlocked.Add(ref _decodedSamples, (long)frames * SpeexPcmDecoder.SamplesPerFrame);
        Interlocked.Exchange(ref _framesInLastPacket, frames);
        Interlocked.Exchange(ref _rmsBits, BitConverter.DoubleToInt64Bits(rms));
    }

    internal void ResetLevel()
    {
        Interlocked.Exchange(ref _rmsBits, 0);
        Interlocked.Exchange(ref _framesInLastPacket, 0);
    }
}

public sealed record GuestSessionReport(
    bool Ready,
    bool SyncTimedOut,
    bool Cancelled,
    string? Failure,
    TimeSpan Duration,
    long ControlLines,
    long AudioPackets,
    long DecodedSamples,
    long DecodeErrors,
    IReadOnlyList<string> SentCommands)
{
    public TimeSpan AudioDuration => TimeSpan.FromSeconds((double)DecodedSamples / SpeexPcmDecoder.SampleRate);
}

/// <summary>
/// A receive-only guest link: no login, no transmit request, no audio uplink.
/// </summary>
/// <remarks>
/// The class deliberately has no general "send this command" entry point. The commands a
/// session writes are fixed here — <c>get id</c>, <c>set protocol rcs</c>, the two control
/// keepalives — plus the <c>IMA</c> audio liveness packet. Chat, buttons, requests and
/// login are unrepresentable from the session's surface.
/// </remarks>
public sealed class GuestSession
{
    private readonly GuestSessionOptions _options;
    private readonly GuestStats _stats = new();

    private bool _readyRaised;
    private bool _syncTimedOut;
    private string? _closedNotice;
    private string? _failure;

    public GuestSession(GuestSessionOptions options) => _options = options;

    public RadioStateStore State { get; } = new();
    public GuestStats Stats => _stats;

    /// <summary>Every non-empty control line, before interpretation.</summary>
    public event Action<string>? ControlLine;

    /// <summary>Fired once, when the server's readiness marker latches.</summary>
    public event Action<RadioStateStore>? Ready;

    /// <summary>Fired for each chat line, already percent-decoded.</summary>
    public event Action<ChatMessage>? ChatReceived;

    /// <summary>Server text that is not state: information, warnings, errors, auth.</summary>
    public event Action<string, string>? Notice;

    public async Task<GuestSessionReport> RunAsync(TimeSpan duration, CancellationToken cancellationToken)
    {
        var started = DateTime.UtcNow;
        var cancelled = false;

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = linked.Token;

        await using var control = new V07ControlChannel();
        await using var audio = new V07AudioChannel();

        Task<string?>? controlPump = null;
        Task<string?>? audioPump = null;
        Task? keepalive = null;
        Task? syncWatch = null;

        try
        {
            await control.ConnectAsync(_options.Target.Host, _options.Target.ControlPort, token).ConfigureAwait(false);
            await control.SendAsync("get id", token).ConfigureAwait(false);

            await audio.ConnectAsync(_options.Target.Host, _options.Target.AudioPort, token).ConfigureAwait(false);

            // Sent once, after both sockets are up. A second request restarts the server's
            // burst (SPEC-SESS-013/018); readiness is never re-requested.
            await control.SendAsync("set protocol rcs", token).ConfigureAwait(false);

            controlPump = ControlPumpAsync(control, token);
            audioPump = AudioPumpAsync(audio, token);
            keepalive = KeepaliveAsync(control, audio, token);
            syncWatch = SyncWatchAsync(token);
            var window = Task.Delay(duration, token);

            var first = await Task.WhenAny(window, controlPump, audioPump).ConfigureAwait(false);

            if (first == controlPump)
            {
                _failure = await controlPump.ConfigureAwait(false) ?? "control channel ended";
            }
            else if (first == audioPump)
            {
                _failure = await audioPump.ConfigureAwait(false) ?? "audio channel ended";
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            cancelled = true;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or InvalidOperationException)
        {
            _failure = ex.Message;
        }
        finally
        {
            linked.Cancel();

            if (controlPump is not null)
            {
                await Swallow(controlPump).ConfigureAwait(false);
            }

            if (audioPump is not null)
            {
                await Swallow(audioPump).ConfigureAwait(false);
            }

            if (keepalive is not null)
            {
                await Swallow(keepalive).ConfigureAwait(false);
            }

            if (syncWatch is not null)
            {
                await Swallow(syncWatch).ConfigureAwait(false);
            }
        }

        return new GuestSessionReport(
            State.IsReady,
            _syncTimedOut,
            cancelled,
            _failure,
            DateTime.UtcNow - started,
            _stats.ControlLines,
            _stats.AudioPackets,
            _stats.DecodedSamples,
            _stats.DecodeErrors,
            control.SentCommands);
    }

    private async Task<string?> ControlPumpAsync(V07ControlChannel control, CancellationToken token)
    {
        var lastTraffic = DateTime.UtcNow;

        try
        {
            while (!token.IsCancellationRequested)
            {
                var (status, line) = await control.ReadLineAsync(_options.ControlDeadline, token).ConfigureAwait(false);

                switch (status)
                {
                    case ControlReadStatus.Timeout:
                        return $"no control traffic for {_options.ControlDeadline.TotalSeconds:0} s (heartbeat lost)";
                    case ControlReadStatus.Closed:
                        return control.Fault ?? "control socket closed";
                }

                if (line.Length == 0)
                {
                    // An empty line is a framing artefact, not traffic; it neither extends the
                    // deadline nor stops it (SPEC 02 §4.2).
                    if (DateTime.UtcNow - lastTraffic > _options.ControlDeadline)
                    {
                        return "only empty lines arrived before the liveness deadline";
                    }

                    continue;
                }

                lastTraffic = DateTime.UtcNow;
                ApplyLine(line);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or ObjectDisposedException)
        {
            return ex.Message;
        }

        return null;
    }

    private void ApplyLine(string line)
    {
        _stats.AddControlLine();
        ControlLine?.Invoke(line);

        State.Apply(line);

        if (!_readyRaised && State.IsReady)
        {
            _readyRaised = true;
            Ready?.Invoke(State);
        }

        if (line.StartsWith("chat::", StringComparison.Ordinal))
        {
            _stats.AddChat();
            if (State.LastChat is { } chat)
            {
                ChatReceived?.Invoke(chat);
            }
        }

        if (ClassifyNotice(line) is { } notice)
        {
            // A server may repeat the same closure reason every few seconds; one copy is the
            // information, the rest is noise in a log the operator is reading.
            if (notice.Kind == "warning" && notice.Text.StartsWith("The remote closed", StringComparison.Ordinal))
            {
                if (notice.Text == _closedNotice)
                {
                    return;
                }

                _closedNotice = notice.Text;
            }

            Notice?.Invoke(notice.Kind, notice.Text);
        }
    }

    private static (string Kind, string Text)? ClassifyNotice(string line)
    {
        // A guest is told *why* the remote closed (SPEC 04 row 82). Without the reason the
        // session just goes quiet, which is indistinguishable from a fault.
        const string closed = "post::radio-closed";
        if (line.StartsWith(closed, StringComparison.Ordinal))
        {
            var reason = line.Length > closed.Length + 2
                ? ProtocolText.Decode(line[(closed.Length + 2)..]).Trim()
                : "";
            return ("warning", reason.Length == 0 ? "The remote closed." : $"The remote closed: {reason}");
        }

        (string Prefix, string Kind)[] prefixes =
        [
            ("post::info::", "info"),
            ("post::auth::", "auth"),
            ("post::error::", "error"),
            ("post::warning::", "warning"),
            ("server::auth::", "auth"),
        ];

        foreach (var (prefix, kind) in prefixes)
        {
            if (line.StartsWith(prefix, StringComparison.Ordinal))
            {
                // Dialog text is percent-encoded on the wire, like chat (SPEC 04).
                return (kind, ProtocolText.Decode(line[prefix.Length..]));
            }
        }

        return null;
    }

    private async Task<string?> AudioPumpAsync(V07AudioChannel audio, CancellationToken token)
    {
        using var decoder = new SpeexPcmDecoder();
        var pcm = new List<short>(SpeexPcmDecoder.SamplesPerFrame * 8);
        var sink = _options.AudioSink;

        try
        {
            while (!token.IsCancellationRequested)
            {
                var frame = await audio.ReadFrameAsync(token).ConfigureAwait(false);
                if (frame is null)
                {
                    return audio.Fault ?? "audio socket closed";
                }

                if (frame.Value.Kind != V07FrameKind.Audio)
                {
                    // Control frames carry the keyed flag; a guest has no transmitter to key,
                    // so the flag is read and ignored. Session packets need credentials.
                    continue;
                }

                pcm.Clear();
                var frames = decoder.Decode(frame.Value.Payload.Span, pcm);
                if (frames == 0)
                {
                    _stats.AddDecodeError();
                    continue;
                }

                sink?.Write(CollectionsMarshal.AsSpan(pcm));
                _stats.AddAudio(frame.Value.Length, frames, Rms(pcm));
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or ObjectDisposedException)
        {
            return ex.Message;
        }

        return null;
    }

    private static double Rms(List<short> pcm)
    {
        if (pcm.Count == 0)
        {
            return 0;
        }

        double sum = 0;
        foreach (var sample in pcm)
        {
            sum += (double)sample * sample;
        }

        return Math.Sqrt(sum / pcm.Count) / short.MaxValue;
    }

    private async Task KeepaliveAsync(V07ControlChannel control, V07AudioChannel audio, CancellationToken token)
    {
        using var controlTimer = new PeriodicTimer(TimeSpan.FromSeconds(4));
        using var audioTimer = new PeriodicTimer(TimeSpan.FromSeconds(2));

        var controlLoop = Task.Run(async () =>
        {
            try
            {
                while (await controlTimer.WaitForNextTickAsync(token).ConfigureAwait(false))
                {
                    await control.SendAsync($"post::heartbeat::{DateTime.Now.Ticks}", token).ConfigureAwait(false);
                    await control.SendAsync("post::check::cantune", token).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or System.Net.Sockets.SocketException or ObjectDisposedException)
            {
            }
        }, CancellationToken.None);

        var audioLoop = Task.Run(async () =>
        {
            try
            {
                while (await audioTimer.WaitForNextTickAsync(token).ConfigureAwait(false))
                {
                    await audio.SendAliveAsync(token).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or System.Net.Sockets.SocketException or ObjectDisposedException)
            {
            }
        }, CancellationToken.None);

        await Task.WhenAll(controlLoop, audioLoop).ConfigureAwait(false);
    }

    private async Task SyncWatchAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(_options.SynchronisationTimeout, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (!State.IsReady)
        {
            _syncTimedOut = true;
            Notice?.Invoke("sync", "no state marker within the synchronisation window; continuing unverified");
        }
    }

    private static async Task Swallow(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }
}
