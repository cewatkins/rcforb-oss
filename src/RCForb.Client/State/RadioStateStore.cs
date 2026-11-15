using System.Text;

namespace RCForb.Client.State;

/// <summary>One chat line from the server, as received on the control channel.</summary>
public sealed record ChatMessage(string Sender, string Timestamp, string Text, string Raw);

/// <summary>One connected user on the remote (roster entry). Keyed for removal by <see cref="Uid"/>.</summary>
public sealed record RosterEntry(string User, string Uid);

/// <summary>
/// Immutable copy of the display state, for readers that do not own the control pump.
/// Dictionaries are copied, so enumeration is safe and stable while the session runs.
/// </summary>
public sealed record RadioStateSnapshot(
    bool IsReady,
    int LinesApplied,
    string? RadioName,
    string? Driver,
    string? ProtocolVersion,
    string? OrbId,
    string? Band,
    string? FrequencyText,
    string? FrequencyBText,
    string SMeter,
    IReadOnlyDictionary<string, string> Dropdowns,
    IReadOnlyDictionary<string, string> Lists,
    IReadOnlyList<RosterEntry> Roster,
    IReadOnlyDictionary<string, string> Extension);

/// <summary>
/// The radio state pushed by a server. Only the bags the guest renders are modelled
/// explicitly; every other key is kept in <see cref="Extension"/> so nothing is lost
/// (SPEC-STATE-005). Values are stored as the wire sent them: the state is a display
/// mirror, not a validated radio model.
/// </summary>
/// <remarks>
/// Lines are applied from the session's control pump. The bag getters expose the live
/// dictionaries and are only safe on that one thread; anything else (a UI, a timer) must
/// call <see cref="Snapshot"/> and render the copy, or it will enumerate a dictionary while
/// the pump writes it.
/// </remarks>
public sealed class RadioStateStore
{
    private readonly object _gate = new();
    private readonly List<ChatMessage> _chat = new();
    private readonly List<RosterEntry> _roster = new();

    public bool IsReady { get; private set; }
    public int LinesApplied { get; private set; }

    public string? RadioName { get; private set; }
    public string? Driver { get; private set; }
    public string? ProtocolVersion { get; private set; }
    public string? OrbId { get; private set; }
    public string? Band { get; private set; }
    public string? Raw { get; private set; }

    /// <summary>VFO A exactly as sent: Hz, or the <c>PWROFF</c> sentinel.</summary>
    public string? FrequencyText { get; private set; }

    public string? FrequencyBText { get; private set; }

    public int? FrequencyHz => ParseHz(FrequencyText);
    public int? FrequencyBHz => ParseHz(FrequencyBText);

    /// <summary>Unparsed <c>&lt;label&gt;,&lt;value&gt;,&lt;max&gt;</c> meter strings, keyed by bag name.</summary>
    public IReadOnlyDictionary<string, string> Meters => _meters;

    public IReadOnlyDictionary<string, string> Buttons => _buttons;
    public IReadOnlyDictionary<string, string> Dropdowns => _dropdowns;
    public IReadOnlyDictionary<string, string> Sliders => _sliders;
    public IReadOnlyDictionary<string, string> Statuses => _statuses;
    public IReadOnlyDictionary<string, string> Messages => _messages;
    public IReadOnlyDictionary<string, string> Lists => _lists;
    public IReadOnlyDictionary<string, string> Ranges => _ranges;

    /// <summary>Keys outside the modelled schema, stored verbatim under their full suffix.</summary>
    public IReadOnlyDictionary<string, string> Extension => _extension;

    public IReadOnlyList<ChatMessage> Chat => _chat;
    public ChatMessage? LastChat => _chat.Count == 0 ? null : _chat[^1];

    /// <summary>Users currently connected to the remote (roster). Removed by uid.</summary>
    public IReadOnlyList<RosterEntry> Roster => _roster;

    public string? Info { get; private set; }
    public string? Warning { get; private set; }
    public string? Error { get; private set; }

    public string SMeter => _meters.GetValueOrDefault("smeter", "");
    public string SMeterB => _meters.GetValueOrDefault("smeterb", _meters.GetValueOrDefault("smeterB", ""));

    private readonly Dictionary<string, string> _meters = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _buttons = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _dropdowns = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _sliders = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _statuses = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _messages = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _lists = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _ranges = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _extension = new(StringComparer.Ordinal);

    /// <summary>
    /// Applies one command line. The server may use either namespace: the v0.7 peer sends
    /// <c>post::</c> and the client's own receive path rewrites it to <c>radio::</c>
    /// (SPEC 04). Readiness is latched before the rewrite so <c>post::state-posted</c> works.
    /// </summary>
    /// <returns>True when the line changed modelled state; false for a line that was only
    /// chat, a protocol marker, or a duplicate.</returns>
    public bool Apply(string line)
    {
        lock (_gate)
        {
            return ApplyCore(line);
        }
    }

