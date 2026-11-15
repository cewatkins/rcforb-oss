namespace RCForb.Client.Audio;

/// <summary>
/// Where a guest session sends decoded receive audio.
/// </summary>
/// <remarks>
/// The session writes synchronously from its audio pump and never disposes the sink, so the
/// owner can keep one sink (a recorder, a player or both) alive across several sessions —
/// which is what a reconnect loop needs. Implementations must be safe to call from that one
/// pump thread and must not block on a device: buffering is the sink's problem.
/// </remarks>
public interface IGuestAudioSink
{
    /// <summary>Accepts 8 kHz mono 16-bit PCM, in the order it was decoded.</summary>
    void Write(ReadOnlySpan<short> samples);
}

/// <summary>Fans one stream out to several sinks, in order.</summary>
public sealed class CompositeAudioSink : IGuestAudioSink, IDisposable
{
    private readonly IGuestAudioSink[] _sinks;
    private bool _disposed;

    public CompositeAudioSink(params IGuestAudioSink[] sinks)
    {
        ArgumentNullException.ThrowIfNull(sinks);
        if (sinks.Length == 0)
        {
            throw new ArgumentException("at least one sink is required", nameof(sinks));
        }

        _sinks = sinks;
    }

    public IReadOnlyList<IGuestAudioSink> Sinks => _sinks;

    public void Write(ReadOnlySpan<short> samples)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        foreach (var sink in _sinks)
        {
            sink.Write(samples);
        }
    }

    /// <summary>
    /// Disposes every sink that is disposable. A failing sink does not prevent the others
    /// from being released, and disposal errors are swallowed: they cannot be acted on once
    /// the stream is over.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var sink in _sinks)
        {
            if (sink is not IDisposable disposable)
            {
                continue;
            }

            try
            {
                disposable.Dispose();
            }
            catch (Exception)
            {
                // A recorder refusing to patch its header must not keep a device open.
            }
        }
    }
}
