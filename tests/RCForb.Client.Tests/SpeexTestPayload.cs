using SpeexSharp.Native;

namespace RCForb.Client.Tests;

/// <summary>
/// Builds the payload a real v0.7 server sends: several Speex frames packed into one
/// bitstream. Concatenating per-frame byte arrays is <em>not</em> the same thing — each
/// frame's trailing padding bits become the next frame's header, which a decoder reads as a
/// corrupt wideband stream (observed: two frames decode, then an error).
/// </summary>
/// <remarks>
/// This lives in the test project on purpose. The guest client is receive-only, so nothing
/// in <c>RCForb.Client</c> exposes an encoder.
/// </remarks>
internal static unsafe class SpeexTestPayload
{
    public const int FrameSamples = 160;
    public const int SampleRate = 8000;

    /// <summary>Packs <paramref name="frames"/> consecutive 20 ms frames into one payload.</summary>
    public static byte[] Frames(int frames, double frequency = 700, short amplitude = 10_000)
    {
        var pcm = new short[FrameSamples];
        var output = new byte[4096];
        var scratch = new byte[4096];

        var encoder = Speex.EncoderInit(Speex.NbMode);
        if (encoder == null)
        {
            throw new InvalidOperationException("libspeex refused to create a narrowband encoder.");
        }

        try
        {
            var quality = 8;
            Speex.EncoderCtl(encoder, (int)SetCoderParameter.Quality, &quality);

            fixed (byte* scratchPtr = scratch)
            {
                SpeexBits bits;
                Speex.BitsInitBuffer(&bits, scratchPtr, scratch.Length);

                for (var frame = 0; frame < frames; frame++)
                {
                    for (var i = 0; i < FrameSamples; i++)
                    {
                        pcm[i] = (short)(amplitude * Math.Sin(
                            2 * Math.PI * frequency * ((frame * FrameSamples) + i) / SampleRate));
                    }

                    fixed (short* samples = pcm)
                    {
                        Speex.EncodeInt(encoder, samples, &bits);
                    }
                }

                int written;
                fixed (byte* destination = output)
                {
                    written = Speex.BitsWrite(&bits, destination, output.Length);
                }

                return output.AsSpan(0, written).ToArray();
            }
        }
        finally
        {
            Speex.EncoderDestroy(encoder);
        }
    }
}