    /// <summary>
    /// A consistent copy of the display state, taken under the store's lock. Safe to call
    /// from any thread while the session keeps applying lines.
    /// </summary>
    public RadioStateSnapshot Snapshot()
    {
        lock (_gate)
        {
            return new RadioStateSnapshot(
                IsReady,
                LinesApplied,
                RadioName,
                Driver,
                ProtocolVersion,
                OrbId,
                Band,
                FrequencyText,
                FrequencyBText,
                SMeter,
                new Dictionary<string, string>(_dropdowns, StringComparer.Ordinal),
                new Dictionary<string, string>(_lists, StringComparer.Ordinal),
                new List<RosterEntry>(_roster),
                new Dictionary<string, string>(_extension, StringComparer.Ordinal));
        }
    }

    private bool ApplyCore(string line)
    {
        if (string.IsNullOrEmpty(line))
        {
            return false;
        }

        if (!IsReady && (line.StartsWith("chat::", StringComparison.Ordinal) || IsStatePosted(line)))
        {
            IsReady = true;
        }

        if (line.StartsWith("chat::", StringComparison.Ordinal))
        {
            ApplyChat(line);
            return false;
        }

        // Roster/auth literals on the session scope are never rewritten (SPEC-CMD-027/057).
        if (line.StartsWith("server::", StringComparison.Ordinal))
        {
            var serverRest = line["server::".Length..];
            if (serverRest.StartsWith("user_in::", StringComparison.Ordinal))
            {
                var payload = serverRest["user_in::".Length..];
                var fields = payload.Split("::", 2, StringSplitOptions.None);
                var user = fields.Length > 0 ? fields[0] : "";
                var uid = fields.Length > 1 && !string.IsNullOrEmpty(fields[1]) ? fields[1] : user;
                AddRoster(user, uid);
                LinesApplied++;
                return true;
            }
            else if (serverRest.StartsWith("user_out::", StringComparison.Ordinal))
            {
                var payload = serverRest["user_out::".Length..];
                var fields = payload.Split("::", 2, StringSplitOptions.None);
                var uid = fields.Length > 1 && !string.IsNullOrEmpty(fields[1]) ? fields[1] : (fields.Length > 0 ? fields[0] : "");
                RemoveRoster(uid);
                LinesApplied++;
                return true;
            }
            else if (serverRest.StartsWith("user_login::", StringComparison.Ordinal))
            {
                var payload = serverRest["user_login::".Length..];
                var fields = payload.Split("::", 2, StringSplitOptions.None);
                var user = fields.Length > 0 ? fields[0] : "";
                var uid = fields.Length > 1 && !string.IsNullOrEmpty(fields[1]) ? fields[1] : user;
                RemoveRoster(uid);
                AddRoster(user, uid);
                LinesApplied++;
                return true;
            }

            return false;
        }

        if (line.StartsWith("post::", StringComparison.Ordinal))
        {
            line = "radio::" + line["post::".Length..];
        }
        else if (line.StartsWith("k3remote::", StringComparison.Ordinal))
        {
            line = "radio::raw::" + line["k3remote::".Length..];
        }

        if (!line.StartsWith("radio::", StringComparison.Ordinal))
        {
            return false;
        }

        LinesApplied++;
        var rest = line["radio::".Length..];

        if (rest.StartsWith("radio::", StringComparison.Ordinal))
        {
            RadioName = rest["radio::".Length..];
        }
        else if (rest.StartsWith("driver::", StringComparison.Ordinal))
        {
            Driver = rest["driver::".Length..];
        }
        else if (rest.StartsWith("version::", StringComparison.Ordinal))
        {
            ProtocolVersion = rest["version::".Length..];
        }
        else if (rest.StartsWith("orbid::", StringComparison.Ordinal))
        {
            OrbId = rest["orbid::".Length..];
        }
        else if (rest.StartsWith("band::", StringComparison.Ordinal))
        {
            Band = rest["band::".Length..];
        }
        else if (rest.StartsWith("frequencyB::", StringComparison.Ordinal))
        {
            FrequencyBText = rest["frequencyB::".Length..];
        }
        else if (rest.StartsWith("user_in::", StringComparison.Ordinal))
        {
            var user = rest["user_in::".Length..];
            AddRoster(user, user);
        }
        else if (rest.StartsWith("user_out::", StringComparison.Ordinal))
        {
            var uid = rest["user_out::".Length..];
            RemoveRoster(uid);
        }
        else if (rest.StartsWith("frequency::", StringComparison.Ordinal))
        {
            FrequencyText = rest["frequency::".Length..];
        }
        else if (rest.StartsWith("smeter::", StringComparison.Ordinal))
        {
            _meters["smeter"] = rest["smeter::".Length..];
        }
        else if (rest.StartsWith("smeterB::", StringComparison.Ordinal))
        {
            // The wire literal keeps its capital B, the stored key does not (SPEC-STATE-003).
            _meters["smeterb"] = rest["smeterB::".Length..];
        }
        else if (TryKeyValue(rest, "meter::", _meters) ||
                 TryKeyValue(rest, "button::", _buttons) ||
                 TryKeyValue(rest, "list::", _lists) ||
                 TryKeyValue(rest, "dropdown::", _dropdowns) ||
                 TryKeyValue(rest, "range::", _ranges) ||
                 TryKeyValue(rest, "slider::", _sliders) ||
                 TryKeyValue(rest, "message::", _messages) ||
                 TryKeyValue(rest, "status::", _statuses))
        {
        }
        else if (rest.StartsWith("info::", StringComparison.Ordinal))
        {
            Info = rest["info::".Length..];
        }
        else if (rest.StartsWith("warn::", StringComparison.Ordinal))
        {
            Warning = rest["warn::".Length..];
        }
        else if (rest.StartsWith("error::", StringComparison.Ordinal))
        {
            Error = rest["error::".Length..];
        }
        else if (rest.StartsWith("raw::", StringComparison.Ordinal))
        {
            Raw = rest["raw::".Length..];
        }
        else if (rest == "state-posted")
        {
            // The readiness marker, already latched above.
        }
        else
        {
            var separator = rest.IndexOf("::", StringComparison.Ordinal);
            if (separator < 0)
            {
                _extension[rest] = "";
            }
            else
            {
                _extension[rest[..separator]] = rest[(separator + 2)..];
            }
        }

        return true;
    }

