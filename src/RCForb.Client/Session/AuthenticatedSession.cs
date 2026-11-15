using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using RCForb.Client.Audio;
using RCForb.Client.State;
using RCForb.Client.V07;
using RCForb.Protocol.V07;

namespace RCForb.Client.Session;

/// <summary>Where an authenticated session connects, with credentials.</summary>
public sealed record AuthenticatedTarget(string Host, int ControlPort, int AudioPort, string User, string PasswordMd5Hex)
{
    public const int DefaultControlPort = 4525;
    public const int DefaultAudioPort = 4524;

    public static AuthenticatedTarget FromHost(string host, string user, string passwordMd5Hex, int controlPort = DefaultControlPort, int audioPort = DefaultAudioPort) =>
        new(host, controlPort, audioPort, user, passwordMd5Hex);

    public string Display => $"{User}@{Host}:{ControlPort} (audio {AudioPort})";

    public GuestTarget ToGuestTarget() => new(Host, ControlPort, AudioPort);
}

public sealed class AuthenticatedSessionOptions
{
    public required AuthenticatedTarget Target { get; init; }

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

/// <summary>
/// Session authentication state.
/// </summary>
public enum AuthState
{
    /// <summary>Not yet attempted.</summary>
    Pending,
    /// <summary>Login sent, awaiting server response.</summary>
    Authenticating,
    /// <summary>Server accepted credentials.</summary>
    Authorised,
    /// <summary>Server refused credentials (wrong password, unknown user, etc.).</summary>
    Refused,
    /// <summary>Session is guest (no credentials provided).</summary>
    Guest
}

/// <summary>
/// Result of a chat send attempt.
/// </summary>
public sealed record ChatSendResult(bool Sent, string? Error);

/// <summary>
/// Result of a channel-control write (frequency, band, mode, filter).
/// The only confirmation is the server's echo; <c>Sent</c> means the command reached the wire.
/// </summary>
public sealed record ChannelWriteResult(bool Sent, string? Error);

/// <summary>
/// Result of a (re-)login attempt. <c>Sent</c> means the login line and, where possible, the
/// session packet reached their sockets; the server's verdict still arrives asynchronously.
/// </summary>
public sealed record LoginResult(bool Sent, string? Error);

/// <summary>
/// An authenticated session: login + session packet + chat send capability.
/// Audio remains receive-only (no TX path).
/// </summary>
/// <remarks>
/// This class extends the guest handshake with:
/// - login command after state sync (or 15s timeout)
/// - 64-byte session packet on audio socket after login
/// - chat send via post::chat::
/// The session still never sends TX, tune, memory, or peripheral commands.
/// </remarks>
public sealed class AuthenticatedSession
{
    private readonly AuthenticatedSessionOptions _options;
    private readonly GuestStats _stats = new();
    private readonly object _sendGate = new();
    private readonly object _chatGate = new();

    private V07ControlChannel? _control;
    private V07AudioChannel? _audio;
    private CancellationTokenSource? _linkedCts;

    private bool _readyRaised;
    private bool _syncTimedOut;
    private string? _closedNotice;
    private string? _lastNoticeKind;
    private string? _lastNoticeText;
    private string? _failure;
    private AuthState _authState = AuthState.Pending;
    private string? _authMessage;
    private bool _loginSent;

    public AuthenticatedSession(AuthenticatedSessionOptions options) => _options = options;

    public RadioStateStore State { get; } = new();
    public GuestStats Stats => _stats;
    public AuthState AuthState => _authState;
    public string? AuthMessage => _authMessage;

    /// <summary>Every non-empty control line, before interpretation.</summary>
    public event Action<string>? ControlLine;

    /// <summary>Fired once, when the server's readiness marker latches.</summary>
    public event Action<RadioStateStore>? Ready;

    /// <summary>Fired for each chat line, already percent-decoded.</summary>
    public event Action<ChatMessage>? ChatReceived;

    /// <summary>Server text that is not state: information, warnings, errors, auth.</summary>
    public event Action<string, string>? Notice;

    /// <summary>Fired when authentication state changes.</summary>
    public event Action<AuthState, string?>? AuthStateChanged;

