namespace RCForb.Protocol;

/// <summary>
/// Wire constants fixed by the protocol. See docs/spec/02-transport.md.
/// </summary>
public static class WireConstants
{
    /// <summary>Marker byte introducing a text (command) frame: SPEC-TRAN-019.</summary>
    public const byte TextMarker = 0xFF;

    /// <summary>Single-byte control values on the v0.9 socket: SPEC 02 §3.1.</summary>
    public const byte Heartbeat = 0x00;
    public const byte TransmitPermitted = 0x03;
    public const byte PingPong = 0x05;
    public const byte TransmitRevoked = 0x06;
    public const byte UserIn = 0xFD;
    public const byte UserOut = 0xFE;

    /// <summary>Largest datagram either peer may produce: SPEC-TRAN-021.</summary>
    public const int MaxDatagramBytes = 8192;

    /// <summary>Recommended application ceiling, below the typical IPv4 path MTU.</summary>
    public const int RecommendedDatagramBytes = 1200;
}
