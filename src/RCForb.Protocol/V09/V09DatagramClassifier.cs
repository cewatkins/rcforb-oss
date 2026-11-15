namespace RCForb.Protocol.V09;

/// <summary>Classification of one v0.9 datagram. See docs/spec/02-transport.md §3.1.</summary>
public enum V09DatagramKind
{
    /// <summary>Zero-length datagram: ignored.</summary>
    Empty,

    /// <summary>First byte 0xFF: one command string, no terminator.</summary>
    Text,

    /// <summary>Multi-byte datagram not starting with 0xFF: one codec frame.</summary>
    Audio,

    /// <summary>Exactly one byte: heartbeat, ping, permission, user-in/out or unknown.</summary>
    Control,
}

/// <summary>
/// One classified datagram. <see cref="Payload"/> is the command text for
/// <see cref="V09DatagramKind.Text"/> and the codec frame for <see cref="V09DatagramKind.Audio"/>;
/// it is empty for <see cref="V09DatagramKind.Control"/>, whose value is <see cref="Control"/>.
/// </summary>
public readonly record struct V09Datagram(V09DatagramKind Kind, byte Control, ReadOnlyMemory<byte> Payload)
{
    /// <summary>True when this datagram is a transmit grant (SPEC-TRAN-023).</summary>
    public bool IsTransmitGrant => Kind == V09DatagramKind.Control && Control == WireConstants.TransmitPermitted;

    /// <summary>True when this datagram revokes transmit (SPEC-TRAN-023).</summary>
    public bool IsTransmitRevocation => Kind == V09DatagramKind.Control && Control == WireConstants.TransmitRevoked;
}

/// <summary>
/// The v0.9 classifier. Length is tested before the first byte, so a multi-byte datagram
/// starting with 0x03 is audio, never a permission grant: SPEC-TRAN-020.
/// </summary>
public static class V09DatagramClassifier
{
    public static V09Datagram Classify(ReadOnlyMemory<byte> datagram)
    {
        if (datagram.Length == 0)
        {
            return new V09Datagram(V09DatagramKind.Empty, 0, ReadOnlyMemory<byte>.Empty);
        }

        var span = datagram.Span;

        if (span.Length == 1)
        {
            // One byte is never audio, so an encoder able to emit a single-byte frame
            // would be unrepresentable on this path. See SPEC 02 §3.1.
            return new V09Datagram(V09DatagramKind.Control, span[0], ReadOnlyMemory<byte>.Empty);
        }

        if (span[0] == WireConstants.TextMarker)
        {
            return new V09Datagram(V09DatagramKind.Text, 0, datagram[1..]);
        }

        return new V09Datagram(V09DatagramKind.Audio, 0, datagram);
    }
}
