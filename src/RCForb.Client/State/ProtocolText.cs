namespace RCForb.Client.State;

/// <summary>
/// Percent-decoding for server-supplied display text. Both chat lines and the
/// <c>post::&lt;severity&gt;::</c> dialogs are sent encoded, with <c>%0D%0A</c> standing in for
/// line breaks (SPEC 04).
/// </summary>
public static class ProtocolText
{
    /// <summary>
    /// Decodes percent escapes and flattens line breaks to single spaces, which is what a
    /// one-line console or status field can show. Malformed escapes are left as they are
    /// rather than dropping the whole message.
    /// </summary>
    public static string Decode(string encoded)
    {
        if (string.IsNullOrEmpty(encoded))
        {
            return string.Empty;
        }

        string decoded;
        try
        {
            decoded = Uri.UnescapeDataString(encoded);
        }
        catch (UriFormatException)
        {
            decoded = encoded;
        }

        return decoded
            .Replace("\r\n", " ")
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();
    }
}
