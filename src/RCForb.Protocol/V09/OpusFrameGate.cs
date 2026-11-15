namespace RCForb.Protocol.V09;

/// <summary>
/// The reference encoder refuses to emit a frame whose first byte falls outside 0x08..0xFA,
/// because such a frame could be mistaken for the text marker. The constraint is stricter
/// than the classifier strictly needs, but peers may assume it: SPEC 02 §3.3.
/// </summary>
public static class OpusFrameGate
{
    /// <summary>
    /// True when the frame may be put on the wire unchanged. A frame that fails this test
    /// must be re-encoded or replaced with comfort silence; it must never be sent.
    /// </summary>
    public static bool IsSendable(ReadOnlySpan<byte> frame)
    {
        if (frame.Length == 0)
        {
            return false;
        }

        var first = frame[0];

        if (first == WireConstants.TextMarker)
        {
            // Would be read as a command string.
            return false;
        }

        if (first >= 0x08 && first <= 0xFA)
        {
            return true;
        }

        if (first <= 0x07)
        {
            // Unambiguous only because the datagram is longer than one byte.
            return frame.Length > 1;
        }

        // 0xFB..0xFE: outside the reference encoder's window, so no peer expects them.
        return false;
    }
}
