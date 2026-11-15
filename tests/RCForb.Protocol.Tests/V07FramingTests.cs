using System.Buffers.Binary;
using RCForb.Protocol.V07;

namespace RCForb.Protocol.Tests;

/// <summary>
/// v0.7 audio-stream framing and the session packet. Every test names the SPEC requirement
/// it demonstrates (docs/spec/02-transport.md §4).
/// </summary>
public class V07FramingTests
{
    private static byte[] DataPacket(int typeFlag, byte[] payload)
    {
        var packet = new byte[10 + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(packet, payload.Length);
        packet[4] = (byte)typeFlag;
        payload.CopyTo(packet, 10);
        return packet;
    }

    [Fact] // SPEC-TRAN-031/032: header layout, audio routed by length.
    public void Reader_extracts_an_audio_frame()
    {
        var payload = Enumerable.Range(0, 40).Select(i => (byte)i).ToArray();
        var reader = new V07FrameReader();

        reader.Append(DataPacket(2, payload));

        Assert.Equal(V07ReadResult.Frame, reader.TryRead(out var frame));
        Assert.Equal(V07FrameKind.Audio, frame.Kind);
        Assert.Equal(payload, frame.Payload.ToArray());
        Assert.False(frame.TransmitterKeyed);
    }

    [Fact] // SPEC-TRAN-033: byte 4 == 2 on a short packet means keyed, and only that.
    public void Reader_reports_a_keyed_control_packet()
    {
        var reader = new V07FrameReader();
        reader.Append(DataPacket(2, "IMA"u8.ToArray()));

        Assert.Equal(V07ReadResult.Frame, reader.TryRead(out var frame));
        Assert.Equal(V07FrameKind.Control, frame.Kind);
        Assert.True(frame.TransmitterKeyed);
        Assert.Equal("IMA", System.Text.Encoding.ASCII.GetString(frame.Payload.Span));
    }

    [Fact] // SPEC-TRAN-033: a 40-byte payload with the keyed flag is audio, not control.
    public void Reader_does_not_route_on_the_type_byte()
    {
        var reader = new V07FrameReader();
        reader.Append(DataPacket(2, new byte[40]));

        Assert.Equal(V07ReadResult.Frame, reader.TryRead(out var frame));
        Assert.Equal(V07FrameKind.Audio, frame.Kind);
    }

    [Fact] // SPEC-TRAN-042: a packet delivered one byte at a time still parses.
    public void Reader_waits_for_a_complete_payload_across_fragments()
    {
        var reader = new V07FrameReader();
        var packet = DataPacket(2, new byte[60]);

        for (var i = 0; i < packet.Length - 1; i++)
        {
            reader.Append(packet.AsSpan(i, 1));
            Assert.Equal(V07ReadResult.NeedMoreData, reader.TryRead(out _));
        }

        reader.Append(packet.AsSpan(packet.Length - 1, 1));
        Assert.Equal(V07ReadResult.Frame, reader.TryRead(out var frame));
        Assert.Equal(60, frame.Length);
        Assert.Equal(0, reader.BufferedBytes);
    }

    [Fact] // SPEC-TRAN-035: 19 is unrepresentable, so the reader resynchronises past it.
    public void Reader_resynchronises_past_a_19_byte_payload_and_recovers()
    {
        var reader = new V07FrameReader();
        var reserved = DataPacket(2, Enumerable.Repeat((byte)0x5A, 19).ToArray());
        var next = DataPacket(2, Enumerable.Repeat((byte)0x33, 40).ToArray());
        reader.Append(reserved);
        reader.Append(next);

        // Recovery is destructive by design, so intermediate phantom frames are legal; what
        // must hold is that the stream is never desynchronised permanently and that no frame
        // of 19 bytes is ever delivered.
        var recovered = false;
        for (var attempt = 0; attempt < 64 && reader.BufferedBytes > 0; attempt++)
        {
            if (reader.TryRead(out var frame) != V07ReadResult.Frame)
            {
                break;
            }

            Assert.NotEqual(19, frame.Length);
            if (frame.Kind == V07FrameKind.Audio && frame.Length == 40)
            {
                recovered = true;
                break;
            }
        }

        Assert.True(reader.ResynchronisedBytes > 0);
        Assert.True(recovered, "the valid packet that followed a reserved length was never recovered");
    }

    [Fact] // SPEC-TRAN-043/044: zero padding is discarded, never delivered as audio.
    public void Reader_discards_zero_padding_runs()
    {
        var reader = new V07FrameReader();
        reader.Append(new byte[32]);
        reader.Append(DataPacket(2, new byte[40]));

        Assert.Equal(V07ReadResult.Frame, reader.TryRead(out var frame));
        Assert.Equal(V07FrameKind.Audio, frame.Kind);
        Assert.Equal(32, reader.PaddingBytes);
    }

    [Fact] // SPEC-TRAN-047 adjacent: an impossible length is rejected and the stream recovers.
    public void Reader_recovers_from_an_out_of_range_length()
    {
        var reader = new V07FrameReader();
        var bogus = DataPacket(2, new byte[3]);
        BinaryPrimitives.WriteInt32LittleEndian(bogus, 2048);
        reader.Append(bogus);
        reader.Append(DataPacket(2, new byte[40]));

        Assert.Equal(V07ReadResult.Frame, reader.TryRead(out var frame));
        Assert.Equal(40, frame.Length);
    }

    [Fact] // SPEC-TRAN-036: length 54 is the session magic and consumes 8 + its own length.
    public void Reader_extracts_a_session_packet()
    {
        var packet = V07SessionPacket.Build("N0CALL", new string('a', 32));
        var reader = new V07FrameReader();

        reader.Append(packet);

        Assert.Equal(V07ReadResult.Frame, reader.TryRead(out var frame));
        Assert.Equal(V07FrameKind.Session, frame.Kind);
        Assert.Equal("n0call," + new string('a', 32), System.Text.Encoding.ASCII.GetString(frame.Payload.Span));
    }

    [Fact] // SPEC-TRAN-038: always 64 bytes, magic 54, real length at offset 4, zero padding.
    public void Session_packet_has_the_fixed_shape()
    {
        var packet = V07SessionPacket.Build("N0CALL", new string('b', 32));

        Assert.Equal(V07SessionPacket.PacketBytes, packet.Length);
        Assert.Equal(V07SessionPacket.SessionMagic, BinaryPrimitives.ReadInt32LittleEndian(packet));
        Assert.Equal(6 + 1 + 32, BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(4)));
        Assert.All(packet.AsSpan(8 + 39).ToArray(), b => Assert.Equal(0, b));
    }

