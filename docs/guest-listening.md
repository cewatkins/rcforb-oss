# Listening to a RemoteHams radio as a guest

`rcforb-guest` joins an advertised remote the way `RCForbClient.exe` does when no credentials
are configured: it connects, receives audio and state, and does nothing else. There is no
login, no password, no transmit, no tuning, no memory access and no chat send. The session
type has no method that could express any of them.

This guide is the whole procedure: find a remote, listen, record, survive a server-side
close, and check the result against the original client.

---

## 1. Requirements

| Need | Detail |
|---|---|
| .NET | .NET 10 SDK (build) or the published binary (run) |
| Audio out | Any device the system can open; playback is the default and recording is optional |
| Network | Outbound TCP to the remote's control and audio ports; the directory feed is HTTP |
| Permission | Listen only to remotes you own or that publish an open listen policy |

## 2. Build it

```bash
cd rcforb-oss
dotnet build
```

That produces `src/RCForb.Guest/bin/Debug/net10.0/rcforb-guest`. For a copy you can move
elsewhere:

```bash
dotnet publish src/RCForb.Guest -c Release -r linux-x64 --self-contained -o dist
./dist/rcforb-guest --list
```

Substitute `win-x64`, `osx-arm64`, `linux-arm64` as needed; the audio backend ships natives
for all of them. The output is a folder of about 150 MB, not a single file: miniaudio is a
native library and single-file bundling fails on it. Add `-p:DebugType=None` if you do not
want the `.pdb` files. Publish somewhere with room — a small tmpfs will fail with
`MSB3021: Disk quota exceeded` halfway through.

## 3. Find a remote

```bash
rcforb-guest --list
rcforb-guest --list K3          # substring match on server, radio or country
```

```
   #  server                       radio            listen  tx proto endpoint
 104  K3NIE                        Ic-7300          public  yes v0.7  71.175.40.132:4525 audio 4524
 146  LZ2KSB                       K3               public  no v0.7  212.75.22.177:4525 audio 4524
 175  Remote SP                    K3               public  yes v0.7  83.150.196.251:4525 audio 4524
```

* **listen** is the owner's policy as published. `public` means the owner expects listeners.
* **tx** describes what the *server* allows, not what this client can do — a guest cannot
  transmit at all.
* **proto** must be `v0.7`. Every remote advertised today is; a `v0.9` entry is refused
  because that path is not implemented.

## 4. Listen

```bash
rcforb-guest --join 175 --seconds 120
rcforb-guest --join "Remote SP" --seconds 60
rcforb-guest --host 83.150.196.251 --seconds 60
rcforb-guest --host 192.0.2.10:4525/4524 --seconds 60
```

`--join` takes the row number from `--list` or a server/radio name. `--host` skips the feed
entirely; the forms are `host`, `host:controlPort`, `host/audioPort`, `host:controlPort/audioPort`.

`--seconds` is total wall-clock time, reconnect gaps included. `Ctrl-C` stops early and still
prints the summary.

## 5. What you see

```
Guest listen — KF4LZA / Ic-9100 (United States)
  listen policy: public; no login, no transmit
  endpoint:      50.196.72.195:4525 (audio 4524)
  playback:      Harman/Kardon SoundSticks Analog Stereo (8000 Hz mono, 0.30 s buffer)

[state] snapshot latched
        Radio:     Ic-9100
        Frequency: 7.185000 MHz (LSB, FL1)
        Band:      40
        S-meter:   S9,9,21

[chat] System: Welcome to KF4LZA's ICOM IC-9100 HF radio. ...
[info] +++++++++++USE TXd TO TRANSMIT! ...

[  5s] audio   4.6s    46 pkt  5.0 frames/pkt  ##..................  buf 0.74s   10s left
[ 10s] audio   9.6s    96 pkt  5.0 frames/pkt  ##..................  buf 0.86s    5s left

  attempts:  1
  duration:  15.3 s wall clock, 15.3 s connected
  state:     ready
  control:   161 line(s)
  audio:     148 packet(s), 14.8 s decoded, 0 decode error(s)
  sent:      get id, set protocol rcs, post::heartbeat::…, post::check::cantune
  playout:   0.68 s buffered, 0 discarded on overflow, 7680 skipped by the drain, 3 underrun(s)
```

The status line repeats every 5 s:

