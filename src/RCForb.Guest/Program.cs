using System.Security.Cryptography;
using System.Text;
using RCForb.Audio;
using RCForb.Client.Audio;
using RCForb.Client.Directory;
using RCForb.Client.Session;
using RCForb.Client.State;

namespace RCForb.Guest;

/// <summary>
/// A listen-only guest client for advertised RemoteHams channels.
///
/// It connects the way the shipping client does when no credentials are configured: the
/// v0.7 control and audio sockets, <c>get id</c>, <c>set protocol rcs</c>, and then nothing
/// but liveness. No login line is written, no transmit request is made, and the audio
/// connection has no uplink API at all.
///
/// With <c>--user</c> and <c>--password-md5</c> (or <c>--password</c>), it performs an
/// authenticated login and sends the session packet on the audio socket, enabling chat send
/// via the interactive console. Audio remains receive-only (no TX path).
/// </summary>
internal static class Program
{
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(1);

    private const string Usage = """
        rcforb-guest — listen to an advertised RemoteHams channel, receive-only.
        With --user/--password, performs authenticated login (chat send enabled).

        Usage:
          rcforb-guest --list [filter]                     list advertised remotes
          rcforb-guest --join <index|name> [options]       join one as a guest
          rcforb-guest --host <host[:port][/audioPort]> [options]
          rcforb-guest --list-devices                      list audio output devices

        Options:
          --seconds <n>          total listening time in seconds (default 30)
          --record <file.wav>    also write the decoded audio (8 kHz mono WAV)
          --reconnect            reconnect when the server drops the link, until --seconds
                                 is used up (default: stop on the first link end)
          --no-playback          do not open an audio output device
          --audio-device <name>  output device to use, matched by substring
                                 (default: the system default device)
          --buffer <ms>          playout target depth, 100-1000 ms (default 300); raise it
                                 on links that deliver audio in bursts
          --verbose              echo every control line
          --audio-port <n>       audio port for --host (default 4524)
          --user <name>          username for authenticated login (enables chat send)
          --password-md5 <hex>   32-char lowercase hex MD5 of password (for --user)
          --password <text>      plaintext password; MD5 is computed locally (for --user)
          --help                 show this text

        Decoded audio is played through the default output device; --record keeps a copy.
        A guest sends no credentials and cannot transmit. Servers whose CanListen policy is
        not "public" may still refuse or restrict the session; the server's own text is shown
        verbatim.
        """;

