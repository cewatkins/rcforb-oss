namespace RCForb.Client.Directory;

/// <summary>Which generation of the wire protocol a remote speaks (SPEC 02 §2.1).</summary>
public enum TransportGeneration
{
    /// <summary>Two TCP connections: control plus a separate audio port. Chosen when the audio port is above zero.</summary>
    V07,

    /// <summary>One UDP socket with in-band audio. Chosen when the audio port is zero or absent.</summary>
    V09,
}

/// <summary>
/// One entry of the public directory feed, reduced to the fields the guest path needs.
/// The feed carries more (audio streaming, web links, coordinates); none of it reaches the
/// transport layer (SPEC 02 §2.1), so it is deliberately not modelled here.
/// </summary>
public sealed record RemoteRecord
{
    public required string OrbId { get; init; }
    public required string ServerName { get; init; }
    public required string RadioName { get; init; }
    public required string Domain { get; init; }
    public int ControlPort { get; init; } = 4525;
    public int AudioPort { get; init; }
    public string Security { get; init; } = "";
    public string ListenPolicy { get; init; } = "";
    public bool CanTransmit { get; init; }
    public bool Online { get; init; }
    public string Version { get; init; } = "";
    public string Country { get; init; } = "";
    public string Grid { get; init; } = "";
    public string Message { get; init; } = "";

    /// <summary>
    /// The transport is selected from the audio port alone. A feed entry that advertises an
    /// audio port is a dual-TCP remote even if the control port is the v0.9 default.
    /// </summary>
    public TransportGeneration Transport =>
        AudioPort > 0 ? TransportGeneration.V07 : TransportGeneration.V09;

    /// <summary>True when the server advertises listening without membership.</summary>
    public bool IsPublicListen =>
        string.Equals(ListenPolicy, "public", StringComparison.OrdinalIgnoreCase);

    public string Endpoint =>
        Transport == TransportGeneration.V07
            ? $"{Domain}:{ControlPort} audio {AudioPort}"
            : $"{Domain}:{ControlPort}";

    internal static bool ParseBool(string? value) =>
        value is not null &&
        (value.Trim() == "1" || bool.TryParse(value.Trim(), out var b) && b);

    internal static int ParsePort(string? value, int fallback) =>
        int.TryParse(value?.Trim(), out var port) && port > 0 && port <= 65535 ? port : fallback;
}
