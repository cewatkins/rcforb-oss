namespace RCForb.Audio;

/// <summary>
/// Bounded single-channel jitter buffer between the decoded receive stream and the playback
/// callback (SPEC 06 §3).
/// </summary>
/// <remarks>
/// The network pump only ever appends and the audio thread only ever consumes, so both sides
/// take a short lock and neither blocks on the other. Overflow discards the oldest audio
/// (SPEC-AUD-028) instead of growing latency or stalling the decoder. Playback starts only
/// once the target depth has been reached (SPEC-AUD-029).
/// </remarks>
public sealed class PlayoutBuffer
{
    private readonly object _gate = new();
    private readonly float[] _ring;
    private readonly int _sampleRate;
    private readonly int _targetSamples;
    private readonly int _frameSamples;
    private readonly int _overloadSamples;

    private int _read;
    private int _count;
    private int _frameIndex;
    private bool _priming;
    private bool _overload;
    private bool _starved;
    private long _written;
    private long _dropped;
    private long _decimated;
    private long _underruns;

    /// <param name="sampleRate">Sample rate of the PCM written to the buffer.</param>
    /// <param name="capacity">Ring capacity; at least two seconds per SPEC-AUD-028.</param>
    /// <param name="target">Depth at which playback may start, 300 ms by default.</param>
    public PlayoutBuffer(int sampleRate, TimeSpan capacity, TimeSpan target)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);

        _sampleRate = sampleRate;
        _ring = new float[Math.Max(1, (int)Math.Ceiling(sampleRate * capacity.TotalSeconds))];
        _targetSamples = Math.Clamp((int)Math.Ceiling(sampleRate * target.TotalSeconds), 0, _ring.Length);
        _frameSamples = Math.Max(1, sampleRate / 50);
        _overloadSamples = Math.Max(sampleRate * 150 / 1000, _targetSamples);
        _priming = _targetSamples > 0;
    }

    public int SampleRate => _sampleRate;

    public int CapacitySamples => _ring.Length;

    public int TargetSamples => _targetSamples;

    /// <summary>False until the target depth has been reached once.</summary>
    public bool IsPrimed
    {
        get
        {
            lock (_gate)
            {
                return !_priming;
            }
        }
    }

    public int BufferedSamples
    {
        get
        {
            lock (_gate)
            {
                return _count;
            }
        }
    }

    /// <summary>Current depth in seconds, as reported to the operator (SPEC-AUD-036).</summary>
    public double DepthSeconds => (double)BufferedSamples / _sampleRate;

    /// <summary>Total samples appended, including any later dropped.</summary>
    public long WrittenSamples => Interlocked.Read(ref _written);

    /// <summary>Samples discarded from the front because the ring was full.</summary>
    public long DroppedSamples => Interlocked.Read(ref _dropped);

    /// <summary>Times the ring ran dry after priming, counted once per dry episode.</summary>
    public long Underruns => Interlocked.Read(ref _underruns);

    /// <summary>Frames discarded by the overload drain rather than by overflow.</summary>
    public long DecimatedSamples => Interlocked.Read(ref _decimated);

    /// <summary>True while the overload drain is discarding one frame in thirteen.</summary>
    public bool IsDraining
    {
        get
        {
            lock (_gate)
            {
                return _overload;
            }
        }
    }

    /// <summary>Appends decoded PCM, converting to float and dropping the oldest on overflow.</summary>
    public void Write(ReadOnlySpan<short> samples)
    {
        if (samples.IsEmpty)
        {
            return;
        }

        lock (_gate)
        {
            if (_overload)
            {
                if (_count <= _targetSamples)
                {
                    _overload = false;
                }
            }
            else if (_count >= _overloadSamples)
            {
                _overload = true;
            }

            var offset = 0;
            while (offset < samples.Length)
            {
                var length = Math.Min(_frameSamples, samples.Length - offset);
                _frameIndex++;

                // SPEC-AUD-032: while draining, discard one 20 ms frame in thirteen. That
                // removes ~7.7 % of the stream, enough to walk a server burst back down to
                // the target without an audible cut.
                if (_overload && _frameIndex % 13 == 0)
                {
                    Interlocked.Add(ref _decimated, length);
                    offset += length;
                    continue;
                }

                Append(samples.Slice(offset, length));
                offset += length;
            }
        }

        Interlocked.Add(ref _written, samples.Length);
    }

    private void Append(ReadOnlySpan<short> samples)
    {
        var overflow = _count + samples.Length - _ring.Length;
        if (overflow > 0)
        {
            DiscardOldest(overflow);
            Interlocked.Add(ref _dropped, overflow);
        }

        var write = (_read + _count) % _ring.Length;
        var first = Math.Min(samples.Length, _ring.Length - write);
        for (var i = 0; i < first; i++)
        {
            _ring[write + i] = samples[i] / 32768f;
        }

        for (var i = first; i < samples.Length; i++)
        {
            _ring[i - first] = samples[i] / 32768f;
        }

        _count += samples.Length;
    }

    /// <summary>
    /// Fills <paramref name="destination"/> with buffered audio. While priming, or when the ring
    /// is empty, the destination is silence and 0 is returned.
    /// </summary>
    public int Read(Span<float> destination)
    {
        if (destination.IsEmpty)
        {
            return 0;
        }

        lock (_gate)
        {
            if (_priming)
            {
                if (_count < _targetSamples)
                {
                    destination.Clear();
                    return 0;
                }

                _priming = false;
            }

            if (_count == 0)
            {
                if (!_starved)
                {
                    _starved = true;
                    Interlocked.Increment(ref _underruns);
                }

                destination.Clear();
                return 0;
            }

            var take = Math.Min(destination.Length, _count);
            var first = Math.Min(take, _ring.Length - _read);
            _ring.AsSpan(_read, first).CopyTo(destination);
            if (take > first)
            {
                _ring.AsSpan(0, take - first).CopyTo(destination[first..]);
            }

            _read = (_read + take) % _ring.Length;
            _count -= take;
            _starved = false;
            destination[take..].Clear();
            return take;
        }
    }

    /// <summary>Discards everything buffered and arms the start-at-target rule again.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _read = 0;
            _count = 0;
            _frameIndex = 0;
            _overload = false;
            _starved = false;
            _priming = _targetSamples > 0;
        }
    }

    private void DiscardOldest(int count)
    {
        _read = (_read + count) % _ring.Length;
        _count -= count;
    }
}
