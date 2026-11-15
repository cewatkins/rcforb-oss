using RCForb.Protocol;
using RCForb.Protocol.V09;

namespace RCForb.Protocol.Tests;

/// <summary>
/// v0.9 datagram framing. Every test names the SPEC requirement it demonstrates
/// (docs/spec/02-transport.md).
/// </summary>
public class V09FramingTests
{
    [Fact] // SPEC-TRAN-019: the marker introduces UTF-8, with no terminator.
    public void TextFrame_encodes_marker_and_payload_only()
    {
        var frame = TextFrame.Encode("radio::power::1");

        Assert.Equal(WireConstants.TextMarker, frame[0]);
        Assert.Equal("radio::power::1", System.Text.Encoding.UTF8.GetString(frame, 1, frame.Length - 1));
        Assert.DoesNotContain((byte)'\n', frame);
    }

    [Fact]
    public void TextFrame_roundtrips_a_payload_containing_a_line_feed()
    {
        const string command = "radio::message::CW::CQ\nTEST";

        var frame = TextFrame.Encode(command);

        Assert.True(TextFrame.TryDecode(frame, out var decoded));
        Assert.Equal(command, decoded);
    }

    [Fact] // A payload is one command however long it is, so an overlarge one is refused.
    public void TextFrame_refuses_a_payload_that_cannot_fit_one_datagram()
    {
        var tooLong = new string('x', TextFrame.MaxCommandBytes + 1);

        Assert.Throws<ArgumentException>(() => TextFrame.Encode(tooLong));
    }

    [Fact]
    public void TextFrame_rejects_invalid_utf8_instead_of_substituting()
    {
        byte[] frame = [WireConstants.TextMarker, 0xC3, 0x28];

        Assert.False(TextFrame.TryDecode(frame, out _));
    }

    [Fact]
    public void TextFrame_treats_an_empty_payload_as_a_legal_frame()
    {
        byte[] frame = [WireConstants.TextMarker];

        Assert.True(TextFrame.TryDecode(frame, out var command));
        Assert.Equal(string.Empty, command);
    }

    [Fact] // SPEC 02 §3.1: a zero-length datagram is ignored, not decoded as an empty command.
    public void Classifier_ignores_a_zero_length_datagram()
    {
        var datagram = V09DatagramClassifier.Classify(ReadOnlyMemory<byte>.Empty);

        Assert.Equal(V09DatagramKind.Empty, datagram.Kind);
    }

    [Fact] // SPEC-TRAN-020: one byte is always control, even when it is the text marker.
    public void Classifier_treats_every_single_byte_as_control()
    {
        foreach (var value in new byte[] { 0x00, 0x03, 0x05, 0x06, 0xFD, 0xFE, 0xFF })
        {
            var datagram = V09DatagramClassifier.Classify(new[] { value });

            Assert.Equal(V09DatagramKind.Control, datagram.Kind);
            Assert.Equal(value, datagram.Control);
        }
    }

    [Fact] // SPEC-TRAN-020: a multi-byte datagram starting 0x03 is audio, not a grant.
    public void Classifier_routes_a_multi_byte_0x03_datagram_to_audio()
    {
        byte[] frame = [0x03, 0x11, 0x22, 0x33];

        var datagram = V09DatagramClassifier.Classify(frame);

        Assert.Equal(V09DatagramKind.Audio, datagram.Kind);
        Assert.Equal(4, datagram.Payload.Length);
        Assert.False(datagram.IsTransmitGrant);
    }

    [Fact]
    public void Classifier_exposes_the_transmit_grant_and_revocation()
    {
        Assert.True(V09DatagramClassifier.Classify(new byte[] { WireConstants.TransmitPermitted }).IsTransmitGrant);
        Assert.True(V09DatagramClassifier.Classify(new byte[] { WireConstants.TransmitRevoked }).IsTransmitRevocation);
    }

    [Fact]
    public void Classifier_decodes_a_text_frame_and_strips_the_marker()
    {
        var datagram = V09DatagramClassifier.Classify(TextFrame.Encode("get id"));

        Assert.Equal(V09DatagramKind.Text, datagram.Kind);
        Assert.Equal("get id", System.Text.Encoding.UTF8.GetString(datagram.Payload.Span));
    }

    [Theory] // SPEC 02 §3.3
    [InlineData(0x08, 2, true)]
    [InlineData(0xFA, 2, true)]
    [InlineData(0x50, 200, true)]
    [InlineData(0x07, 2, true)]
    [InlineData(0x07, 1, false)]
    [InlineData(0x00, 1, false)]
    [InlineData(0xFF, 20, false)]
    [InlineData(0xFB, 20, false)]
    public void Opus_gate_enforces_the_first_byte_window(byte first, int length, bool expected)
    {
        var frame = new byte[length];
        frame[0] = first;

        Assert.Equal(expected, OpusFrameGate.IsSendable(frame));
    }

    [Fact]
    public void Opus_gate_rejects_an_empty_frame()
    {
        Assert.False(OpusFrameGate.IsSendable(ReadOnlySpan<byte>.Empty));
    }
}
