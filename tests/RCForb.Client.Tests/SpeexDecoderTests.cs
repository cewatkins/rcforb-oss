using RCForb.Client.Audio;

namespace RCForb.Client.Tests;

/// <summary>
/// The v0.7 audio payload can hold several 20 ms Speex frames. A real server sends five
/// per packet; decoding only the first would throw away four fifths of the audio.
/// </summary>
public class SpeexDecoderTests
{
    [Fact] // SPEC-AUD-022: every frame in the payload is decoded, not just the first.
    public void One_payload_with_five_frames_yields_100_ms_of_pcm()
    {
        // A real capture of a five-frame server packet is 188 bytes; the packing is a single
        // bitstream, so the encoder must write the frames through one SpeexBits.
        var payload = SpeexTestPayload.Frames(5);

        using var decoder = new SpeexPcmDecoder();
        var output = new List<short>();

        var frames = decoder.Decode(payload, output);

        Assert.Equal(5, frames);
        Assert.Equal(800, output.Count);
        Assert.Equal(188, payload.Length);
        Assert.True(output.Max(s => Math.Abs((int)s)) > 0);
        Assert.Equal(0, decoder.DecodeErrors);
    }

    [Fact]
    public void A_single_frame_payload_yields_exactly_one_frame()
    {
        using var decoder = new SpeexPcmDecoder();
        var output = new List<short>();

        var frames = decoder.Decode(SpeexTestPayload.Frames(1), output);

        Assert.Equal(1, frames);
        Assert.Equal(SpeexPcmDecoder.SamplesPerFrame, output.Count);
    }

    [Fact]
    public void A_truncated_or_junk_payload_is_reported_not_played()
    {
        using var decoder = new SpeexPcmDecoder();
        var output = new List<short>();

        var frames = decoder.Decode(new byte[] { 0x01 }, output);

        Assert.Equal(0, frames);
        Assert.Empty(output);
    }

    [Fact] // A corrupt packet must not poison the stream: the next good one still decodes.
    public void The_decoder_recovers_after_a_corrupt_payload()
    {
        using var decoder = new SpeexPcmDecoder();
        var good = SpeexTestPayload.Frames(1);

        decoder.Decode(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }, new List<short>());
        var frames = decoder.Decode(good, new List<short>());

        Assert.Equal(1, frames);
    }
}