    private static async Task<int> Main(string[] args)
    {
        Options options;
        try
        {
            options = Options.Parse(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            Console.Error.WriteLine();
            Console.Error.WriteLine(Usage);
            return 2;
        }

        if (options.Help)
        {
            Console.WriteLine(Usage);
            return 0;
        }

        if (options.ListDevicesOnly)
        {
            var devices = SoundFlowAudioSink.ListDevices();
            if (devices.Count == 0)
            {
                Console.WriteLine("no playback devices found (or the audio backend could not be loaded)");
                return 1;
            }

            foreach (var device in devices)
            {
                Console.WriteLine(device);
            }

            return 0;
        }

        if (options.ListOnly)
        {
            return await ListAsync(options).ConfigureAwait(false);
        }

        // Validate auth options
        if ((options.User is null) != (options.PasswordMd5Hex is null && options.PasswordPlaintext is null))
        {
            Console.Error.WriteLine("error: --user requires --password-md5 or --password (and vice versa)");
            return 2;
        }

        RemoteRecord? record = null;
        GuestTarget guestTarget;

        if (options.JoinQuery is { Length: > 0 } query)
        {
            RemoteDirectory directory;
            try
            {
                directory = await RemoteDirectory.FetchAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Xml.XmlException)
            {
                Console.Error.WriteLine($"error: could not read the remote list: {ex.Message}");
                return 1;
            }

            try
            {
                record = directory.Find(query);
            }
            catch (ArgumentException ex)
            {
                Console.Error.WriteLine($"error: {ex.Message}");
                return 2;
            }

            if (record.Transport == TransportGeneration.V09)
            {
                Console.Error.WriteLine(
                    $"error: '{record.ServerName}' advertises no audio port, which selects the single-socket " +
                    "v0.9 path. The guest client implements the dual-TCP v0.7 path only.");
                return 2;
            }

            if (!record.Online)
            {
                Console.Error.WriteLine($"error: '{record.ServerName}' is listed as offline.");
                return 2;
            }

            guestTarget = new GuestTarget(record.Domain, record.ControlPort, record.AudioPort);
        }
        else
        {
            guestTarget = options.Target!;
        }

        // Build authenticated target if credentials provided
        AuthenticatedTarget? authTarget = null;
        if (options.User is not null)
        {
            var md5Hex = options.PasswordMd5Hex ?? ComputeMd5Hex(options.PasswordPlaintext!);
            authTarget = new AuthenticatedTarget(
                guestTarget.Host,
                guestTarget.ControlPort,
                guestTarget.AudioPort,
                options.User,
                md5Hex);
        }

        return await RunAsync(options, record, guestTarget, authTarget).ConfigureAwait(false);
    }

    private static string ComputeMd5Hex(string plaintext)
    {
        using var md5 = MD5.Create();
        var hash = md5.ComputeHash(Encoding.UTF8.GetBytes(plaintext));
        var sb = new StringBuilder(32);
        foreach (var b in hash)
        {
            sb.Append(b.ToString("x2"));
        }
        return sb.ToString();
    }

    private static async Task<int> ListAsync(Options options)
    {
        RemoteDirectory directory;
        try
        {
            directory = await RemoteDirectory.FetchAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Xml.XmlException)
        {
            Console.Error.WriteLine($"error: could not read the remote list: {ex.Message}");
            return 1;
        }

        var entries = directory.Entries
            .Select((remote, index) => (Remote: remote, Index: index + 1))
            .Where(item => options.Filter is null ||
                           item.Remote.ServerName.Contains(options.Filter, StringComparison.OrdinalIgnoreCase) ||
                           item.Remote.RadioName.Contains(options.Filter, StringComparison.OrdinalIgnoreCase) ||
                           item.Remote.Country.Contains(options.Filter, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Console.WriteLine($"{"#",4}  {"server",-28} {"radio",-16} {"listen",-7} {"tx",-2} {"proto",-5} endpoint");
        foreach (var (remote, index) in entries)
        {
            Console.WriteLine(
                $"{index,4}  {Clip(remote.ServerName, 28),-28} {Clip(remote.RadioName, 16),-16} " +
                $"{Clip(remote.ListenPolicy, 7),-7} {(remote.CanTransmit ? "yes" : "no"),-2} " +
                $"{(remote.Transport == TransportGeneration.V07 ? "v0.7" : "v0.9"),-5} {remote.Endpoint}");
        }

        Console.WriteLine();
        Console.WriteLine($"{entries.Length} remote(s). Join with --join <index|name>.");
        return 0;
    }

    private static async Task<int> RunAsync(Options options, RemoteRecord? record, GuestTarget guestTarget, AuthenticatedTarget? authTarget)
    {
        var isAuthenticated = authTarget is not null;
        var displayTarget = authTarget?.Display ?? guestTarget.Display;
        var authLabel = isAuthenticated ? "authenticated" : "guest (no login, no transmit)";

        Console.WriteLine(record is null
            ? $"{authLabel} listen — {displayTarget}"
            : $"{authLabel} listen — {record.ServerName} / {record.RadioName} ({record.Country})");
        Console.WriteLine($"  listen policy: {record?.ListenPolicy ?? "unknown"}; {authLabel}");
        Console.WriteLine($"  endpoint:      {displayTarget}");

        WavFileWriter? recorder = null;
        SoundFlowAudioSink? player = null;
        IGuestAudioSink? sink = null;

        try
        {
            if (options.RecordPath is { Length: > 0 } path)
            {
                recorder = new WavFileWriter(path);
                Console.WriteLine($"  recording:     {path}");
            }

            if (!options.NoPlayback)
            {
                player = SoundFlowAudioSink.TryStart(
                    options.AudioDevice,
                    out var playbackError,
                    targetLatency: TimeSpan.FromMilliseconds(options.BufferMs));
                if (player is null)
                {
                    Console.WriteLine($"  playback:      unavailable ({playbackError}); continuing silently");
                }
                else
                {
                    Console.WriteLine(
                        $"  playback:      {player.DeviceName} " +
                        $"({player.SampleRate} Hz mono, {player.Buffer.TargetSamples / (double)player.SampleRate:0.00} s buffer)");
                }
            }

            sink = (recorder, player) switch
            {
                (not null, not null) => new CompositeAudioSink(recorder, player),
                (not null, null) => recorder,
                (null, not null) => player,
                _ => null,
            };

            Console.WriteLine();

            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
            };

            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(options.Seconds);
            var totals = new Totals { Playout = player?.Buffer };
            var holder = new SessionHolder();
            var status = StatusLoopAsync(holder, totals, deadline, player, cts.Token);

            try
            {
                if (isAuthenticated && authTarget is not null)
                {
                    await ListenAuthenticatedAsync(options, authTarget, sink, player, deadline, totals, holder, cts.Token).ConfigureAwait(false);
                }
                else
                {
                    await ListenAsync(options, guestTarget, sink, player, deadline, totals, holder, cts.Token).ConfigureAwait(false);
                }
            }
            finally
            {
                holder.Session = null;
                holder.AuthSession = null;
                cts.Cancel();
                await Swallow(status).ConfigureAwait(false);
            }

            PrintSummary(options, totals);
            return totals.Final is { Failure: null, Ready: true } ? 0 : 1;
        }
        finally
        {
            if (sink is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }

    private static async Task ListenAsync(
        Options options,
        GuestTarget target,
        IGuestAudioSink? sink,
        SoundFlowAudioSink? player,
        DateTime deadline,
        Totals totals,
        SessionHolder holder,
        CancellationToken cancellationToken)
    {
        var attempt = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                return;
            }

            attempt++;
            if (attempt > 1)
            {
                Console.WriteLine($"[reconnect] attempt {attempt}, {remaining.TotalSeconds:0} s left");
            }

            player?.Buffer.Reset();

            var session = new GuestSession(new GuestSessionOptions
            {
                Target = target,
                AudioSink = sink,
            });

            Wire(session, options);
            holder.Session = session;

            var report = await session.RunAsync(remaining, cancellationToken).ConfigureAwait(false);
            totals.Add(report);

            if (report.Cancelled || report.Failure is null || !options.Reconnect)
            {
                return;
            }

            if (DateTime.UtcNow + ReconnectDelay >= deadline)
            {
                return;
            }

            Console.WriteLine($"[reconnect] link ended: {report.Failure}");

            try
            {
                await Task.Delay(ReconnectDelay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private static async Task ListenAuthenticatedAsync(
        Options options,
        AuthenticatedTarget target,
        IGuestAudioSink? sink,
        SoundFlowAudioSink? player,
        DateTime deadline,
        Totals totals,
        SessionHolder holder,
        CancellationToken cancellationToken)
    {
        var attempt = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                return;
            }

            attempt++;
            if (attempt > 1)
            {
                Console.WriteLine($"[reconnect] attempt {attempt}, {remaining.TotalSeconds:0} s left");
            }

            player?.Buffer.Reset();

            var session = new AuthenticatedSession(new AuthenticatedSessionOptions
            {
                Target = target,
                AudioSink = sink,
            });

            Wire(session, options);
            holder.AuthSession = session;

            var report = await session.RunAsync(remaining, cancellationToken).ConfigureAwait(false);
            totals.Add(report);

            if (report.Cancelled || report.Failure is null || !options.Reconnect)
            {
                return;
            }

            if (DateTime.UtcNow + ReconnectDelay >= deadline)
            {
                return;
            }

            Console.WriteLine($"[reconnect] link ended: {report.Failure}");

            try
            {
                await Task.Delay(ReconnectDelay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private static void Wire(GuestSession session, Options options)
    {
        session.ControlLine += line =>
        {
            if (options.Verbose)
            {
                Console.WriteLine($"<< {line}");
            }
        };

        session.Ready += state =>
        {
            Console.WriteLine("[state] snapshot latched");
            foreach (var line in state.Summary())
            {
                Console.WriteLine($"        {line}");
            }

            Console.WriteLine();
        };

        session.ChatReceived += chat =>
        {
            Console.WriteLine($"[chat] {chat.Sender}: {Clip(chat.Text, 180)}");
        };

        session.Notice += (kind, text) =>
        {
            if (kind is "info" or "warning" or "error" or "auth" or "sync")
            {
                Console.WriteLine($"[{kind}] {Clip(text, 200)}");
            }
        };
    }

    private static void Wire(AuthenticatedSession session, Options options)
    {
        session.ControlLine += line =>
        {
            if (options.Verbose)
            {
                Console.WriteLine($"<< {line}");
            }
        };

        session.Ready += state =>
        {
            Console.WriteLine("[state] snapshot latched");
            foreach (var line in state.Summary())
            {
                Console.WriteLine($"        {line}");
            }

            Console.WriteLine();
        };

        session.ChatReceived += chat =>
        {
            Console.WriteLine($"[chat] {chat.Sender}: {Clip(chat.Text, 180)}");
        };

        session.Notice += (kind, text) =>
        {
            if (kind is "info" or "warning" or "error" or "auth" or "sync")
            {
                Console.WriteLine($"[{kind}] {Clip(text, 200)}");
            }
        };

        session.AuthStateChanged += (state, message) =>
        {
            var msg = message is null ? "" : $": {message}";
            Console.WriteLine($"[auth] {state}{msg}");
        };
    }

    private static void PrintSummary(Options options, Totals totals)
    {
        Console.WriteLine();
        Console.WriteLine($"  attempts:  {totals.Attempts}" +
                          (totals.LinkEnds > 0 ? $" ({totals.LinkEnds} link end(s))" : string.Empty));
        Console.WriteLine($"  duration:  {totals.WallClock.TotalSeconds:0.0} s wall clock, " +
                          $"{totals.Connected.TotalSeconds:0.0} s connected");
        Console.WriteLine($"  state:     {(totals.Final?.Ready == true ? "ready" : totals.SyncTimedOut ? "unverified (no marker)" : "not ready")}");
        Console.WriteLine($"  control:   {totals.ControlLines} line(s)");
        Console.WriteLine($"  audio:     {totals.AudioPackets} packet(s), {totals.AudioDuration.TotalSeconds:0.0} s decoded, " +
                          $"{totals.DecodeErrors} decode error(s)");
        Console.WriteLine($"  sent:      {string.Join(", ", totals.SentCommands.Distinct())}");

        if (totals.Playout is { } playout)
        {
            Console.WriteLine(
                $"  playout:   {playout.DepthSeconds:0.00} s buffered, {playout.DroppedSamples} discarded on overflow, " +
                $"{playout.DecimatedSamples} skipped by the drain, {playout.Underruns} underrun(s)");
        }

        if (totals.Final?.Failure is { } failure)
        {
            Console.WriteLine($"  ended:     {failure}");
        }

        // Authenticated session report fields
        if (totals.AuthFinal is { } authReport)
        {
            Console.WriteLine($"  auth:      {authReport.AuthState}" +
                              (authReport.AuthMessage is { } m ? $" ({m})" : ""));
        }

        if (options.RecordPath is { Length: > 0 } && totals.AudioDuration > TimeSpan.Zero)
        {
            Console.WriteLine($"  wrote:     {options.RecordPath}");
        }
    }

    private static async Task StatusLoopAsync(
        SessionHolder holder,
        Totals totals,
        DateTime deadline,
        SoundFlowAudioSink? player,
        CancellationToken cancellationToken)
    {
        var started = DateTime.UtcNow;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
                var elapsed = (int)(DateTime.UtcNow - started).TotalSeconds;

                GuestStats? stats = null;
                if (holder.Session is not null)
                {
                    stats = holder.Session.Stats;
                }
                else if (holder.AuthSession is not null)
                {
                    stats = holder.AuthSession.Stats;
                }

                if (stats is null || (stats.AudioPackets == 0 && totals.AudioPackets == 0))
                {
                    Console.WriteLine($"[{elapsed,3}s] waiting for audio — {stats?.ControlLines ?? 0} control line(s)");
                    continue;
                }

                var packets = totals.AudioPackets + stats.AudioPackets;
                var decoded = totals.DecodedSamples + stats.DecodedSamples;
                var frames = stats.AudioPackets == 0
                    ? 0
                    : (double)stats.DecodedSamples / SpeexPcmDecoder.SamplesPerFrame / stats.AudioPackets;
                var depth = player is null ? string.Empty : $"  buf {player.Buffer.DepthSeconds:0.00}s";

                Console.WriteLine(
                    $"[{elapsed,3}s] audio {TimeSpan.FromSeconds((double)decoded / SpeexPcmDecoder.SampleRate).TotalSeconds,5:0.0}s  " +
                    $"{packets,4} pkt  {frames:0.0} frames/pkt  {Bar(stats.LastRms)}{depth}  " +
                    $"{Math.Max(0, (deadline - DateTime.UtcNow).TotalSeconds),3:0}s left");
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static string Bar(double rms)
    {
        var level = (int)Math.Clamp(rms * 40, 0, 20);
        return new string('#', level) + new string('.', 20 - level);
    }

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";

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

    private sealed class SessionHolder
    {
        public GuestSession? Session { get; set; }
        public AuthenticatedSession? AuthSession { get; set; }
    }

    /// <summary>Accumulates the reports of every attempt in one run.</summary>
    private sealed class Totals
    {
        public GuestSessionReport? Final { get; private set; }
        public AuthenticatedSessionReport? AuthFinal { get; private set; }
        public int Attempts { get; private set; }
        public int LinkEnds { get; private set; }
        public bool SyncTimedOut { get; private set; }
        public DateTime Started { get; } = DateTime.UtcNow;
        public TimeSpan WallClock => DateTime.UtcNow - Started;
        public TimeSpan Connected { get; private set; }
        public long ControlLines { get; private set; }
        public long AudioPackets { get; private set; }
        public long DecodedSamples { get; private set; }
        public long DecodeErrors { get; private set; }
        public PlayoutBuffer? Playout { get; set; }
        public IReadOnlyList<string> SentCommands { get; private set; } = [];

        public TimeSpan AudioDuration => TimeSpan.FromSeconds((double)DecodedSamples / SpeexPcmDecoder.SampleRate);

        public void Add(GuestSessionReport report)
        {
            Attempts++;
            Final = report;
            AuthFinal = null;
            Connected += report.Duration;
            ControlLines = Math.Max(ControlLines, report.ControlLines);
            AudioPackets += report.AudioPackets;
            DecodedSamples += report.DecodedSamples;
            DecodeErrors += report.DecodeErrors;
            SyncTimedOut |= report.SyncTimedOut;
            SentCommands = report.SentCommands;

            if (report.Failure is not null)
            {
                LinkEnds++;
            }
        }

        public void Add(AuthenticatedSessionReport report)
        {
            Attempts++;
            AuthFinal = report;
            Final = null;
            Connected += report.Duration;
            ControlLines = Math.Max(ControlLines, report.ControlLines);
            AudioPackets += report.AudioPackets;
            DecodedSamples += report.DecodedSamples;
            DecodeErrors += report.DecodeErrors;
            SyncTimedOut |= report.SyncTimedOut;
            SentCommands = report.SentCommands;

            if (report.Failure is not null)
            {
                LinkEnds++;
            }
        }
    }

    private sealed class Options
    {
        public bool Help { get; private set; }
        public bool ListOnly { get; private set; }
        public bool ListDevicesOnly { get; private set; }
        public string? Filter { get; private set; }
        public string? JoinQuery { get; private set; }
        public string? HostArgument { get; private set; }
        public int AudioPort { get; private set; } = GuestTarget.DefaultAudioPort;
        public int Seconds { get; private set; } = 30;
        public string? RecordPath { get; private set; }
        public bool Reconnect { get; private set; }
        public bool NoPlayback { get; private set; }
        public string? AudioDevice { get; private set; }
        public int BufferMs { get; private set; } = 300;
        public bool Verbose { get; private set; }
        public GuestTarget? Target { get; private set; }

        // Auth options
        public string? User { get; private set; }
        public string? PasswordMd5Hex { get; private set; }
        public string? PasswordPlaintext { get; private set; }

        public static Options Parse(string[] args)
        {
            var options = new Options();

            for (var i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                switch (arg)
                {
                    case "--help" or "-h":
                        options.Help = true;
                        break;
                    case "--verbose" or "-v":
                        options.Verbose = true;
                        break;
                    case "--reconnect":
                        options.Reconnect = true;
                        break;
                    case "--no-playback":
                        options.NoPlayback = true;
                        break;
                    case "--list-devices":
                        options.ListDevicesOnly = true;
                        break;
                    case "--audio-device":
                        options.AudioDevice = Next(args, ref i, arg);
                        break;
                    case "--list":
                        options.ListOnly = true;
                        if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                        {
                            options.Filter = args[++i];
                        }

                        break;
                    case "--join":
                        options.JoinQuery = Next(args, ref i, arg);
                        break;
                    case "--host":
                        options.HostArgument = Next(args, ref i, arg);
                        break;
                    case "--audio-port":
                        options.AudioPort = ParsePort(Next(args, ref i, arg), arg);
                        break;
                    case "--seconds":
                        options.Seconds = int.TryParse(Next(args, ref i, arg), out var seconds) && seconds > 0
                            ? seconds
                            : throw new ArgumentException("--seconds needs a positive integer.");
                        break;
                    case "--record":
                        options.RecordPath = Next(args, ref i, arg);
                        break;
                    case "--buffer":
                        options.BufferMs = int.TryParse(Next(args, ref i, arg), out var bufferMs) && bufferMs is >= 100 and <= 1000
                            ? bufferMs
                            : throw new ArgumentException("--buffer needs a value in 100..1000 (milliseconds).");
                        break;
                    case "--user":
                        options.User = Next(args, ref i, arg);
                        break;
                    case "--password-md5":
                        options.PasswordMd5Hex = Next(args, ref i, arg);
                        break;
                    case "--password":
                        options.PasswordPlaintext = Next(args, ref i, arg);
                        break;
                    default:
                        throw new ArgumentException($"unknown option '{arg}'");
                }
            }

            if ((options.ListDevicesOnly && (options.ListOnly || options.JoinQuery is not null)) ||
                (options.ListOnly && options.JoinQuery is not null) ||
                (options.HostArgument is not null && options.JoinQuery is not null))
            {
                throw new ArgumentException("choose one of --list, --list-devices, --join or --host");
            }

            // Validate auth options
            if (options.PasswordMd5Hex is not null)
            {
                if (options.PasswordMd5Hex.Length != 32 || !IsLowerHex(options.PasswordMd5Hex))
                {
                    throw new ArgumentException("--password-md5 must be a 32-character lowercase hex string.");
                }
            }

            if (options.HostArgument is { Length: > 0 } host)
            {
                options.Target = ParseTarget(host, options.AudioPort);
            }
            else if (!options.ListOnly && !options.ListDevicesOnly && options.JoinQuery is null && !options.Help)
            {
                throw new ArgumentException("nothing to do; use --list, --join or --host");
            }

            return options;
        }

        private static string Next(string[] args, ref int index, string option)
        {
            if (index + 1 >= args.Length)
            {
                throw new ArgumentException($"{option} needs a value");
            }

            return args[++index];
        }

        private static int ParsePort(string value, string option) =>
            int.TryParse(value, out var port) && port is > 0 and <= 65535
                ? port
                : throw new ArgumentException($"{option} needs a port in 1..65535");

        private static bool IsLowerHex(string value)
        {
            foreach (var c in value)
            {
                if (c is not ((>= '0' and <= '9') or (>= 'a' and <= 'f')))
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>Accepts <c>host</c>, <c>host:port</c>, <c>host/audioPort</c> and <c>host:port/audioPort</c>.</summary>
        private static GuestTarget ParseTarget(string argument, int defaultAudioPort)
        {
            var slash = argument.IndexOf('/');
            var endpoint = slash < 0 ? argument : argument[..slash];
            var audio = defaultAudioPort;
            if (slash >= 0 && !int.TryParse(argument[(slash + 1)..], out audio))
            {
                throw new ArgumentException($"'{argument}' has no audio port after '/'");
            }

            var colon = endpoint.LastIndexOf(':');
            var host = colon < 0 ? endpoint : endpoint[..colon];
            var control = GuestTarget.DefaultControlPort;
            if (colon >= 0 && !int.TryParse(endpoint[(colon + 1)..], out control))
            {
                throw new ArgumentException($"'{endpoint}' has no control port after ':'");
            }

            if (string.IsNullOrWhiteSpace(host))
            {
                throw new ArgumentException("--host needs a host name or address");
            }

            return new GuestTarget(host, control, audio);
        }
    }
}