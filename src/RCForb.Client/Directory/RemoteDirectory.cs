using System.Xml.Linq;

namespace RCForb.Client.Directory;

/// <summary>
/// The public remote list, as fetched from <c>http://online.remotehams.com/xmlfeed.php</c>.
/// The feed is plain HTTP and unauthenticated; it is a discovery aid, not authority.
/// </summary>
public sealed class RemoteDirectory
{
    public const string DefaultFeedUrl = "http://online.remotehams.com/xmlfeed.php";

    private RemoteDirectory(IReadOnlyList<RemoteRecord> entries) => Entries = entries;

    /// <summary>Online remotes in a stable display order (server name, then radio name).</summary>
    public IReadOnlyList<RemoteRecord> Entries { get; }

    /// <summary>
    /// Parses the feed. Rows without a domain or with an unusable control port are skipped:
    /// a remote that cannot be addressed is not a listing, it is a parse artefact.
    /// </summary>
    public static RemoteDirectory Parse(string xml)
    {
        ArgumentException.ThrowIfNullOrEmpty(xml);

        var document = XDocument.Parse(xml);
        var entries = new List<RemoteRecord>();

        foreach (var element in document.Descendants("Radio"))
        {
            var domain = Field(element, "Domain");
            if (string.IsNullOrWhiteSpace(domain))
            {
                continue;
            }

            entries.Add(new RemoteRecord
            {
                OrbId = Field(element, "OrbId"),
                ServerName = Field(element, "ServerName"),
                RadioName = Field(element, "RadioName"),
                Domain = domain,
                ControlPort = RemoteRecord.ParsePort(Field(element, "Port"), 4525),
                // Zero is meaningful here: it selects the single-socket path.
                AudioPort = RemoteRecord.ParsePort(Field(element, "VoipPort"), 0),
                Security = Field(element, "Security"),
                ListenPolicy = Field(element, "CanListen"),
                CanTransmit = RemoteRecord.ParseBool(Field(element, "CanTransmit")),
                Online = RemoteRecord.ParseBool(Field(element, "Online")),
                Version = Field(element, "Version"),
                Country = Field(element, "Country"),
                Grid = Field(element, "Grid"),
                Message = Field(element, "Message"),
            });
        }

        var ordered = entries
            .OrderBy(e => e.ServerName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.RadioName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new RemoteDirectory(ordered);
    }

    /// <summary>
    /// Resolves a one-based list index, an orb id, or a name. Name matching is exact first,
    /// then a unique case-insensitive substring, so a script can say <c>--join K3</c> without
    /// depending on list positions.
    /// </summary>
    public RemoteRecord Find(string query)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        if (int.TryParse(query, out var index))
        {
            if (index < 1 || index > Entries.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(query), $"Index {index} is outside 1..{Entries.Count}.");
            }

            return Entries[index - 1];
        }

        var exact = Entries.FirstOrDefault(e =>
            string.Equals(e.OrbId, query, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(e.ServerName, query, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(e.RadioName, query, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            return exact;
        }

        var matches = Entries.Where(e =>
            e.ServerName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            e.RadioName.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();

        return matches.Length switch
        {
            1 => matches[0],
            0 => throw new ArgumentException($"No remote matches '{query}'.", nameof(query)),
            _ => throw new ArgumentException(
                $"'{query}' matches {matches.Length} remotes ({string.Join(", ", matches.Take(5).Select(m => m.ServerName))}...). " +
                "Use the list index instead.",
                nameof(query)),
        };
    }

    /// <summary>Downloads and parses the feed. Plain HTTP is what the service offers.</summary>
    public static async Task<RemoteDirectory> FetchAsync(
        HttpClient? http = null,
        string url = DefaultFeedUrl,
        CancellationToken cancellationToken = default)
    {
        var owned = http is null;
        http ??= new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        try
        {
            var xml = await http.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
            return Parse(xml);
        }
        finally
        {
            if (owned)
            {
                http.Dispose();
            }
        }
    }

    private static string Field(XElement row, string name) => row.Element(name)?.Value.Trim() ?? "";
}
