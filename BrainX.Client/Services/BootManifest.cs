using System;
using System.Collections.Generic;
using System.Linq;

namespace BrainX.Client.Services;

/// <summary>
/// Every single thing BrainX does on startup, in the order it does it.
///
/// <para><b>ADDING STARTUP WORK? ADD A ROW HERE.</b> That is not a style
/// preference, it is the contract this list exists to keep. The owner reads
/// the checklist to know what the app is doing and how much is left, and the
/// boot curtain lifts when — and only when — every row on it has settled.
/// Work that is not on the list is work that runs while the bar claims to be
/// finished, which is how the window came to sit frozen under a completed
/// progress bar. Twice.</para>
///
/// <para><b>One list, two renderers.</b> This is the source. The WPF overlay
/// (<c>UniverseLoadingOverlay</c>) draws it from the first painted frame,
/// before WebView2 exists at all; the Universe HUD is sent the same list and
/// draws it from the moment its page loads, then takes over. They cannot
/// disagree, because there is nothing for them to disagree about.</para>
///
/// <para>Rows settle OUT of order — several of these run at once and land
/// whenever they land. The order here is the order the work is STARTED, which
/// is what makes the list readable as a description of the boot.</para>
/// </summary>
public static class BootManifest
{
    /// <summary>
    /// One row. <paramref name="Tag"/> is the <see cref="StartupProgress"/>
    /// tag whose reports belong to this row — it is how a stage that only
    /// reports progress (the update download's percentage) finds its line.
    /// Null means nothing reports into it; the row is ticked directly.
    /// </summary>
    public readonly record struct Row(string Id, string Label, string? Tag = null);

    public static readonly IReadOnlyList<Row> Rows = new[]
    {
        // ── Before the Universe page exists. The WPF overlay is the only
        //    thing that can show these: measured cold, the window sat black
        //    for ten seconds before WebView2 had painted anything. ──
        new Row("boot",       "Booting BrainX",              "boot"),
        new Row("theme",      "Loading theme & resources",   "theme"),
        new Row("update",     "Checking for updates",        "update"),
        new Row("identity",   "Initialising brain identity", "identity"),
        new Row("universe",   "Starting the Universe",       "universe"),
        new Row("host",       "Reading the vault",           "index"),
        new Row("claudeconn", "Checking Claude connection",  "mcp"),
        new Row("ui",         "Wiring UI panels",            "ui"),

        // ── The HUD's own sections, ticked by their render functions as each
        //    payload lands and relayed back here. `network` is the peer
        //    READOUT — the mesh connection itself is `mesh`, below, and
        //    conflating the two let the list claim the mesh was joined while
        //    the dial was still ringing. ──
        new Row("galaxy",     "Rendering galaxy"),
        new Row("stats",      "Reading brain index"),
        new Row("expertise",  "Mapping expertise"),
        new Row("activity",   "Attaching activity feed"),
        new Row("agents",     "Locating agents"),
        new Row("network",    "Reading mesh status"),
        new Row("system",     "Polling system"),
        new Row("usage",      "Tallying usage"),

        // ── After the readouts, and the reason this list was written: all of
        //    it used to run with the curtain already up. ──
        new Row("export",     "Writing brain snapshot",      "tail-export"),
        new Row("mcpfresh",   "Verifying MCP servers",       "tail-mcpfresh"),
        new Row("mesh",       "Connecting to the mesh",      "tail-mesh"),
        new Row("ai",         "Reaching the AI node",        "tail-ai"),
        new Row("springs",    "Linking notes by meaning",    "tail-springs"),
        new Row("workspace",  "Opening the workspace",       "tail-workspace"),
    };

    /// <summary>Row id a <see cref="StartupProgress"/> tag reports into, or
    /// null when the tag belongs to no row.</summary>
    public static string? RowIdForTag(string? tag)
    {
        if (string.IsNullOrEmpty(tag)) return null;
        foreach (var r in Rows) if (r.Tag == tag) return r.Id;
        return null;
    }

    public static string LabelFor(string id) =>
        Rows.FirstOrDefault(r => r.Id == id) is { Label.Length: > 0 } r ? r.Label : id;

    /// <summary>True when <paramref name="id"/> is a row anyone declared. A
    /// tick for anything else is a manifest that has fallen behind the code —
    /// surfaced loudly rather than dropped, in both renderers.</summary>
    public static bool Knows(string id) => Rows.Any(r => r.Id == id);
}
