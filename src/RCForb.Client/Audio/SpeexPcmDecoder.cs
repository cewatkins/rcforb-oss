using SpeexSharp.Native;

namespace RCForb.Client.Audio;

/// <summary>
/// Speex narrowband receive decoder for the v0.7 audio payload.
/// </summary>
/// <remarks>
/// One payload can hold several 20 ms frames: a real server sends five (188 bytes, 100 ms)
/// per packet, and the reference client's decoder walks the whole bitstream. Decoding only
/// the first frame would play one fifth of the audio, so this decoder loops while at least
/// five bits remain, exactly as libspeex documents.
///
/// The decoder uses the native libspeex bundled by SpeexSharp. It is allocated once and
/// reused; an error destroys and recreates it because a desynchronised Speex state produces
/// persistent noise rather than a recoverable glitch (SPEC-AUD-021).
/// </remarks>
public sealed class SpeexPcmDecoder : IDisposable
{
    public const int SampleRate = 8000;
    public const int SamplesPerFrame = 160;
    public const int MaxPayloadBytes = 2047;

    private readonly byte[] _scratch = new byte[MaxPayloadBytes];
    private readonly short[] _frame = new short[SamplesPerFrame];

    private unsafe void* _state;
    private bool _disposed;

    public long FramesDecoded { get; private set; }
    public long DecodeErrors { get; private set; }
    public int Resets { get; private set; }

    public SpeexPcmDecoder()
    {
        unsafe
        {
            _state = Speex.DecoderInit(Speex.NbMode);
            if (_state == null)
            {
                throw new InvalidOperationException("libspeex refused to create a narrowband decoder.");
            }
        }
    }

    /// <summary>
    /// Decodes every complete frame in <paramref name="payload"/> and appends 16-bit mono PCM
    /// at <see cref="SampleRate"/> to <paramref name="pcm"/>. Returns the number of frames
    /// decoded; a corrupt stream yields a reset and zero for that payload.
    /// </summary>
    public unsafe int Decode(ReadOnlySpan<byte> payload, ICollection<short> pcm)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (payload.Length < 2 || payload.Length > MaxPayloadBytes)
        {
            return 0;
        }

        var frames = 0;

        fixed (byte* scratch = _scratch)
        fixed (byte* source = payload)
        fixed (short* frame = _frame)
        {
            SpeexBits bits;
            // The bit reader gets its own buffer: libspeex copies into it, and nothing is
            // allocated per packet. BitsInitBuffer marks the buffer as not owned, so there
            // is no matching destroy call and no leak.
            Speex.BitsInitBuffer(&bits, scratch, _scratch.Length);
            Speex.BitsReadFrom(&bits, source, payload.Length);

            while (Speex.BitsRemaining(&bits) > 5)
            {
                if (Speex.DecodeInt(_state, &bits, frame) != 0)
                {
                    DecodeErrors++;
                    Reset();
                    break;
                }

                for (var i = 0; i < SamplesPerFrame; i++)
                {
                    pcm.Add(frame[i]);
                }

                frames++;
                FramesDecoded++;
            }
        }

        return frames;
    }

    private unsafe void Reset()
    {
        if (_state != null)
        {
            Speex.DecoderDestroy(_state);
        }

        _state = Speex.DecoderInit(Speex.NbMode);
        Resets++;
    }

    public unsafe void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_state != null)
        {
            Speex.DecoderDestroy(_state);
            _state = null;
        }
    }
}