    public async Task<AuthenticatedSessionReport> RunAsync(TimeSpan duration, CancellationToken cancellationToken)
    {
        var started = DateTime.UtcNow;
        var cancelled = false;

        _linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _linkedCts.Token;

        await using var control = new V07ControlChannel();
        await using var audio = new V07AudioChannel();

        _control = control;
        _audio = audio;

        Task<string?>? controlPump = null;
        Task<string?>? audioPump = null;
        Task? keepalive = null;
        Task? syncWatch = null;

        try
        {
            await control.ConnectAsync(_options.Target.Host, _options.Target.ControlPort, token).ConfigureAwait(false);
            await control.SendAsync("get id", token).ConfigureAwait(false);

            await audio.ConnectAsync(_options.Target.Host, _options.Target.AudioPort, token).ConfigureAwait(false);

            // Sent once, after both sockets are up.
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
            _linkedCts.Cancel();

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

            _control = null;
            _audio = null;
            _linkedCts.Dispose();
            _linkedCts = null;
        }

        return new AuthenticatedSessionReport(
            State.IsReady,
            _syncTimedOut,
            cancelled,
            _failure,
            DateTime.UtcNow - started,
            _stats.ControlLines,
            _stats.AudioPackets,
            _stats.DecodedSamples,
            _stats.DecodeErrors,
            control.SentCommands,
            _authState,
            _authMessage);
    }

    /// <summary>
    /// Sends a chat message to the server. Only available after authentication succeeds.
    /// Returns immediately; the server echoes via chat:: if accepted.
    /// </summary>
    public async Task<ChatSendResult> SendChatAsync(string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new ChatSendResult(false, "Message cannot be empty.");
        }

        if (text.Contains("::", StringComparison.Ordinal))
        {
            return new ChatSendResult(false, "Message cannot contain '::' (protocol delimiter).");
        }

        if (text.Contains('%', StringComparison.Ordinal))
        {
            return new ChatSendResult(false, "Message cannot contain '%' (would be mis-decoded by other clients).");
        }

        if (text.StartsWith("/", StringComparison.Ordinal))
        {
            return new ChatSendResult(false, "Messages starting with '/' are console commands; use the console if intended.");
        }

        lock (_chatGate)
        {
            if (_authState != AuthState.Authorised)
            {
                return new ChatSendResult(false, $"Not authorised (state: {_authState}).");
            }
        }

        var control = _control;
        var cts = _linkedCts;
        if (control is null || cts is null)
        {
            return new ChatSendResult(false, "Session not running.");
        }

        try
        {
            // Send raw text via post::chat:: (NOT percent-encoded per SPEC-CMD-037)
            await control.SendAsync($"post::chat::{text}", cts.Token).ConfigureAwait(false);
            return new ChatSendResult(true, null);
        }
        catch (OperationCanceledException)
        {
            return new ChatSendResult(false, "Session cancelled.");
        }
        catch (Exception ex)
        {
            return new ChatSendResult(false, ex.Message);
        }
    }

    /// <summary>
    /// Sends a raw control command. For advanced use only — prefer SendChatAsync.
    /// </summary>
    public async Task<bool> SendCommandAsync(string command, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return false;
        }

        var control = _control;
        var cts = _linkedCts;
        if (control is null || cts is null)
        {
            return false;
        }