    private void AddRoster(string user, string uid)
    {
        if (string.IsNullOrEmpty(user)) return;
        if (string.IsNullOrEmpty(uid)) uid = user;
        _roster.Add(new RosterEntry(user, uid));
    }

    private void RemoveRoster(string uid)
    {
        if (string.IsNullOrEmpty(uid)) return;
        _roster.RemoveAll(e => e.Uid == uid);
    }

    private void ApplyChat(string line)
    {
        var rest = line["chat::".Length..];
        var parts = rest.Split("::", 3, StringSplitOptions.None);
        var sender = parts.Length > 0 ? parts[0] : "";
        var timestamp = parts.Length > 1 ? parts[1] : "";
        var encoded = parts.Length > 2 ? parts[2] : "";
        var text = ProtocolText.Decode(encoded);

        _chat.Add(new ChatMessage(sender, timestamp, text, line));
        if (_chat.Count > 200)
        {
            _chat.RemoveAt(0);
        }
    }

    private static bool IsStatePosted(string line)
    {
        var text = line.StartsWith("post::", StringComparison.Ordinal) ? line["post::".Length..] : line;
        return text == "state-posted" || text == "radio::state-posted";
    }

    private static int? ParseHz(string? text)
    {
        if (text is null)
        {
            return null;
        }

        return int.TryParse(text, out var hz) && hz >= 0 ? hz : null;
    }

    private bool TryKeyValue(string rest, string prefix, Dictionary<string, string> bag)
    {
        if (!rest.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var payload = rest[prefix.Length..];
        var separator = payload.IndexOf("::", StringComparison.Ordinal);
        var key = separator < 0 ? payload : payload[..separator];
        var value = separator < 0 ? "" : payload[(separator + 2)..];

        if (key.Length == 0)
        {
            _extension[rest] = value;
            return true;
        }

        bag[key] = value;
        return true;
    }

    /// <summary>Human-readable summary lines, shared by the CLI and tests.</summary>
    public IEnumerable<string> Summary()
    {
        yield return $"Radio:     {RadioName ?? "(unknown)"}";
        if (!string.IsNullOrEmpty(Driver))
        {
            yield return $"Driver:    {Driver}";
        }

        yield return $"Frequency: {FormatHz(FrequencyText)}{FormatMode()}";
        if (!string.IsNullOrEmpty(FrequencyBText))
        {
            yield return $"VFO B:     {FormatHz(FrequencyBText)}";
        }

        if (!string.IsNullOrEmpty(SMeter))
        {
            yield return $"S-meter:   {SMeter}";
        }

        if (!string.IsNullOrEmpty(Band))
        {
            yield return $"Band:      {Band}";
        }
    }

    /// <summary>Hz as MHz with six decimals; anything non-numeric is shown verbatim.</summary>
    public static string FormatHz(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "(unknown)";
        }

        return int.TryParse(text, out var hz) ? $"{hz / 1_000_000.0:0.000000} MHz" : text;
    }

    private string FormatMode()
    {
        var mode = _dropdowns.GetValueOrDefault("Mode", "");
        var filter = _dropdowns.GetValueOrDefault("Filter", "");
        var suffix = new StringBuilder();
        if (mode.Length > 0)
        {
            suffix.Append(" (").Append(mode);
            if (filter.Length > 0)
            {
                suffix.Append(", ").Append(filter);
            }

            suffix.Append(')');
        }

        return suffix.ToString();
    }
}
