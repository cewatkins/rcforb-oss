# rcforb-oss

A clean-room, cross-platform implementation of an RCForb-compatible client, built from
`../docs/SPEC.md`. Nothing here is derived from decompiled source: the SPEC is the sole
input, and `../work/decompiled/` is reference for the analysis documents only.

## Status

The wire layer, a receive-only guest client, live playback and a read-only desktop UI
exist. There is no login and no transmit path anywhere in the code.

| Project | Purpose | State |
|---|---|---|
| `src/RCForb.Protocol` | transport framing, state model, command grammar — no UI, no audio devices | framing complete for v0.7, framing-only for v0.9 |
| `src/RCForb.Client` | directory feed, v0.7 control/audio channels, Speex decode, state store, guest session | working, receive-only |
| `src/RCForb.Audio` | playout ring and the SoundFlow/miniaudio output sink | working, playback verified by ear and by capture |
| `src/RCForb.Guest` | console client: list the directory, join a remote, listen and hear it | working |
| `src/RCForb.Gui` | Avalonia desktop listener: directory, live state, chat, meters, playout counters | working, receive-only |
| `tests/RCForb.Protocol.Tests` | requirement-level tests for the framing layer | 32 tests |
| `tests/RCForb.Client.Tests` | directory, state, line framing, Speex, playout ring, end-to-end guest over loopback | 47 tests |

All 79 tests pass. See `../docs/PROGRESS.md` for the phase board. The end-to-end
walkthrough — find a remote, listen, record, reconnect, and compare with the original
client — is [docs/guest-listening.md](docs/guest-listening.md).

## Guest mode

`rcforb-guest` joins an advertised remote the way the shipping client does without
credentials. It is listen-only by construction, not by configuration: the session type has
no general "send this command" method, no login field, no transmit request, and the audio
channel has no uplink method. The only bytes it can write are:

| Socket | Written |
|---|---|
| control | `get id`, `set protocol rcs`, `post::heartbeat::<ticks>`, `post::check::cantune` |
| audio | the `IMA` liveness packet (header type 1, never type 2) |

```
dotnet run --project src/RCForb.Guest -- --list [filter]
dotnet run --project src/RCForb.Guest -- --join <index|name> --seconds 60
dotnet run --project src/RCForb.Guest -- --join "K3" --seconds 120 --record rx.wav
dotnet run --project src/RCForb.Guest -- --host 192.0.2.10:4525/4524 --seconds 30
```

Options: `--seconds` (total listening time), `--record <file.wav>` (also keep a copy),
`--reconnect`, `--buffer <ms>`, `--no-playback`, `--audio-device <name>`,
`--list-devices`, `--verbose` (echo every control line), `--audio-port`, `--help`.

Decoded audio is played through the system default output device. `--record` writes the same
stream to an 8 kHz mono 16-bit WAV alongside playback; the two are composed in one
`CompositeAudioSink`, so a recording is byte-identical to what was played.

### What it does on the wire

1. TCP connect to the control port, send `get id`; the server answers `RCForb`.
2. TCP connect to the audio port; the server starts streaming immediately — a guest has no
   session packet to send.
3. Send `set protocol rcs` once; the server pushes the catalogue, state and chat roster.
4. Every 4 s: `post::heartbeat::<ticks>` and `post::check::cantune`, idle only.
5. Every 2 s: `IMA` on the audio socket, whether or not audio is arriving.
6. Decode every complete Speex frame in each payload (a real payload holds five 20 ms
   frames in one bitstream) and write it to every configured sink: the playout ring, the
   WAV file, or both.

### Limits found by running it

- A public host may end a guest session on its own schedule. One host closed both sockets
  at exactly 15.0 s on every attempt, with any keepalive cadence; two others held 60 s and
  were still streaming. The client reports this as a link loss; it does not reconnect and
  has no credentials to offer.
- `--join` follows the public feed at `http://online.remotehams.com/xmlfeed.php`. Every
  remote advertised there today selects the dual-TCP v0.7 path (`VoipPort > 0`), so the
  v0.9/UDP guest path is not implemented and `--join` refuses an entry that would need it.
