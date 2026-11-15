using RCForb.Client.Audio;
using SoundFlow.Abstracts;
using SoundFlow.Abstracts.Devices;
using SoundFlow.Backends.MiniAudio;
using SoundFlow.Backends.MiniAudio.Devices;
using SoundFlow.Enums;
using SoundFlow.Structs;

namespace RCForb.Audio;

/// <summary>
/// Plays the 8 kHz mono receive stream through the system default output device.
/// </summary>
/// <remarks>
/// SoundFlow's miniaudio backend converts 8 kHz mono to whatever the device runs at, so no
/// resampling is done here. Decoded PCM lands in a <see cref="PlayoutBuffer"/> and a
/// <see cref="SoundComponent"/> drains it from the audio callback; the device therefore never
/// sees a blocking read and the network pump never sees a device.
/// <para>
/// Starting and stopping the device on underrun (SPEC-AUD-031) is deliberately not implemented:
/// an idle device that emits silence is indistinguishable to the listener, and pausing around a
/// 150 ms gap costs a device restart per late packet. See docs/spec/06-audio.md §3.1.
/// </para>
/// </remarks>
public sealed class SoundFlowAudioSink : IGuestAudioSink, IDisposable
{
    public const int DefaultSampleRate = 8000;

    public static readonly TimeSpan DefaultTargetLatency = TimeSpan.FromMilliseconds(300);
    public static readonly TimeSpan DefaultCapacity = TimeSpan.FromSeconds(2);

    private readonly MiniAudioEngine _engine;
    private readonly AudioPlaybackDevice _device;
    private readonly PlayoutComponent _component;
    private bool _disposed;

    private SoundFlowAudioSink(
        string? deviceName,
        int sampleRate,
        TimeSpan targetLatency,
        TimeSpan capacity)
    {
        Buffer = new PlayoutBuffer(sampleRate, capacity, targetLatency);

        var format = new AudioFormat
        {
            Format = SampleFormat.F32,
            Channels = 1,
            Layout = ChannelLayout.Mono,
            SampleRate = sampleRate,
        };

        MiniAudioEngine? engine = null;
        try
        {
            engine = new MiniAudioEngine();

            DeviceInfo? info = null;
            if (deviceName is { Length: > 0 })
            {
                info = FindDevice(engine, deviceName);
            }

            // 20 ms periods, two of them: small enough that a stop is immediate, large enough
            // not to wake the CPU 500 times a second.
            _device = engine.InitializePlaybackDevice(
                info,
                format,
                new MiniAudioDeviceConfig { PeriodSizeInMilliseconds = 20, Periods = 2 });

            _component = new PlayoutComponent(engine, format, Buffer);
            _device.MasterMixer.AddComponent(_component);
            _device.Start();
        }
        catch
        {
            engine?.Dispose();
            throw;
        }

        _engine = engine!;
        DeviceName = _device.Info?.Name is { Length: > 0 } name ? name : "default device";
    }

    /// <summary>The jitter buffer feeding the device; useful for status output.</summary>
    public PlayoutBuffer Buffer { get; }

    public string DeviceName { get; }

    /// <summary>Alias so callers can report the negotiated rate without reaching into the engine.</summary>
    public int SampleRate => Buffer.SampleRate;

    /// <summary>
    /// Opens the playback device, returning <c>null</c> instead of throwing when the host has no
    /// usable output (headless server, missing native library, busy device).
    /// </summary>
    public static SoundFlowAudioSink? TryStart(
        string? deviceName,
        out string? error,
        int sampleRate = DefaultSampleRate,
        TimeSpan? targetLatency = null,
        TimeSpan? capacity = null)
    {
        try
        {
            var sink = new SoundFlowAudioSink(
                deviceName,
                sampleRate,
                targetLatency ?? DefaultTargetLatency,
                capacity ?? DefaultCapacity);
            error = null;
            return sink;
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            return null;
        }
    }

    /// <summary>Names of the playback devices miniaudio can see, default first.</summary>
    public static IReadOnlyList<string> ListDevices()
    {
        try
        {
            using var engine = new MiniAudioEngine();
            return engine.PlaybackDevices
                .Where(device => device.Name is { Length: > 0 })
                .Select(device => device.Name + (device.IsDefault ? " (default)" : string.Empty))
                .ToArray();
        }
        catch (Exception)
        {
            return [];
        }
    }

    public void Write(ReadOnlySpan<short> samples) => Buffer.Write(samples);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Each step is independent: a device that fails to stop must not keep the engine's
        // native context alive for the rest of the process.
        try
        {
            _component.Dispose();
        }
        catch (Exception)
        {
        }

        try
        {
            _device.Stop();
        }
        catch (Exception)
        {
        }

        try
        {
            _device.Dispose();
        }
        catch (Exception)
        {
        }

        try
        {
            _engine.Dispose();
        }
        catch (Exception)
        {
        }
    }

    private static DeviceInfo FindDevice(MiniAudioEngine engine, string deviceName)
    {
        foreach (var device in engine.PlaybackDevices)
        {
            if (device.Name is { Length: > 0 } name &&
                name.Contains(deviceName, StringComparison.OrdinalIgnoreCase))
            {
                return device;
            }
        }

        throw new InvalidOperationException(
            $"no playback device matches '{deviceName}'; available: " +
            string.Join(", ", engine.PlaybackDevices.Select(device => device.Name)));
    }

    /// <summary>Drains the playout buffer into the mixer.</summary>
    private sealed class PlayoutComponent : SoundComponent
    {
        private readonly PlayoutBuffer _buffer;

        public PlayoutComponent(AudioEngine engine, AudioFormat format, PlayoutBuffer buffer)
            : base(engine, format)
        {
            _buffer = buffer;
            Name = "RCForb receive";
        }

        protected override void GenerateAudio(Span<float> buffer, int channels) => _buffer.Read(buffer);
    }
}
