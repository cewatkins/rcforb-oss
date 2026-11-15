namespace RCForb.Protocol.V07;

/// <summary>What one v0.7 audio-channel frame turned out to be. See docs/spec/02-transport.md §4.</summary>
public enum V07FrameKind
{
    /// <summary>Payload length 20..2047: one codec frame.</summary>
    Audio,

    /// <summary>Payload length 1..18: a control string, possibly carrying the keyed flag.</summary>
    Control,

    /// <summary>Length field 54: the 64-byte session packet.</summary>
    Session,
}

/// <summary>
/// One framed packet from the v0.7 audio channel. <see cref="Payload"/> excludes the
/// 10-byte header for audio and control frames, and is the ASCII session string for a
/// session packet (its trailing zero padding is consumed as padding, not returned).
/// </summary>
public readonly record struct V07Frame(
    V07FrameKind Kind,
    ReadOnlyMemory<byte> Payload,
    bool TransmitterKeyed)
{
    public int Length => Payload.Length;
}

/// <summary>Outcome of one read attempt.</summary>
public enum V07ReadResult
{
    /// <summary>No complete frame yet; nothing was consumed.</summary>
    NeedMoreData,

    /// <summary>A frame was produced and its bytes consumed.</summary>
    Frame,
}