| Field | Meaning |
|---|---|
| `[ 10s]` | wall clock since start |
| `9.6s` | decoded audio so far, cumulative across reconnects |
| `96 pkt` | audio packets received |
| `5.0 frames/pkt` | Speex frames per packet; live v0.7 servers send five 20 ms frames |
| `##...` | received level (RMS of the last packet, 0–1 scaled) |
| `buf 0.86s` | playout depth: what is decoded but not yet heard |
| `5s left` | remaining budget |

Lines from the server are printed as they arrive: `[chat]` for chat, and `[info]`, `[auth]`,
`[warning]`, `[error]` for the server's own messages. One of those is worth knowing about —
a host can close the radio to guests and say why:

```
[warning] The remote closed: Guests are not allowed, please login with your RemoteHams.com account.
```

A guest that ignores the closure message would just sound silent, so the reason is shown
once per session (the server repeats it; a repeat is not new information). Whether audio
keeps flowing after that is the server's decision — some hosts keep streaming metering and
audio, some stop — so the client stays connected and lets you decide.

`sent:` is the complete list of what went on the wire. That list is the promise: `get id`,
one `set protocol rcs`, the two control keepalives, and the `IMA` liveness packet on the audio
socket. Nothing else is representable.

Exit code is `0` when the last session latched a state snapshot and did not end on a link
loss, `1` otherwise, `2` for a usage or lookup error.

## 6. Keep a copy

```bash
rcforb-guest --join 175 --seconds 300 --record rx.wav
```

The WAV is 8 kHz mono 16-bit, written alongside playback from the same decoded samples. The
header is patched when the process exits, so a `Ctrl-C` still leaves a playable file. If you
want only the file and no sound, add `--no-playback` (useful when capturing what the original
client plays on the same machine).

## 7. Survive a server-side close

Some hosts end a guest session on their own schedule — one public K3 closes both sockets at
exactly 15.0 s, no matter how the client behaves. That is a policy, not a fault.

```bash
rcforb-guest --join 175 --seconds 120 --reconnect
```

* Off by default: a client that reconnects on its own argues with the owner's policy.
* When on, a **link end** is retried after 1 s until `--seconds` is used up. A refusal, a bad
  name or a user `Ctrl-C` is never retried.
* Each attempt is a full new session: new sockets, new snapshot, empty playout buffer.
  Nothing is resumed, because the protocol has nothing to resume.
* The recording stays open across attempts, so you get one continuous file and one header.

## 8. Choose and tune the audio output

```bash
rcforb-guest --list-devices
rcforb-guest --join 175 --audio-device "SoundSticks"
```

`--audio-device` matches a substring of the device name; without it the system default is
used. If no device opens, the client says so and keeps decoding (so `--record` still works).

```bash
rcforb-guest --join 175 --seconds 300 --buffer 800
```

`--buffer` is the playout target in milliseconds, 100–1000, default 300. It is the amount of
audio collected before playback starts:

* **Choppy, gaps, or the level meter stutters** — the link delivers bursts. Raise it to
  600–800.
* **Everything sounds late** — lower it toward 300, at the cost of tolerating less jitter.

Independently of the target, the ring holds at most 2 s, discards the oldest audio if a burst
overflows it, and drains back to the target by dropping one 20 ms frame in thirteen when the
server runs fast. Depth and drain/skip counters are in the summary.

## 9. Check it against the original client

The comparison this project is built on: run the original under Wine and this client against
the same remote, and confirm they see, and hear, the same thing. Do it in this order so the
captures do not overlap.

1. **Start `RCForbClient.exe` under Wine** and connect to the remote. Note what it displays:
   radio name and driver, frequency, mode, band, S-meter, and the chat lines that arrive.
2. **Capture what the original plays**, while it is connected:
   ```bash
   sink=$(pactl get-default-sink)
   parec -d "$sink.monitor" --rate=48000 --channels=2 --format=s16le > original.raw
   # ... let it run, then stop with Ctrl-C
   ```
3. **Disconnect the original**, then run this client against the same host:
   ```bash
   rcforb-guest --join 175 --seconds 30 --record ours.wav --no-playback
   ```
4. **Compare state**: the `[state]` block must match what the original showed for radio,
   frequency, mode, band and S-meter. If the original shows a key the guest does not, that
   key is missing from the state model — worth reporting.
