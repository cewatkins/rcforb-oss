using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using RCForb.Client.Audio;
using RCForb.Client.Session;

namespace RCForb.Client.Tests;

/// <summary>
/// End-to-end guest behaviour against a loopback v0.7 imitator. These are the tests that
/// hold the "listen only" promise: they inspect what actually went on the wire, not what the
/// code intended to send.
/// </summary>
public class GuestSessionTests
{
    private static GuestSession NewSession(FakeV07Server server, IGuestAudioSink? sink = null, TimeSpan? syncTimeout = null) =>
        new(new GuestSessionOptions
        {
            Target = new GuestTarget("127.0.0.1", server.ControlPort, server.AudioPort),
            AudioSink = sink,
            SynchronisationTimeout = syncTimeout ?? TimeSpan.FromSeconds(15),
        });

    [Fact] // SPEC-SESS-005 / SPEC 02 §7.3: guest handshake, no login line.
    public async Task A_guest_connects_without_credentials_and_never_writes_login()
    {
        await using var server = FakeV07Server.Start(audioPayload: FakeV07Server.FiveFramePayload());
        var session = NewSession(server);

        var report = await session.RunAsync(TimeSpan.FromMilliseconds(900), CancellationToken.None);

        Assert.True(report.Ready);
        Assert.Null(report.Failure);
        Assert.Equal(28_074_000, session.State.FrequencyHz);
        Assert.Equal("K3", session.State.RadioName);

        Assert.Contains("get id", server.ControlLines);
        Assert.Equal(1, server.ControlLines.Count(line => line == "set protocol rcs"));
        Assert.DoesNotContain(server.ControlLines, line => line.StartsWith("login", StringComparison.Ordinal));

        // Nothing that requires authorisation or keys a transmitter.
        Assert.DoesNotContain(server.ControlLines, line => line.Contains("TX", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(server.ControlLines, line => line.StartsWith("post::request", StringComparison.Ordinal));
        Assert.DoesNotContain(server.ControlLines, line => line.StartsWith("radio::", StringComparison.Ordinal));
    }

    [Fact] // SPEC-AUD-018/022: a five-frame payload decodes completely.
    public async Task Receive_audio_is_decoded_from_the_payload()
    {
        await using var server = FakeV07Server.Start(audioPayload: FakeV07Server.FiveFramePayload());
        var session = NewSession(server);

        var report = await session.RunAsync(TimeSpan.FromMilliseconds(900), CancellationToken.None);

        Assert.True(report.AudioPackets >= 1);
        Assert.Equal(800, report.DecodedSamples);
        Assert.Equal(0, report.DecodeErrors);
        Assert.True(session.Stats.LastRms > 0);
    }

    [Fact] // SPEC-SESS-015: the chat roster alone satisfies the readiness latch.
    public async Task The_chat_roster_alone_latches_readiness()
    {
        await using var server = FakeV07Server.Start(snapshot:
        [
            "chat::System::2026-10-28 13:44:18 +01:00::Hello%20guest",
            "post::radio::K3",
        ]);
        var session = NewSession(server);
        var chats = new List<string>();
        session.ChatReceived += chat => chats.Add(chat.Text);

        var report = await session.RunAsync(TimeSpan.FromMilliseconds(700), CancellationToken.None);

        Assert.True(report.Ready);
        Assert.Contains("Hello guest", chats);
    }

    [Fact] // SPEC-SESS-017/020: no marker means a degraded but live session.
    public async Task A_missing_marker_is_reported_but_does_not_end_the_session()
    {
        await using var server = FakeV07Server.Start(snapshot: ["post::radio::K3"]);
        var session = NewSession(server, syncTimeout: TimeSpan.FromMilliseconds(200));
        var notices = new List<string>();
        session.Notice += (kind, text) => notices.Add($"{kind}:{text}");

        var report = await session.RunAsync(TimeSpan.FromMilliseconds(800), CancellationToken.None);

        Assert.True(report.SyncTimedOut);
        Assert.False(report.Ready);
        Assert.Null(report.Failure);
        Assert.Contains(notices, n => n.StartsWith("sync:", StringComparison.Ordinal));
    }

    [Fact] // SPEC 04 row 82: the reason a remote closed is operator-facing text, not a silent drop.
    public async Task A_closed_remote_is_reported_with_the_reason_the_server_gave()
    {
        await using var server = FakeV07Server.Start(snapshot:
        [
            "post::radio::K3",
            "post::radio-closed::Guests are not allowed, please login",
            "post::radio-closed::Guests are not allowed, please login",
            "post::state-posted",
        ]);
        var session = NewSession(server);
        var notices = new List<string>();
        session.Notice += (kind, text) => notices.Add($"{kind}:{text}");

        await session.RunAsync(TimeSpan.FromMilliseconds(600), CancellationToken.None);

        // Sent twice, reported once: a server nags, and a nag is not new information.
        Assert.Single(notices, n => n.Contains("Guests are not allowed", StringComparison.Ordinal));
    }

    [Fact] // SPEC-TRAN-051: keepalives are on a fixed schedule; IMA is the only audio write.
    public async Task Keepalives_fire_on_schedule_and_the_audio_socket_carries_no_voice()
    {
        await using var server = FakeV07Server.Start(audioPayload: FakeV07Server.FiveFramePayload());
        var session = NewSession(server);

        var report = await session.RunAsync(TimeSpan.FromMilliseconds(4300), CancellationToken.None);

        Assert.Null(report.Failure);
        Assert.Contains(server.ControlLines, line => line.StartsWith("post::heartbeat::", StringComparison.Ordinal));
        Assert.Contains("post::check::cantune", server.ControlLines);
        Assert.DoesNotContain(server.ControlLines, line => line.StartsWith("login", StringComparison.Ordinal));

        var audio = server.AudioBytes;
        Assert.NotEmpty(audio);

        // More than one IMA is expected in a 4.3 s window (the tick is 2 s), so walk the
        // packet stream rather than assuming a single 13-byte frame.
        var offset = 0;
        var imaPackets = 0;
        while (offset + 10 <= audio.Length)
        {
            var length = BinaryPrimitives.ReadInt32LittleEndian(audio.AsSpan(offset));
            Assert.Equal(3, length);

            // SPEC-TRAN-032: 1 means control with the transmitter idle. 2 would claim PTT.
            Assert.Equal(1, audio[offset + 4]);
            Assert.Equal("IMA", Encoding.ASCII.GetString(audio, offset + 10, 3));
            offset += 10 + length;
            imaPackets++;
        }

        Assert.Equal(audio.Length, offset);
        Assert.True(imaPackets >= 2);
    }

    [Fact] // SPEC-TRAN-052/053: loss is reported once, and there is no logout command.
    public async Task A_closed_control_socket_is_reported_as_a_link_loss()
    {
        await using var server = FakeV07Server.Start(closeControlAfterSnapshot: true);
        var session = NewSession(server);

        var report = await session.RunAsync(TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.NotNull(report.Failure);
        Assert.DoesNotContain(server.ControlLines, line => line is "exit" or "bye" or "logout");
    }

    [Fact]
    public async Task Recording_writes_a_playable_wav_header_and_the_decoded_samples()
    {
        // The test output directory, not the system temp directory: /tmp is a small tmpfs on
        // some machines and a 3 KB WAV can fail with "Disk quota exceeded" for reasons that
        // have nothing to do with this test.
        var path = Path.Combine(AppContext.BaseDirectory, $"rcforb-guest-{Guid.NewGuid():N}.wav");
        try
        {
            await using var server = FakeV07Server.Start(audioPayload: FakeV07Server.FiveFramePayload());
            using var recorder = new WavFileWriter(path);
            var session = NewSession(server, recorder);

            await session.RunAsync(TimeSpan.FromMilliseconds(900), CancellationToken.None);

            recorder.Dispose();

            var bytes = await File.ReadAllBytesAsync(path);
            Assert.True(bytes.Length >= 44 + 1600);
            Assert.Equal("RIFF", Encoding.ASCII.GetString(bytes, 0, 4));
            Assert.Equal("WAVE", Encoding.ASCII.GetString(bytes, 8, 4));
            Assert.Equal(8000, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(24)));
            Assert.Equal(1600, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(40)));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact] // The sink outlives the session: a reconnect must not truncate the recording.
    public async Task A_sink_survives_the_session_that_wrote_to_it()
    {
        // The test output directory, not the system temp directory: /tmp is a small tmpfs on
        // some machines and a 3 KB WAV can fail with "Disk quota exceeded" for reasons that
        // have nothing to do with this test.
        var path = Path.Combine(AppContext.BaseDirectory, $"rcforb-guest-{Guid.NewGuid():N}.wav");
        try
        {
            using var recorder = new WavFileWriter(path);

            for (var attempt = 0; attempt < 2; attempt++)
            {
                await using var server = FakeV07Server.Start(audioPayload: FakeV07Server.FiveFramePayload());
                var session = NewSession(server, recorder);
                var report = await session.RunAsync(TimeSpan.FromMilliseconds(600), CancellationToken.None);
                Assert.Null(report.Failure);
            }

            // Two sessions over one sink: both payloads are in the file, header patched once.
            recorder.Dispose();
            var bytes = await File.ReadAllBytesAsync(path);
            Assert.Equal(3200, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(40)));
            Assert.Equal(44 + 3200, bytes.Length);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

/// <summary>
/// Tests for AuthenticatedSession: login, session packet, auth responses, chat send.
/// </summary>
public class AuthenticatedSessionTests
{
    private static AuthenticatedSession NewSession(FakeV07Server server, string user = "testuser", string passwordMd5Hex = "5f4dcc3b5aa765d61d8327deb882cf99", IGuestAudioSink? sink = null) =>
        new(new AuthenticatedSessionOptions
        {
            Target = new AuthenticatedTarget("127.0.0.1", server.ControlPort, server.AudioPort, user, passwordMd5Hex),
            AudioSink = sink,
        });

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

    [Fact] // SPEC-SESS-004/007: login sent after readiness, session packet on audio socket.
    public async Task Authenticated_session_sends_login_after_readiness_and_session_packet()
    {
        var md5Hex = ComputeMd5Hex("password");
        await using var server = FakeV07Server.Start(
            snapshot:
            [
                "post::radio::K3",
                "post::frequency::28074000",
                "post::state-posted",
            ],
            audioPayload: FakeV07Server.FiveFramePayload(),
            authSuccessMessage: "Welcome, you are now authenticated");
        var session = NewSession(server, "testuser", md5Hex);

        var report = await session.RunAsync(TimeSpan.FromMilliseconds(1500), CancellationToken.None);

        Assert.True(report.Ready);
        Assert.Equal(AuthState.Authorised, report.AuthState);

        // Login sent exactly once
        Assert.Contains(server.ControlLines, line => line == $"login testuser {md5Hex}");
        Assert.Equal(1, server.ControlLines.Count(line => line.StartsWith("login ", StringComparison.Ordinal)));

        // Session packet sent on audio socket (64 bytes, starts with magic 0x36 0x00 0x00 0x00)
        // Find it in the recorded client audio bytes
        var audio = server.AudioBytes;
        var found = FindSessionPacket(audio, "testuser", md5Hex);
        Assert.True(found, "Session packet not found in audio bytes");
    }

    private static bool FindSessionPacket(byte[] audio, string expectedUser, string expectedMd5)
    {
        // The session packet is sent as raw 64 bytes (no 10-byte header).
        // Scan through the audio bytes looking for the 64-byte packet.
        for (var offset = 0; offset + 64 <= audio.Length; offset++)
        {
            var magic = BinaryPrimitives.ReadInt32LittleEndian(audio.AsSpan(offset));
            if (magic != 54) // SessionMagic
            {
                continue;
            }

            var payloadLength = BinaryPrimitives.ReadInt32LittleEndian(audio.AsSpan(offset + 4));
            if (payloadLength < 2 || payloadLength > 54)
            {
                continue;
            }

            if (offset + 8 + payloadLength > audio.Length)
            {
                continue;
            }

            var payload = audio.AsSpan(offset + 8, payloadLength);
            var comma = payload.IndexOf((byte)',');
            if (comma <= 0)
            {
                continue;
            }

            var user = Encoding.ASCII.GetString(payload[..comma]);
            var digest = Encoding.ASCII.GetString(payload[(comma + 1)..]);
            if (user == expectedUser && digest == expectedMd5)
            {
                return true;
            }
        }
        return false;
    }

    [Fact] // SPEC-SESS-005: guest sends no login; authenticated session does.
    public async Task Guest_sends_no_login_but_authenticated_does()
    {
        var md5Hex = ComputeMd5Hex("password");
        await using var guestServer = FakeV07Server.Start(audioPayload: FakeV07Server.FiveFramePayload());
        await using var authServer = FakeV07Server.Start(
            snapshot:
            [
                "post::radio::K3",
                "post::state-posted",
            ],
            audioPayload: FakeV07Server.FiveFramePayload(),
            authSuccessMessage: "Welcome, you are now authenticated");

        var guestSession = new GuestSession(new GuestSessionOptions
        {
            Target = new GuestTarget("127.0.0.1", guestServer.ControlPort, guestServer.AudioPort),
        });
        var authSession = NewSession(authServer, "testuser", md5Hex);

        await guestSession.RunAsync(TimeSpan.FromMilliseconds(900), CancellationToken.None);
        await authSession.RunAsync(TimeSpan.FromMilliseconds(900), CancellationToken.None);

        Assert.DoesNotContain(guestServer.ControlLines, line => line.StartsWith("login", StringComparison.Ordinal));
        Assert.Contains(authServer.ControlLines, line => line.StartsWith("login testuser ", StringComparison.Ordinal));
    }

    [Fact] // SPEC-SESS-011: auth refusal reported as authentication outcome.
    public async Task Auth_refusal_reported_as_auth_state_refused()
    {
        var md5Hex = ComputeMd5Hex("wrongpassword");
        await using var server = FakeV07Server.Start(
            snapshot:
            [
                "post::radio::K3",
                "post::state-posted",
            ],
            audioPayload: FakeV07Server.FiveFramePayload(),
            authFailureMessage: "Invalid username or password");
        var session = NewSession(server, "testuser", md5Hex);

        var authStates = new List<AuthState>();
        session.AuthStateChanged += (state, _) => authStates.Add(state);

        var report = await session.RunAsync(TimeSpan.FromMilliseconds(1500), CancellationToken.None);

        Assert.Equal(AuthState.Refused, report.AuthState);
        Assert.Contains("Invalid username or password", report.AuthMessage);
        Assert.Contains(AuthState.Refused, authStates);
    }

    [Fact] // SPEC-SESS-007: session packet contains correct user and digest.
    public async Task Session_packet_contains_username_and_md5_digest()
    {
        var md5Hex = ComputeMd5Hex("password123");
        await using var server = FakeV07Server.Start(
            snapshot:
            [
                "post::radio::K3",
                "post::state-posted",
            ],
            audioPayload: FakeV07Server.FiveFramePayload(),
            authSuccessMessage: "Welcome, you are now authenticated");
        var session = NewSession(server, "myuser", md5Hex);

        await session.RunAsync(TimeSpan.FromMilliseconds(1200), CancellationToken.None);

        // Find session packet in recorded audio bytes
        var audio = server.AudioBytes;
        var found = FindSessionPacket(audio, "myuser", md5Hex);
        Assert.True(found, "Session packet not found in audio bytes");
    }

    [Fact] // Chat send not available before auth.
    public async Task Chat_send_rejected_before_authorised()
    {
        var md5Hex = ComputeMd5Hex("password");
        await using var server = FakeV07Server.Start(
            snapshot:
            [
                "post::radio::K3",
                "post::state-posted",
                // Server never sends auth success
            ],
            audioPayload: FakeV07Server.FiveFramePayload());
        var session = NewSession(server, "testuser", md5Hex);

        _ = session.RunAsync(TimeSpan.FromMilliseconds(2000), CancellationToken.None);

        // Wait for login to be sent
        await Task.Delay(500);

        var result = await session.SendChatAsync("hello");
        Assert.False(result.Sent);
        Assert.Contains("Not authorised", result.Error);
    }

    // ---- Channel control, re-login, revert, restrictions (SPEC 04 rows 23–25, 27, 81) ----

    private static async Task WaitForAuthStateAsync(AuthenticatedSession session, AuthState expected, TimeSpan? timeout = null)
    {
        await WaitForConditionAsync(() => session.AuthState == expected, timeout);
    }

    /// <summary>
    /// The imitator records a line when its own reader drains the socket, which can trail the
    /// client's completed write by a few milliseconds; assertions wait rather than assume.
    /// </summary>
    private static async Task WaitForConditionAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        var limit = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(3));
        while (!condition() && DateTime.UtcNow < limit)
        {
            await Task.Delay(50);
        }
    }

    [Fact] // SPEC-CMD-052/064: radio writes go out as post::<verb> on V7.
    public async Task Channel_writes_emit_the_normalised_post_forms()
    {
        await using var server = FakeV07Server.Start(
            snapshot: ["post::radio::K3", "post::state-posted"],
            audioPayload: FakeV07Server.FiveFramePayload(),
            authSuccessMessage: "Welcome");
        var session = NewSession(server);

        _ = session.RunAsync(TimeSpan.FromMilliseconds(2500), CancellationToken.None);
        await WaitForConditionAsync(() => server.ControlLines.Contains("set protocol rcs"));

        Assert.True((await session.SetFrequencyAsync(14_250_000)).Sent);
        Assert.True((await session.SetFrequencyBAsync(7_185_000)).Sent);
        Assert.True((await session.SetBandAsync("40M")).Sent);
        Assert.True((await session.SetModeAsync("USB")).Sent);
        Assert.True((await session.SetFilterAsync("Narrow")).Sent);

        await WaitForConditionAsync(() =>
            server.ControlLines.Contains("post::frequency::14250000") &&
            server.ControlLines.Contains("post::frequencyb::7185000") &&
            server.ControlLines.Contains("post::band::40M") &&
            server.ControlLines.Contains("post::dropdown::Mode::USB") &&
            server.ControlLines.Contains("post::dropdown::Filter::Narrow"));

        Assert.Contains("post::frequency::14250000", server.ControlLines);
        Assert.Contains("post::frequencyb::7185000", server.ControlLines);
        Assert.Contains("post::band::40M", server.ControlLines);
        Assert.Contains("post::dropdown::Mode::USB", server.ControlLines);
        Assert.Contains("post::dropdown::Filter::Narrow", server.ControlLines);
    }

    [Fact] // SPEC-CMD-045: a channel write needs a running session, not an authorised one.
    public async Task Channel_writes_work_while_authentication_is_still_pending()
    {
        await using var server = FakeV07Server.Start(
            snapshot: ["post::radio::K3", "post::state-posted"],
            audioPayload: FakeV07Server.FiveFramePayload());
        var session = NewSession(server);

        _ = session.RunAsync(TimeSpan.FromMilliseconds(2000), CancellationToken.None);
        await WaitForAuthStateAsync(session, AuthState.Authenticating);

        Assert.True((await session.SetFrequencyAsync(7_000_000)).Sent);
        await WaitForConditionAsync(() => server.ControlLines.Contains("post::frequency::7000000"));
        Assert.Contains("post::frequency::7000000", server.ControlLines);
    }

    [Fact] // SPEC-CMD-019/068: outbound arguments must be representable and carry text, not indexes.
    public async Task Channel_write_validation_rejects_unrepresentable_arguments()
    {
        await using var server = FakeV07Server.Start(
            snapshot: ["post::radio::K3", "post::state-posted"],
            audioPayload: FakeV07Server.FiveFramePayload(),
            authSuccessMessage: "Welcome");
        var session = NewSession(server);

        _ = session.RunAsync(TimeSpan.FromMilliseconds(2000), CancellationToken.None);
        await WaitForConditionAsync(() => server.ControlLines.Contains("set protocol rcs"));

        Assert.False((await session.SetFrequencyAsync(0)).Sent);
        Assert.False((await session.SetFrequencyAsync(10_000_000_001)).Sent);
        Assert.False((await session.SetFrequencyBAsync(-1)).Sent);
        Assert.False((await session.SetBandAsync("")).Sent);
        Assert.False((await session.SetBandAsync("   ")).Sent);
        Assert.False((await session.SetBandAsync("40::M")).Sent);
        Assert.False((await session.SetModeAsync("")).Sent);
        Assert.False((await session.SetModeAsync("A::B")).Sent);

        Assert.DoesNotContain(server.ControlLines, line => line.StartsWith("post::frequency::0", StringComparison.Ordinal));
        Assert.DoesNotContain(server.ControlLines, line => line.StartsWith("post::band::", StringComparison.Ordinal));
        Assert.DoesNotContain(server.ControlLines, line => line.StartsWith("post::dropdown::", StringComparison.Ordinal));
    }

    [Fact] // SPEC-CMD-007 / SPEC 05 §3.9: option text may contain spaces and is never trimmed away.
    public async Task Dropdown_option_text_with_internal_spaces_is_sent_verbatim()
    {
        await using var server = FakeV07Server.Start(
            snapshot: ["post::radio::K3", "post::state-posted"],
            audioPayload: FakeV07Server.FiveFramePayload(),
            authSuccessMessage: "Welcome");
        var session = NewSession(server);

        _ = session.RunAsync(TimeSpan.FromMilliseconds(2000), CancellationToken.None);
        await WaitForConditionAsync(() => server.ControlLines.Contains("set protocol rcs"));

        Assert.True((await session.SetFilterAsync("15 kHz")).Sent);
        Assert.True((await session.SetBandAsync(" 40M ")).Sent);

        await WaitForConditionAsync(() =>
            server.ControlLines.Contains("post::dropdown::Filter::15 kHz") &&
            server.ControlLines.Contains("post::band::40M"));

        Assert.Contains("post::dropdown::Filter::15 kHz", server.ControlLines);
        Assert.Contains("post::band::40M", server.ControlLines);
        Assert.DoesNotContain("post::dropdown::Filter:: 15 kHz", server.ControlLines);
    }

    [Fact] // SPEC-CMD-065: a fresh attempt needs operator action; revert blocks chat and never re-fires the login.
    public async Task Revert_to_guest_clears_auth_state_and_blocks_chat()
    {
        await using var server = FakeV07Server.Start(
            snapshot: ["post::radio::K3", "post::state-posted"],
            audioPayload: FakeV07Server.FiveFramePayload(),
            authSuccessMessage: "Welcome, you are now authenticated");
        var session = NewSession(server);

        _ = session.RunAsync(TimeSpan.FromMilliseconds(2500), CancellationToken.None);
        await WaitForAuthStateAsync(session, AuthState.Authorised);
        Assert.Equal(1, server.ControlLines.Count(line => line.StartsWith("login ", StringComparison.Ordinal)));

        session.RevertToGuest();

        Assert.Equal(AuthState.Pending, session.AuthState);
        var chat = await session.SendChatAsync("hello");
        Assert.False(chat.Sent);
        Assert.Contains("Not authorised", chat.Error);

        await Task.Delay(700);
        Assert.Equal(1, server.ControlLines.Count(line => line.StartsWith("login ", StringComparison.Ordinal)));
    }

    [Fact] // Re-login drives a different account on the live sockets and recovers from a refusal.
    public async Task Relogin_sends_a_second_login_and_session_packet_for_the_new_user()
    {
        var secondMd5 = ComputeMd5Hex("second-password");
        await using var server = FakeV07Server.Start(
            snapshot: ["post::radio::K3", "post::state-posted"],
            audioPayload: FakeV07Server.FiveFramePayload(),
            authResponses: new Dictionary<string, string>
            {
                ["testuser"] = "Invalid username or password",
                ["seconduser"] = "Welcome, you are now authenticated",
            });
        var session = NewSession(server, "testuser", ComputeMd5Hex("wrong"));

        var states = new List<AuthState>();
        session.AuthStateChanged += (state, _) => states.Add(state);

        _ = session.RunAsync(TimeSpan.FromMilliseconds(3000), CancellationToken.None);
        await WaitForAuthStateAsync(session, AuthState.Refused);
        Assert.Equal(1, server.ControlLines.Count(line => line.StartsWith("login ", StringComparison.Ordinal)));

        var result = await session.ReloginAsync("seconduser", secondMd5);
        Assert.True(result.Sent);

        await WaitForAuthStateAsync(session, AuthState.Authorised);
        Assert.Equal(2, server.ControlLines.Count(line => line.StartsWith("login ", StringComparison.Ordinal)));
        Assert.Contains(server.ControlLines, line => line == $"login seconduser {secondMd5}");
        await WaitForConditionAsync(() => FindSessionPacket(server.AudioBytes, "seconduser", secondMd5));
        Assert.True(FindSessionPacket(server.AudioBytes, "seconduser", secondMd5));

        // The auto-login gate is sticky: no third login may appear on its own.
        await Task.Delay(700);
        Assert.Equal(2, server.ControlLines.Count(line => line.StartsWith("login ", StringComparison.Ordinal)));
        Assert.Contains(states, s => s == AuthState.Refused);
        Assert.Contains(states, s => s == AuthState.Authenticating);
        Assert.Contains(states, s => s == AuthState.Authorised);
    }

    [Fact] // SPEC 04 row 81: another holder is a restriction, surfaced with the holder's name.
    public async Task Radio_in_use_snapshot_surfaces_a_restriction_notice()
    {
        await using var server = FakeV07Server.Start(
            snapshot:
            [
                "post::radio::K3",
                "post::state-posted",
                "post::radio-in-use::testuser",
            ],
            audioPayload: FakeV07Server.FiveFramePayload(),
            authSuccessMessage: "Welcome");
        var session = NewSession(server);

        var notices = new List<(string Kind, string Text)>();
        session.Notice += (kind, text) => notices.Add((kind, text));

        await session.RunAsync(TimeSpan.FromMilliseconds(1200), CancellationToken.None);

        Assert.Contains(notices, n => n.Kind == "restriction" && n.Text.Contains("testuser", StringComparison.Ordinal));
        Assert.Single(notices, n => n.Kind == "restriction");
    }

    [Fact] // SPEC-CMD-044/045: the server echo alone updates state; the write itself never does.
    public async Task State_changes_only_when_the_server_echoes_a_channel_write()
    {
        await using var server = FakeV07Server.Start(
            snapshot:
            [
                "post::radio::K3",
                "post::frequency::28074000",
                "post::state-posted",
            ],
            audioPayload: FakeV07Server.FiveFramePayload(),
            authSuccessMessage: "Welcome",
            echoResponder: EchoChannelWrite);
        var session = NewSession(server);

        _ = session.RunAsync(TimeSpan.FromMilliseconds(3000), CancellationToken.None);
        await WaitForAuthStateAsync(session, AuthState.Authorised);

        Assert.Equal("28074000", session.State.Snapshot().FrequencyText);

        Assert.True((await session.SetFrequencyAsync(14_250_000)).Sent);
        Assert.True((await session.SetModeAsync("CW")).Sent);

        // The imitator queues the echoes: until they are released, nothing may move.
        Assert.Equal("28074000", session.State.Snapshot().FrequencyText);
        Assert.NotEqual("CW", session.State.Snapshot().Dropdowns.GetValueOrDefault("Mode"));

        await server.EchoQueuedAsync();
        await WaitForConditionAsync(() => session.State.Snapshot().FrequencyText == "14250000"
            && session.State.Snapshot().Dropdowns.GetValueOrDefault("Mode") == "CW");

        var after = session.State.Snapshot();
        Assert.Equal("14250000", after.FrequencyText);
        Assert.Equal("CW", after.Dropdowns.GetValueOrDefault("Mode"));
    }

    /// <summary>Maps a client channel write to the state line a conforming server would echo.</summary>
    private static string? EchoChannelWrite(string line)
    {
        if (line.StartsWith("post::frequency::", StringComparison.Ordinal) ||
            line.StartsWith("post::band::", StringComparison.Ordinal) ||
            line.StartsWith("post::dropdown::", StringComparison.Ordinal))
        {
            return line;
        }

        if (line.StartsWith("post::frequencyb::", StringComparison.Ordinal))
        {
            return line.Replace("post::frequencyb::", "post::frequencyB::", StringComparison.Ordinal);
        }

        return null;
    }

    [Fact] // Undo then try again: revert -> pending -> relogin -> authorised.
    public async Task Revert_then_relogin_drives_a_fresh_attempt_and_recovers()
    {
        var md5Hex = ComputeMd5Hex("password");
        await using var server = FakeV07Server.Start(
            snapshot: ["post::radio::K3", "post::state-posted"],
            audioPayload: FakeV07Server.FiveFramePayload(),
            authSuccessMessage: "Welcome");
        var session = NewSession(server, "testuser", md5Hex);

        var states = new List<AuthState>();
        session.AuthStateChanged += (state, _) => states.Add(state);

        _ = session.RunAsync(TimeSpan.FromMilliseconds(2500), CancellationToken.None);
        await WaitForAuthStateAsync(session, AuthState.Authorised);

        session.RevertToGuest();
        Assert.Equal(AuthState.Pending, session.AuthState);

        Assert.True((await session.ReloginAsync("testuser", md5Hex)).Sent);
        await WaitForAuthStateAsync(session, AuthState.Authorised);

        Assert.Equal(2, server.ControlLines.Count(line => line.StartsWith("login ", StringComparison.Ordinal)));
        Assert.Contains(states, s => s == AuthState.Pending);
        Assert.Contains(states, s => s == AuthState.Authorised);
    }

    [Fact] // SPEC-CMD-054: chat is sent raw via post::chat:: once authorised.
    public async Task Authorised_chat_send_goes_out_as_a_single_post_chat_line()
    {
        await using var server = FakeV07Server.Start(
            snapshot: ["post::radio::K3", "post::state-posted"],
            audioPayload: FakeV07Server.FiveFramePayload(),
            authSuccessMessage: "Welcome");
        var session = NewSession(server);

        _ = session.RunAsync(TimeSpan.FromMilliseconds(2500), CancellationToken.None);
        await WaitForAuthStateAsync(session, AuthState.Authorised);

        Assert.True((await session.SendChatAsync("hello world")).Sent);
        await WaitForConditionAsync(() => server.ControlLines.Contains("post::chat::hello world"));
        Assert.Contains("post::chat::hello world", server.ControlLines);
    }

    [Fact] // SPEC-CMD-019/054: chat validation rejects unrepresentable text before the wire.
    public async Task Chat_send_rejects_unrepresentable_text_before_the_wire()
    {
        await using var server = FakeV07Server.Start(
            snapshot: ["post::radio::K3", "post::state-posted"],
            audioPayload: FakeV07Server.FiveFramePayload(),
            authSuccessMessage: "Welcome");
        var session = NewSession(server);

        _ = session.RunAsync(TimeSpan.FromMilliseconds(2500), CancellationToken.None);
        await WaitForAuthStateAsync(session, AuthState.Authorised);

        Assert.False((await session.SendChatAsync("")).Sent);
        Assert.False((await session.SendChatAsync("a::b")).Sent);
        Assert.False((await session.SendChatAsync("100%")).Sent);
        Assert.False((await session.SendChatAsync("/pm someone hi")).Sent);

        Assert.DoesNotContain(server.ControlLines, line => line.StartsWith("post::chat::", StringComparison.Ordinal));
    }
}