        try
        {
            await control.SendAsync(command, cts.Token).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    // Region: channel control (SPEC rows 23, 24, 25, 27; SPEC-CMD-052)
    //
    // On the V7 transport the client MUST emit post::<verb> for radio verbs (SPEC-CMD-021);
    // composing the post:: form directly is therefore the normalised wire form. None of these
    // touch the PTT path (row 26 / radio::button::TX) — audio stays receive-only.

    /// <summary>
    /// Sets VFO A frequency in Hz (row 23). The server's echo, not this call, is the
    /// confirmation; a refusal surfaces through the <see cref="Notice"/> event.
    /// </summary>
    public async Task<ChannelWriteResult> SetFrequencyAsync(long hz, CancellationToken cancellationToken = default) =>
        await SendChannelAsync($"post::frequency::{hz}", hz >= 1 && hz <= 10_000_000_000,
            "Frequency must be within 1 Hz – 10 GHz (SPEC-STATE-016).", cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Sets VFO B frequency in Hz (row 24). Lowercase <c>b</c> outbound per SPEC-CMD-006;
    /// the inbound echo arrives as <c>frequencyB</c>.
    /// </summary>
    public async Task<ChannelWriteResult> SetFrequencyBAsync(long hz, CancellationToken cancellationToken = default) =>
        await SendChannelAsync($"post::frequencyb::{hz}", hz >= 1 && hz <= 10_000_000_000,
            "Frequency must be within 1 Hz – 10 GHz (SPEC-STATE-016).", cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Selects a band by label (row 25). Selection is by the label the server advertised,
    /// not by number.
    /// </summary>
    public async Task<ChannelWriteResult> SetBandAsync(string band, CancellationToken cancellationToken = default)
    {
        if (!TryOptionText(band, out var text))
        {
            return new ChannelWriteResult(false, "Band must be a non-empty label (no '::' or line breaks).");
        }

        return await SendChannelAsync($"post::band::{text}", true, null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Selects the Mode dropdown option by text (row 27).</summary>
    public Task<ChannelWriteResult> SetModeAsync(string text, CancellationToken cancellationToken = default) =>
        SendDropdownAsync("Mode", text, cancellationToken);

    /// <summary>Selects the Filter dropdown option by text (row 27).</summary>
    public Task<ChannelWriteResult> SetFilterAsync(string text, CancellationToken cancellationToken = default) =>
        SendDropdownAsync("Filter", text, cancellationToken);

    private Task<ChannelWriteResult> SendDropdownAsync(string dropdownName, string optionText, CancellationToken cancellationToken)
    {
        if (!TryOptionText(optionText, out var text))
        {
            return Task.FromResult(new ChannelWriteResult(false,
                "Option must be non-empty text (no '::' or line breaks)."));
        }

        return SendChannelAsync($"post::dropdown::{dropdownName}::{text}", true, null, cancellationToken);
    }

    /// <summary>
    /// Validates user-supplied option/label text for a dropdown or band write.
    ///
    /// DIVERGENCE (SPEC-CMD-007 / SPEC 05 §3.9 override the ABNF field grammar here):
    /// the ABNF says fields are <c>*(VCHAR)</c> (no spaces), but SPEC-CMD-007 requires
    /// whitespace inside a field to be preserved verbatim and SPEC 05 §3.9 explicitly
    /// lists option text such as <c>15 kHz</c>, <c>6 dB</c>. Internal whitespace is
    /// therefore preserved; leading/trailing whitespace would change the option and is
    /// trimmed. Rejects the unrepresentable <c>::</c> separator plus CR/LF/NUL
    /// (SPEC-CMD-008/019).
    /// </summary>
    private static bool TryOptionText(string? value, out string text)
    {
        text = value?.Trim() ?? string.Empty;
        return text.Length > 0
            && !text.Contains("::", StringComparison.Ordinal)
            && text.IndexOfAny(['\r', '\n', '\0']) < 0;
    }

    private async Task<ChannelWriteResult> SendChannelAsync(
        string command,
        bool argumentValid,
        string? invalidError,
        CancellationToken cancellationToken)
    {
        if (!argumentValid)
        {
            return new ChannelWriteResult(false, invalidError);
        }

        var control = _control;
        var cts = _linkedCts;
        if (control is null || cts is null)
        {
            return new ChannelWriteResult(false, "Session not running.");
        }

        try
        {
            await control.SendAsync(command, cts.Token).ConfigureAwait(false);
            return new ChannelWriteResult(true, null);
        }
        catch (OperationCanceledException)
        {
            return new ChannelWriteResult(false, "Session cancelled.");
        }
        catch (Exception ex)
        {
            return new ChannelWriteResult(false, ex.Message);
        }
    }

    // End region: channel control

    /// <summary>
    /// Tries a different (user, password) pair on the live sockets without reconnecting:
    /// re-sends <c>login <user> <md5></c> and the 64-byte session packet. The previous
    /// verdict is discarded; state moves to <see cref="AuthState.Authenticating"/> until the
    /// server answers. Use this to undo a refused login or to switch session options.
    /// </summary>
    public async Task<LoginResult> ReloginAsync(string user, string passwordMd5Hex, CancellationToken cancellationToken = default)
    {
        user = user?.Trim() ?? string.Empty;
        if (user.Length == 0 || user.Any(char.IsWhiteSpace) || user.Contains("::", StringComparison.Ordinal))
        {
            return new LoginResult(false, "User must be a single non-empty token without spaces or '::'.");
        }

        var md5 = (passwordMd5Hex ?? string.Empty).Trim().ToLowerInvariant();
        if (md5.Length != 32 || !md5.All(Uri.IsHexDigit))
        {
            return new LoginResult(false, "Password must be a 32-character lowercase-hex MD5 digest.");
        }

        var control = _control;
        var cts = _linkedCts;
        if (control is null || cts is null)
        {
            return new LoginResult(false, "Session not running.");
        }

        lock (_chatGate)
        {
            _authState = AuthState.Authenticating;
            _authMessage = null;
            // The auto-login gate is permanently satisfied from here: we are driving the
            // attempt, and its credentials may already be refused.
            _loginSent = true;
        }
        AuthStateChanged?.Invoke(AuthState.Authenticating, null);

        try
        {
            await control.SendAsync($"login {user} {md5}", cts.Token).ConfigureAwait(false);

            // Re-bind the audio socket to the new session (best effort, as on first login).
            var audio = _audio;
            if (audio is not null)
            {
                var packet = V07SessionPacket.Build(user, md5);
                try
                {
                    await audio.SendSessionPacketAsync(packet, cts.Token).ConfigureAwait(false);
                }
                catch
                {
                    // If the packet fails, auth may still succeed but audio won't be bound.
                }
            }

            return new LoginResult(true, null);
        }
        catch (OperationCanceledException)
        {
            return new LoginResult(false, "Session cancelled.");
        }
        catch (Exception ex)
        {
            return new LoginResult(false, ex.Message);
        }
    }

    /// <summary>
    /// Reverts to a guest posture on the live sockets: the local auth state is reset to
    /// <see cref="AuthState.Pending"/>, the verdict text is cleared, and chat is disabled
    /// again. Sockets stay open and the server may still hold the login; this is the client
    /// side of "undo the auth attempt" and always precedes <see cref="ReloginAsync"/>.
    /// </summary>
    public void RevertToGuest()
    {
        lock (_chatGate)
        {
            if (_authState == AuthState.Pending)
            {
                return;
            }

            _authState = AuthState.Pending;
            _authMessage = null;
            // Prevent the queued auto-login from following the revert with the original
            // credentials, which the user just asked to step away from.
            _loginSent = true;
        }

        AuthStateChanged?.Invoke(AuthState.Pending, null);
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
                    if (DateTime.UtcNow - lastTraffic > _options.ControlDeadline)
                    {
                        return "only empty lines arrived before the liveness deadline";
                    }

                    continue;
                }

                lastTraffic = DateTime.UtcNow;
                ApplyLine(line, control, token);
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

    private void ApplyLine(string line, V07ControlChannel control, CancellationToken token)
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

        // Handle auth-related messages
        if (line.StartsWith("post::auth::", StringComparison.Ordinal) ||
            line.StartsWith("server::auth::", StringComparison.Ordinal))
        {
            var prefixEnd = line.IndexOf("::", StringComparison.Ordinal);
            if (prefixEnd >= 0)
            {
                var secondPrefixEnd = line.IndexOf("::", prefixEnd + 2, StringComparison.Ordinal);
                if (secondPrefixEnd >= 0)
                {
                    var text = ProtocolText.Decode(line[(secondPrefixEnd + 2)..]);
                    HandleAuthMessage(text);
                }
            }
        }

        if (ClassifyNotice(line) is { } notice)
        {
            // De-dupe identical consecutive notices (servers often repeat them).
            if (notice.Kind == _lastNoticeKind && notice.Text == _lastNoticeText)
            {
                return;
            }
            _lastNoticeKind = notice.Kind;
            _lastNoticeText = notice.Text;

            if (notice.Kind == "warning" && notice.Text.StartsWith("Remote restricted", StringComparison.Ordinal))
            {
                if (notice.Text == _closedNotice)
                {
                    return;
                }
                _closedNotice = notice.Text;
            }
            Notice?.Invoke(notice.Kind, notice.Text);
        }

        // Implicit auth success: server grants radio access without explicit auth message
        // SPEC: some servers signal auth success via radio-open rather than post::auth::
        if ((_authState == AuthState.Authenticating || _authState == AuthState.Pending) &&
            line.StartsWith("post::radio-open::", StringComparison.Ordinal))
        {
            lock (_chatGate)
            {
                _authState = AuthState.Authorised;
                _authMessage = line["post::radio-open::".Length..].Trim();
            }
            AuthStateChanged?.Invoke(_authState, _authMessage);
        }

        // Send login after readiness or sync timeout, once
        if (!_loginSent && (_readyRaised || _syncTimedOut))
        {
            _loginSent = true;
            _ = Task.Run(async () =>
            {
                try
                {
                    await SendLoginAndSessionPacketAsync(control, token).ConfigureAwait(false);
                }
                catch
                {
                    // Fire and forget; errors will surface via auth messages
                }
            }, CancellationToken.None);
        }
    }

    private async Task SendLoginAndSessionPacketAsync(V07ControlChannel control, CancellationToken token)
    {
        var target = _options.Target;
        var loginCmd = $"login {target.User} {target.PasswordMd5Hex}";

        lock (_chatGate)
        {
            _authState = AuthState.Authenticating;
            _authMessage = null;
        }
        AuthStateChanged?.Invoke(_authState, null);

        await control.SendAsync(loginCmd, token).ConfigureAwait(false);

        // Build and send the 64-byte session packet on the audio socket
        // SPEC 02 §4.6, SPEC-SESS-007: session packet binds audio to control session
        var sessionPacket = V07SessionPacket.Build(target.User, target.PasswordMd5Hex);
        
        var audio = _audio;
        if (audio is not null)
        {
            try
            {
                await audio.SendSessionPacketAsync(sessionPacket, token).ConfigureAwait(false);
            }
            catch
            {
                // If session packet fails, auth may still succeed but audio won't be bound
            }
        }
    }

    private void HandleAuthMessage(string text)
    {
        lock (_chatGate)
        {
            _authMessage = text;

            // Check for refusal keywords (covers a wide range of server wordings).
            var lower = text.ToLowerInvariant();
            bool refused = lower.Contains("login") || lower.Contains("password") || lower.Contains("unauthori") ||
                lower.Contains("invalid") || lower.Contains("denied") || lower.Contains("refused") ||
                lower.Contains("wrong") || lower.Contains("unknown user") || lower.Contains("not authori") ||
                lower.Contains("fail") || lower.Contains("reject") || lower.Contains("forbidden") ||
                lower.Contains("no access") || lower.Contains("not allowed") || lower.Contains("disabled");

            bool success = lower.Contains("welcome") || lower.Contains("authorised") || lower.Contains("authorized") ||
                lower.Contains("granted") || lower.Contains("logged in") || lower.Contains("success") ||
                lower.Contains("accepted") || lower.Contains("ok") || lower.Contains("connected");

            if (refused && !success)
            {
                if (_authState == AuthState.Authenticating || _authState == AuthState.Pending)
                {
                    _authState = AuthState.Refused;
                    AuthStateChanged?.Invoke(_authState, text);
                }
            }
            else
            {
                // Lenient: any non-refusal auth dialog while authenticating is treated as success,
                // because remotes vary widely in their success wording (SPEC-CMD-065).
                if (_authState == AuthState.Authenticating)
                {
                    _authState = AuthState.Authorised;
                    AuthStateChanged?.Invoke(_authState, text);
                }
            }
        }
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

    private static (string Kind, string Text)? ClassifyNotice(string line)
    {
        const string closed = "post::radio-closed";
        if (line.StartsWith(closed, StringComparison.Ordinal))
        {
            var reason = line.Length > closed.Length + 2
                ? ProtocolText.Decode(line[(closed.Length + 2)..]).Trim()
                : "";
            return ("warning", reason.Length == 0 ? "Remote restricted." : $"Remote restricted: {reason}");
        }

        // Row 81: another user already holds the radio. A session restriction, not an auth
        // failure — surface it so the user can wait, leave, or try a different account.
        const string inUse = "post::radio-in-use::";
        if (line.StartsWith(inUse, StringComparison.Ordinal))
        {
            var holder = ProtocolText.Decode(line[inUse.Length..]).Trim();
            return ("restriction", holder.Length == 0
                ? "The radio is already in use by another user."
                : $"The radio is already in use by {holder}.");
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
                return (kind, ProtocolText.Decode(line[prefix.Length..]));
            }
        }

        return null;
    }
}

public sealed record AuthenticatedSessionReport(
    bool Ready,
    bool SyncTimedOut,
    bool Cancelled,
    string? Failure,
    TimeSpan Duration,
    long ControlLines,
    long AudioPackets,
    long DecodedSamples,
    long DecodeErrors,
    IReadOnlyList<string> SentCommands,
    AuthState AuthState,
    string? AuthMessage)
{
    public TimeSpan AudioDuration => TimeSpan.FromSeconds((double)DecodedSamples / SpeexPcmDecoder.SampleRate);
}