- Audio is delivered in bursts, not evenly. One public host batches packets so that a
  second can pass with nothing and then 1.2 s arrives at once; with a 300 ms target the
  ring drains back to the target (SPEC-AUD-032) and occasionally runs dry before the next
  burst. `--buffer 800` trades latency for fewer gaps. Measured on three hosts: depth
  oscillates between roughly the target and the burst size, nothing overflows.
- `--reconnect` is opt-in and retries a link end until `--seconds` is used up, 1 s apart.
  That is wider than SPEC-SESS-061's single retry (which exists to stop a client from
  hammering a server over a rejected login); a guest has no login to reject and a fixed
  time budget, and every attempt is printed. A session that ends without a failure is
  never retried.

## Desktop UI

```
dotnet run --project src/RCForb.Gui
dotnet run --project src/RCForb.Gui -- --host 192.0.2.10:4525/4524 --seconds 60
dotnet run --project src/RCForb.Gui -- --host 83.150.196.251 --record rx.wav
```

The window has a remote directory rail with a filter and a manual endpoint, a live state panel
(frequency, mode, band, radio/driver, S-meter, VFO B, anything else the server publishes),
read-only chat and notices, and a bottom strip with the audio level, playout depth, underruns,
packet and decode counters, device picker and buffer target. Startup arguments mirror the CLI:
`--host`, `--seconds`, `--reconnect`, `--record`, `--buffer`, `--audio-device`.

Transmit has no representation: there is no PTT, no tune control, no memory list, no chat
input, no login form and no command entry. Chat and notices are read-only views of what the
server sent. The window is a view over the same `GuestSession` the CLI uses, so the wire
behaviour is identical; nothing in the UI can emit a command.

Two details worth knowing when reading the code:

- The UI re-reads the state every 250 ms when `LinesApplied` changes. The server keeps sending
  `smeter` and `frequency` after the snapshot, so rendering only on `Ready` freezes the
  display within a second of connecting. It reads `RadioStateStore.Snapshot()`, not the live
  bags: lines are applied on the session's pump thread, and enumerating a dictionary while it
  is being written throws `Collection was modified`.
- Sinks are disposed on **disconnect**, not at exit. `WavFileWriter` patches the WAV header on
  dispose, so a recording is playable the moment the session ends rather than when the window
  closes.

## Build and test

Requires the .NET 10 SDK.

```
dotnet build
dotnet test
```

Tests are named after the behaviour they demonstrate and cite the SPEC requirement in a
comment, so a failing test points at the paragraph that defines it. The guest tests run
against a loopback imitator (`FakeV07Server`) and assert on the bytes that actually reached
each socket, which is how "no login, no transmit" is enforced rather than merely intended.

## Release

GitHub Actions builds and tests on every push, and publishes a **self-contained win-x64**
`rcforb-gui.exe` (bundles the .NET 10 runtime, Avalonia, SoundFlow/miniaudio, Speex) as a
release asset on each published release. Download `rcforb-gui-win-x64.zip` from the release
page and run `rcforb-gui.exe` directly — nothing needs pre-installed on the target machine.

```
dotnet publish src/RCForb.Gui -c Release -r win-x64 --self-contained
```

produces the same folder locally.

## Dependencies

| Package | Version | Licence | Why |
|---|---|---|---|
| `SpeexSharp` | 1.2.0 | MIT | managed wrapper plus native `libspeex` for linux-x64 and win-x86/x64 |
| `SoundFlow` | 1.4.2 | MIT | playback through miniaudio; ships natives for linux-x64/arm64/arm, win-x86/x64/arm64, osx-x64/arm64 |
| `Avalonia`, `Avalonia.Desktop`, `Avalonia.Themes.Fluent`, `Avalonia.Fonts.Inter` | 12.1.4 | MIT | desktop UI; no MVVM framework, no DevExpress (it cannot be shipped) |
| `xunit`, `Microsoft.NET.Test.Sdk`, `coverlet.collector` | — | Apache-2.0 / MIT | tests only |

