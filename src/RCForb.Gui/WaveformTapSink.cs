using System;
using System.Threading;
using RCForb.Client.Audio;

namespace RCForb.Gui;

/// <summary>
/// A GUI-local audio sink that keeps the most recent decoded samples in a fixed ring buffer.
/// Written on the session's audio pump thread and read on the UI thread.
/// </summary>
public sealed class WaveformTapSink : IGuestAudioSink, IDisposable
{
    // 8000 Hz mono => 2048 samples is 256 ms, a comfortable oscilloscope sweep.
    public const int Capacity = 2048;
    public const int SampleRate = 8000;

    private readonly short[] _ring = new short[Capacity];

    // Only touched with Interlocked.
    private long _writeCount;

    private bool _disposed;

    public void Write(ReadOnlySpan<short> samples)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (samples.IsEmpty)
        {
            return;
        }

        var count = samples.Length;
        var written = Interlocked.Read(ref _writeCount);

        // Write in two chunks if the span wraps around the ring end.
        var startIndex = (int)(written % Capacity);
        var firstChunk = Math.Min(count, Capacity - startIndex);

        samples.Slice(0, firstChunk).CopyTo(_ring.AsSpan(startIndex, firstChunk));
        if (firstChunk < count)
        {
            samples.Slice(firstChunk, count - firstChunk).CopyTo(_ring.AsSpan(0, count - firstChunk));
        }

        Interlocked.Add(ref _writeCount, count);
    }

    /// <summary>
    /// Copies the most recent <paramref name="length"/> samples into <paramref name="target"/>.
    /// Returns the number of samples written. No allocation.
    /// </summary>
    public int ReadRecent(Span<short> target, int length)
    {
        if (length <= 0 || target.IsEmpty)
        {
            return 0;
        }

        length = Math.Min(Math.Min(length, Capacity), target.Length);
        var written = Interlocked.Read(ref _writeCount);
        var available = (int)Math.Min(written, Capacity);

        if (available == 0)
        {
            return 0;
        }

        length = Math.Min(length, available);

        var endIndex = (int)((written - 1) % Capacity);
        for (var i = 0; i < length; i++)
        {
            var ringIndex = (Capacity + endIndex - (length - 1 - i)) % Capacity;
            target[i] = _ring[ringIndex];
        }

        return length;
    }

    public void Dispose()
    {
        _disposed = true;
    }
}
