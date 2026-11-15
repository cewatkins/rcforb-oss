using RCForb.Audio;
using RCForb.Client.Audio;

namespace RCForb.Client.Tests;

/// <summary>
/// The playout ring is the only part of the playback path with logic worth testing without a
/// sound card: start-at-target (SPEC-AUD-029), bounded overflow (SPEC-AUD-028) and silence on
/// underrun rather than growth.
/// </summary>
public class PlayoutBufferTests
{
    private const int Rate = 8000;

    private static PlayoutBuffer NewBuffer(int capacityMs = 2000, int targetMs = 300) =>
        new(Rate, TimeSpan.FromMilliseconds(capacityMs), TimeSpan.FromMilliseconds(targetMs));

    private static short[] Ramp(int count, short start = 0) =>
        Enumerable.Range(0, count).Select(i => (short)(start + i)).ToArray();

    [Fact]
    public void Playback_does_not_start_before_the_target_depth()
    {
        var buffer = NewBuffer();
        var output = new float[800];

        buffer.Write(Ramp(800, 100));
        Assert.Equal(0, buffer.Read(output));
        Assert.True(buffer.IsPrimed == false);
        Assert.All(output, sample => Assert.Equal(0f, sample));

        buffer.Write(Ramp(1600, 900));

        Assert.Equal(800, buffer.Read(output));
        Assert.True(buffer.IsPrimed);
        Assert.Equal(100f / 32768f, output[0], 6);
    }

    [Fact]
    public void Overflow_discards_the_oldest_audio_and_keeps_the_buffer_bounded()
    {
        var buffer = NewBuffer(capacityMs: 100, targetMs: 0);

        buffer.Write(Ramp(1200));

        Assert.Equal(800, buffer.BufferedSamples);
        Assert.Equal(400, buffer.DroppedSamples);
        Assert.Equal(800, buffer.CapacitySamples);

        var output = new float[800];
        Assert.Equal(800, buffer.Read(output));
        Assert.Equal(400f / 32768f, output[0], 6);
        Assert.Equal(1199f / 32768f, output[^1], 6);
    }

    [Fact]
    public void An_empty_ring_yields_silence_and_counts_an_underrun()
    {
        var buffer = NewBuffer(targetMs: 0);
        buffer.Write(Ramp(100));

        var output = new float[800];
        Assert.Equal(100, buffer.Read(output));
        Assert.Equal(0f, output[^1]);

        Assert.Equal(0, buffer.Read(output));
        Assert.All(output, sample => Assert.Equal(0f, sample));
        Assert.Equal(1, buffer.Underruns);
    }

    [Fact] // SPEC-AUD-032: a server burst is walked back down to the target, one frame in 13.
    public void The_overload_drain_removes_one_frame_in_thirteen_until_the_target_is_reached()
    {
        var buffer = NewBuffer(targetMs: 100);

        // 0.5 s of backlog: five times the target.
        buffer.Write(Ramp(4000, 1));
        Assert.Equal(4000, buffer.BufferedSamples);

        var output = new float[800];
        for (var tick = 0; tick < 400; tick++)
        {
            buffer.Write(Ramp(800, 1));
            buffer.Read(output);
        }

        Assert.True(buffer.DecimatedSamples > 0);
        Assert.True(buffer.BufferedSamples <= 800, $"depth stayed at {buffer.BufferedSamples} samples");

        // Steady state around the target: the latch releases instead of draining forever.
        buffer.Write(Ramp(1600, 1));
        buffer.Read(output);
        buffer.Read(output);
        Assert.False(buffer.IsDraining);
    }

    [Fact]
    public void Reset_rearms_the_start_at_target_rule()
    {
        var buffer = NewBuffer(targetMs: 100);
        buffer.Write(Ramp(800));
        Assert.Equal(800, buffer.Read(new float[800]));
        Assert.True(buffer.IsPrimed);

        buffer.Reset();

        Assert.False(buffer.IsPrimed);
        Assert.Equal(0, buffer.BufferedSamples);
        buffer.Write(Ramp(400));
        Assert.Equal(0, buffer.Read(new float[800]));
    }

    [Fact]
    public void A_composite_sink_fans_out_and_disposes_every_child()
    {
        var first = new Collector();
        var second = new Collector();
        var composite = new CompositeAudioSink(first, second);

        composite.Write(Ramp(10, 5));

        Assert.Equal(10, first.Samples.Count);
        Assert.Equal(5, first.Samples[0]);
        Assert.Equal(first.Samples, second.Samples);

        composite.Dispose();

        Assert.True(first.Disposed);
        Assert.True(second.Disposed);
        Assert.Throws<ObjectDisposedException>(() => composite.Write(Ramp(1)));
    }

    private sealed class Collector : IGuestAudioSink, IDisposable
    {
        public List<short> Samples { get; } = [];
        public bool Disposed { get; private set; }

        public void Write(ReadOnlySpan<short> samples) => Samples.AddRange(samples.ToArray());

        public void Dispose() => Disposed = true;
    }
}