The Speex decoder calls the native `SpeexBits` API directly: the wrapper's `DecodeInt`
decodes exactly one frame per call, while the wire carries several.

Playback opens the device at 8 kHz mono and lets miniaudio convert to whatever the hardware
runs at, so no resampling code lives here. `--audio-device` failures are reported, not fatal: a
host without an output device keeps decoding and recording. SoundFlow's maintainer is on
hiatus until February 2027, which is a supply-chain risk worth knowing; the wrapper around
it is one file (`src/RCForb.Audio/SoundFlowAudioSink.cs`).

## Layout

```
RCForb.slnx
src/RCForb.Protocol/
  WireConstants.cs          protocol-fixed byte values and size limits
  V09/                      single-socket path: text frames, datagram classifier, Opus gate
  V07/                      dual-stream path: 10-byte header reader, session packet
src/RCForb.Client/
  Directory/                public feed parsing and remote lookup
  V07/                      control line pump, audio frame pump, sent-command log
  Audio/                    multi-frame Speex decode, WAV writer
  State/                    state bags, chat, display text decoding
  Session/                  guest handshake, keepalives, counters, report
src/RCForb.Audio/
  PlayoutBuffer.cs          bounded ring: start-at-target, overload drain, overflow
  SoundFlowAudioSink.cs     miniaudio device, one SoundComponent draining the ring
src/RCForb.Guest/           console client
src/RCForb.Gui/             Avalonia window, view models, theme
tests/RCForb.Protocol.Tests/
tests/RCForb.Client.Tests/  includes FakeV07Server and the wire-level guest tests
```

## Assumptions and decisions recorded here

These are implementation choices, not protocol facts; the protocol facts are in the SPEC.

1. **.NET 10, not .NET 8.** `docs/LANGUAGE_DECISION.md` recommended .NET 8 when it was
   written. .NET 8 leaves support in November 2026; .NET 10 is the current LTS (to
   November 2028) and is the only SDK installed on the development host. The stack choice
   (C# + Avalonia) is unchanged.
2. **Framing before transport.** The UDP/TCP I/O, timers and relay are deliberately not
   implemented first: the framing rules are the part that is fully determined by the SPEC
   and testable without a peer.
3. **Payloads are copied out of the frame reader.** Consuming a frame shifts the pending
   buffer, so returning a view into it would start returning the next packet's bytes. A
   ring buffer can remove the allocation later; correctness first.
4. **No licence file yet.** Apache-2.0 is the recommendation in `docs/LEGAL_NOTES.md` and is
   not yet chosen by the project owner, so no `LICENSE` is present.
5. **Guest before general client.** The first user-visible milestone is the smallest thing
   that proves the protocol end to end: join, receive, decode, stop. A general client needs
   credentials, playback, a UI and transmit semantics, none of which the SPEC settles as
   firmly as the receive path.
6. **Reconnect is a flag, not a policy.** A server that closes a guest session is expressing
   a policy, so the client never reconnects on its own; `--reconnect` says the operator wants
   the time budget filled anyway, and stops as soon as `--seconds` runs out.

## Deliberate gaps

- v0.9 (single-socket UDP/Opus) has framing and classification only. No session, no audio.
- Playback is mono, default-device-or-named-device only: no per-device format negotiation
  beyond miniaudio's own, no volume control, no mute, no latency calibration.
- The device is not paused on underrun (SPEC-AUD-031): an idle device emitting silence is
  indistinguishable to a listener, and pausing costs a restart per late packet. The ring
  still starts at the target depth and drains overload, so the audible behaviour matches.
- Chat, buttons, memories, tuning and transmit are read-only in the state model and cannot
  be sent at all from the guest session.
- The UI is guest-only: no account login, so a host that requires one is simply refused, and
  the server's reason is shown verbatim (`The remote closed: …`).
- The v0.7 keepalive control packet and the `IMA` payload are produced by the caller; the
  framing reader classifies them but does not synthesise them.
- The 16 KiB framing-buffer cap throws instead of stalling. A network loop must apply
  backpressure rather than let the buffer grow.
- No relay (IpEx) support; the original never uses it either (`SPEC-TRAN-078`, `MAY`).