    [Fact] // SPEC-TRAN-040: a name that would truncate the digest is refused.
    public void Session_packet_refuses_a_name_that_would_truncate_the_digest()
    {
        var tooLong = new string('x', V07SessionPacket.MaxUserBytes + 1);

        Assert.Throws<ArgumentException>(() => V07SessionPacket.Build(tooLong, new string('c', 32)));
    }

    [Fact]
    public void Session_packet_roundtrips_and_lower_cases_the_name()
    {
        var packet = V07SessionPacket.Build("N0CALL", new string('d', 32));

        Assert.True(V07SessionPacket.TryParse(packet, out var user, out var digest));
        Assert.Equal("n0call", user);
        Assert.Equal(new string('d', 32), digest);
    }

    [Fact]
    public void Session_packet_rejects_a_comma_in_the_name_and_a_bad_digest()
    {
        Assert.Throws<ArgumentException>(() => V07SessionPacket.Build("N0,CALL", new string('e', 32)));
        Assert.Throws<ArgumentException>(() => V07SessionPacket.Build("N0CALL", "NOT-A-DIGEST"));
    }

    [Fact] // A truncated packet is never consumed.
    public void Reader_keeps_a_partial_session_packet()
    {
        var reader = new V07FrameReader();
        var packet = V07SessionPacket.Build("N0CALL", new string('f', 32));

        reader.Append(packet.AsSpan(0, 20));

        Assert.Equal(V07ReadResult.NeedMoreData, reader.TryRead(out _));
        Assert.Equal(20, reader.BufferedBytes);
    }
}
