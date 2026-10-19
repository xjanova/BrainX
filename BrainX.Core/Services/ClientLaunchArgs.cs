namespace BrainX.Core.Services;

/// <summary>
/// BrainX.Client's command line: <c>BrainX.Client.exe [vault] [--settings &lt;section&gt;]</c>.
///
/// The first argument used to be read as the vault path unconditionally, so
/// <c>--settings claude-in-chrome</c> — the target of bridge_status's "Turn on
/// in BrainX" action — would have opened a vault named "--settings" and
/// remembered it. Now a switch is never a vault, and the only switch with a
/// value is checked against a fixed list before anything acts on it.
///
/// When a client is already running, the second launch hands the section over
/// by WM_COPYDATA (<see cref="CopyDataTag"/>, <see cref="ToMessage"/>) and
/// exits; the running window opens Settings at that card.
/// </summary>
public sealed record ClientLaunchArgs(string? VaultPath, bool OpenSettings, string? SettingsSection)
{
    public const string SettingsSwitch = "--settings";

    /// <summary>dwData of the WM_COPYDATA a second launch sends ('BXST').</summary>
    public const int CopyDataTag = 0x42585354;

    private const string MessagePrefix = "brainx:settings:";

    /// <summary>
    /// The sections a launch may open, by public name → the Settings card's
    /// anchor tag (<c>SettingsAnchor&lt;tag&gt;</c> in MainWindow.xaml). Nothing
    /// outside this list is ever looked up by name.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> Sections =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["claude-in-chrome"] = "Chrome",
            ["chrome"] = "Chrome",
            ["mcp"] = "Mcp",
            ["updates"] = "Updates",
            ["storage"] = "Storage",
            ["about"] = "About",
        };

    public static ClientLaunchArgs Parse(IEnumerable<string>? args)
    {
        string? vault = null, section = null;
        var open = false;
        var list = (args ?? Array.Empty<string>()).ToList();
        for (var i = 0; i < list.Count; i++)
        {
            var a = list[i] ?? "";
            if (a.StartsWith(SettingsSwitch + "=", StringComparison.OrdinalIgnoreCase))
            {
                open = true;
                section ??= Known(a[(SettingsSwitch.Length + 1)..]);
                continue;
            }
            if (string.Equals(a, SettingsSwitch, StringComparison.OrdinalIgnoreCase))
            {
                open = true;
                if (i + 1 < list.Count && !list[i + 1].StartsWith('-'))
                    section ??= Known(list[++i]);
                continue;
            }
            // Any other switch (Velopack's, a future one) is not a vault.
            if (a.StartsWith('-') || string.IsNullOrWhiteSpace(a)) continue;
            vault ??= a;
        }
        return new ClientLaunchArgs(vault, open, section);
    }

    /// <summary>The section's canonical public name, or null if it is not one.</summary>
    private static string? Known(string? s)
    {
        s = s?.Trim();
        if (string.IsNullOrEmpty(s) || !Sections.ContainsKey(s)) return null;
        return Sections.Keys.First(k => string.Equals(k, s, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The Settings anchor tag for a section, or null.</summary>
    public static string? AnchorFor(string? section) =>
        section != null && Sections.TryGetValue(section, out var tag) ? tag : null;

    /// <summary>What a second launch sends the running client.</summary>
    public static string ToMessage(string? section) => MessagePrefix + (Known(section) ?? "");

    /// <summary>
    /// Read a received message: (true, section-or-null) for a well-formed
    /// settings request, (false, null) for anything else — any process on the
    /// desktop can send WM_COPYDATA, so nothing here is trusted beyond opening
    /// one of the known Settings cards.
    /// </summary>
    public static (bool IsSettings, string? Section) FromMessage(string? message)
    {
        if (string.IsNullOrEmpty(message) || message.Length > 128
            || !message.StartsWith(MessagePrefix, StringComparison.Ordinal)) return (false, null);
        var rest = message[MessagePrefix.Length..];
        return (true, rest.Length == 0 ? null : Known(rest));
    }
}
