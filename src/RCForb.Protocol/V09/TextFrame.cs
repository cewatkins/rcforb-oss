using System.Text;

namespace RCForb.Protocol.V09;

/// <summary>
/// Text frames on the v0.9 socket: 0xFF followed by UTF-8, no terminator of any kind.
/// A payload containing 0x0A is one command containing a line feed, never two commands.
/// SPEC-TRAN-019.
/// </summary>
public static class TextFrame
{
    /// <summary>Largest command payload that still fits one datagram.</summary>
    public const int MaxCommandBytes = WireConstants.MaxDatagramBytes - 1;

    public static byte[] Encode(string command)
    {
        ArgumentNullException.ThrowIfNull(command);

        var payloadBytes = Encoding.UTF8.GetByteCount(command);
        if (payloadBytes > MaxCommandBytes)
        {
            throw new ArgumentException(
                $"A command of {payloadBytes} UTF-8 bytes exceeds the {MaxCommandBytes}-byte frame limit " +
                "(SPEC-TRAN-021).",
                nameof(command));
        }

        var frame = new byte[payloadBytes + 1];
        frame[0] = WireConstants.TextMarker;
        Encoding.UTF8.GetBytes(command, frame.AsSpan(1));
        return frame;
    }

    /// <summary>
    /// Decodes bytes 1..n as UTF-8. Returns false for a datagram that is not a text frame
    /// or whose payload is not valid UTF-8; an empty payload is a legal frame carrying no command.
    /// </summary>
    public static bool TryDecode(ReadOnlySpan<byte> datagram, out string command)
    {
        command = string.Empty;

        if (datagram.Length == 0 || datagram[0] != WireConstants.TextMarker)
        {
            return false;
        }

        if (datagram.Length == 1)
        {
            return true;
        }

        try
        {
            command = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(datagram[1..]);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }
}
