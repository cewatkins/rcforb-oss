using System.Buffers.Binary;
using System.Text;

namespace RCForb.Protocol.V07;

/// <summary>
/// The 64-byte session packet that binds the v0.7 audio socket to the control socket.
/// It carries the same user and password digest that was sent as <c>login</c>, ASCII-encoded,
/// with no nonce, challenge, key or signature: any peer that observed the login can reproduce
/// it. SPEC 02 §4.6.
/// </summary>
public static class V07SessionPacket
{
    public const int PacketBytes = 64;
    public const int SessionMagic = 54;
    public const int MaxPayloadBytes = 54;
    public const int MaxUserBytes = MaxPayloadBytes - 1 - DigestLength;
    public const int DigestLength = 32;

    /// <summary>
    /// Builds the packet. The user name is lower-cased and the digest appended verbatim,
    /// separated by a comma. A name that would truncate the digest is refused rather than
    /// silently cut, because truncation leaves the two sides unable to agree forever.
    /// </summary>
    public static byte[] Build(string user, string passwordMd5Hex)
    {
        ArgumentException.ThrowIfNullOrEmpty(user);
        ArgumentException.ThrowIfNullOrEmpty(passwordMd5Hex);

        if (passwordMd5Hex.Length != DigestLength || !IsLowerHex(passwordMd5Hex))
        {
            throw new ArgumentException(
                $"Expected a {DigestLength}-character lower-case hex MD5.", nameof(passwordMd5Hex));
        }

        if (user.Contains(','))
        {
            throw new ArgumentException("A user name may not contain a comma: it is the field separator.", nameof(user));
        }

        var normalized = user.ToLowerInvariant();
        var userBytes = Encoding.ASCII.GetByteCount(normalized);
        if (userBytes != normalized.Length)
        {
            throw new ArgumentException("The session payload is ASCII; the user name must be ASCII.", nameof(user));
        }

        if (userBytes > MaxUserBytes)
        {
            throw new ArgumentException(
                $"A user name of {userBytes} bytes would truncate the digest; the maximum is {MaxUserBytes} (SPEC-TRAN-040).",
                nameof(user));
        }

        var payloadLength = userBytes + 1 + DigestLength;
        var packet = new byte[PacketBytes];

        BinaryPrimitives.WriteInt32LittleEndian(packet, SessionMagic);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(4), payloadLength);

        Encoding.ASCII.GetBytes(normalized, packet.AsSpan(8));
        packet[8 + userBytes] = (byte)',';
        Encoding.ASCII.GetBytes(passwordMd5Hex, packet.AsSpan(8 + userBytes + 1));

        // Bytes after the payload stay zero; the receiver consumes them as padding.
        return packet;
    }

    /// <summary>Parses a packet produced by <see cref="Build"/>. Returns false on any inconsistency.</summary>
    public static bool TryParse(ReadOnlySpan<byte> packet, out string user, out string passwordMd5Hex)
    {
        user = string.Empty;
        passwordMd5Hex = string.Empty;

        if (packet.Length != PacketBytes)
        {
            return false;
        }

        if (BinaryPrimitives.ReadInt32LittleEndian(packet) != SessionMagic)
        {
            return false;
        }

        var payloadLength = BinaryPrimitives.ReadInt32LittleEndian(packet[4..]);
        if (payloadLength < 2 || payloadLength > MaxPayloadBytes)
        {
            return false;
        }

        var payload = packet.Slice(8, payloadLength);
        var comma = payload.IndexOf((byte)',');
        if (comma <= 0 || payload.Length - comma - 1 != DigestLength)
        {
            return false;
        }

        user = Encoding.ASCII.GetString(payload[..comma]);
        passwordMd5Hex = Encoding.ASCII.GetString(payload[(comma + 1)..]);
        return IsLowerHex(passwordMd5Hex);
    }

    private static bool IsLowerHex(string value)
    {
        foreach (var c in value)
        {
            if (c is not ((>= '0' and <= '9') or (>= 'a' and <= 'f')))
            {
                return false;
            }
        }

        return true;
    }
}