5. **Compare audio**: both captures are the same station. Convert or measure them, for
   example:
   ```bash
   ffmpeg -f s16le -ar 48000 -ac 2 -i original.raw original.wav
   ffmpeg -i original.wav -ac 1 -ar 8000 original8k.wav
   python3 - <<'PY'
   import wave, math, array
   for path in ("original8k.wav", "ours.wav"):
       w = wave.open(path); p = array.array("h", w.readframes(w.getnframes()))
       rms = math.sqrt(sum(x*x for x in p) / max(len(p), 1))
       print(f"{path:16} {w.getframerate()} Hz {w.getnframes()/w.getframerate():6.1f} s  rms {rms:7.0f}")
   PY
   ```
   Levels should be in the same range and the audio recognisably the same signal. Exact
   sample equality will not hold: the two sessions start at different moments, and Speex
   decoders differ in concealment and rounding.
6. **Compare the wire**, if you want byte-level evidence: capture the session with
   `sudo tcpdump -i any -s0 -w guest.pcap 'host 50.196.72.195'` and confirm the control
   stream contains no `login` line and no `post::request`, and that the audio socket carries
   only the small `IMA` packets from the client.

## 10. When it does not work

| Symptom | Cause | What to do |
|---|---|---|
| `state: unverified (no marker)`, audio still plays | Server sent no `chat::` or `state-posted` line within 15 s | Nothing is broken; the session is live but unconfirmed. Try another host if the panels stay empty |
| Session ends at exactly 15 s every time | Host-side guest limit | Add `--reconnect`, or pick another remote |
| `playback: unavailable (…)` | No output device, or the audio backend could not load | Listen with `--no-playback --record rx.wav`, or fix the device |
| `error: no playback device matches 'x'` | Substring matched nothing | `--list-devices` and copy a name fragment |
| Choppy audio, `underrun(s)` climbing | Link delivers audio in bursts | `--buffer 800`, or a nearer remote |
| Audio plays but the level bar stays flat | The station is quiet or squelched; some hosts stream silence | Tune-in is not available to a guest; try another remote |
| `error: … advertises no audio port, which selects the single-socket v0.9 path` | That remote is not `v0.7` | Pick a `v0.7` remote from `--list` |
| `error: could not read the remote list` | The public feed is unreachable | Use `--host <addr>` directly |
| `error: '…' is listed as offline` | The owner's entry is stale | Try another remote |
| `[warning] The remote closed: Guests are not allowed…` | The host does not accept anonymous listeners | Pick a remote whose `listen` column says `public`, or ask the owner for access |

## 11. Limits and etiquette

* A guest can listen to whatever the server chooses to push. It cannot tune, change mode,
  request a memory, key a transmitter or send chat. Those are not disabled controls; they do
  not exist in the session type.
* The server may restrict volume, drop the session, or refuse guests entirely. Its own text
  is shown verbatim rather than reinterpreted.
* Listen to remotes that publish an open policy or that you have asked to use. Do not leave
  `--reconnect` running against a host that is clearly closing you on purpose.
* Nothing is uploaded anywhere. The only state kept locally is the optional WAV file.

## 12. The same thing with a window

Everything above has a desktop equivalent in `src/RCForb.Gui`. It is a view over the same
`GuestSession`, so the wire behaviour is identical and the `sent:` promise still holds.

```bash
dotnet run --project src/RCForb.Gui
dotnet run --project src/RCForb.Gui -- --host 83.150.196.251 --record rx.wav
```

| Area | What it shows |
|---|---|
| Top bar | `LISTEN ONLY` badge, connection state (`idle`, `connecting`, `live`, `unverified`, `link lost`), target, elapsed time, opt-in Reconnect, Playback, Record to WAV |
| Left rail | the directory feed with a filter and a refresh button, plus a manual host/port/port entry for remotes that are not listed |
| Centre | frequency, mode and filter, band, radio and driver, S-meter bar and raw value, VFO B, and any other state keys the server publishes |
| Right | chat (read-only, wrapped, follows the newest line) and a timestamped notices log |
| Bottom | audio level, playout depth, underruns, packets, decoded seconds, decode errors, control lines, output device, buffer target |

Two behaviours are worth knowing because they differ from a naive build of the same window:

* **It keeps up with the server.** The S-meter and frequency are re-read whenever the state
  store changes, not once at connect; the server keeps publishing both after the snapshot.
* **Disconnecting finalises the recording.** Sinks are disposed when the session ends, so a
  `--record` WAV is playable the moment you press Disconnect rather than when the window
  closes. The same dispose closes the audio device.

It has no login form, no PTT, no tune or memory controls and no chat input. Those are not
greyed out — they do not exist in the window, because they do not exist in the session type
underneath it.
