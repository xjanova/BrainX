// ClaudeUsageProbe — pulls live plan-limit numbers off
// claude.ai/settings/usage via a hidden WebView2.
//
// Why a WebView2 and not an HttpClient: the settings/usage page is a
// Next.js shell that hydrates from JSON the server bakes into a script
// tag (__NEXT_DATA__). Reading it requires a live browser session that
// has executed the auth handshake and received the long-lived
// `sessionKey` + cookies, so a plain HTTP fetch from .NET would only
// see the login redirect. WebView2 IS the browser, so the easiest path
// is to let it navigate and then read the resulting DOM/JSON via JS.
//
// On startup we copy the user's Edge cookies into the WebView2's
// CookieManager (see EdgeCookieReader) so navigation auto-authenticates.
// If that fails (Edge never used, cookie expired, etc.) the page lands
// on the login screen — we detect that and report Authenticated=false,
// leaving the dashboard in "amber, local only" mode.
//
// Polling cadence is 60 s — the underlying counters update at most
// once a minute, and the WebView2 runs in a 1×1 invisible host so CPU
// is negligible.

using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace BrainX.Client.Services;

public sealed class ClaudeUsageProbe
{
    public sealed class UsageRow
    {
        public double Percent { get; set; } = -1;
        public string? ResetLabel { get; set; }
    }

    public sealed class UsageSnapshot
    {
        public bool Authenticated { get; set; }
        public string? PlanLabel { get; set; }
        public UsageRow? Session { get; set; }
        public UsageRow? WeeklyAll { get; set; }
        public UsageRow? SonnetOnly { get; set; }
        public UsageRow? Credits { get; set; }
        public DateTime FetchedAt { get; set; } = DateTime.UtcNow;
    }

    public event EventHandler<UsageSnapshot>? Updated;

    private readonly MainWindow _host;
    private WebView2? _wv;
    private CoreWebView2? _core;
    private System.Windows.Threading.DispatcherTimer? _pollTimer;
    private bool _started;
    private bool _bootstrapped;
    private int _consecutiveFailures;

    private const string UsageUrl = "https://claude.ai/settings/usage";

    public ClaudeUsageProbe(MainWindow host) { _host = host; }

    public async Task StartAsync()
    {
        if (_started) return;
        _started = true;

        _wv = _host.FindName("DashClaudeProbeWebView") as WebView2;
        if (_wv == null)
        {
            System.Diagnostics.Debug.WriteLine("ClaudeUsageProbe: DashClaudeProbeWebView not found in visual tree");
            EmitFailureSnapshot();
            return;
        }

        try
        {
            // Use a dedicated user-data folder so the probe's cookies
            // don't bleed into / out of any other WebView2 the app hosts
            // (universe view, wallpaper setup). Stored under LocalAppData
            // to survive across installs.
            var userData = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BrainX", "WebView2", "ClaudeUsageProbe");
            System.IO.Directory.CreateDirectory(userData);
            var env = await CoreWebView2Environment.CreateAsync(null, userData);
            await _wv.EnsureCoreWebView2Async(env);
            _core = _wv.CoreWebView2;

            _core.NewWindowRequested += (_, e) => { e.Handled = true; };
            _core.NavigationCompleted += OnNavigated;

            await InjectEdgeCookiesAsync();

            _core.Navigate(UsageUrl);

            // Poll every 60 s.
            _pollTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(60)
            };
            _pollTimer.Tick += async (_, _) => await TickAsync();
            _pollTimer.Start();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"ClaudeUsageProbe.StartAsync: {ex.Message}");
            EmitFailureSnapshot();
        }
    }

    private async Task InjectEdgeCookiesAsync()
    {
        try
        {
            // Pull cookies from EVERY installed Chromium browser (Chrome,
            // Edge, Brave). User picked Chrome — Edge alone returned an
            // empty list because they don't use Edge. Order in
            // ChromiumCookieReader.ReadAllForHost is chrome→edge→brave so
            // duplicates resolve to chrome's value (later AddOrUpdate wins).
            var cookies = ChromiumCookieReader.ReadAllForHost("claude.ai");
            LogLine($"InjectEdgeCookies: found {cookies.Count} cookies");
            int byBrowser_chrome = 0, byBrowser_edge = 0, byBrowser_brave = 0;
            foreach (var c in cookies)
            {
                switch (c.Source)
                {
                    case ChromiumBrowser.Chrome: byBrowser_chrome++; break;
                    case ChromiumBrowser.Edge: byBrowser_edge++; break;
                    case ChromiumBrowser.Brave: byBrowser_brave++; break;
                }
            }
            LogLine($"  by browser: chrome={byBrowser_chrome} edge={byBrowser_edge} brave={byBrowser_brave}");

            if (cookies.Count == 0 || _core == null) return;

            int ok = 0, fail = 0;
            foreach (var ck in cookies)
            {
                try
                {
                    var cookie = _core.CookieManager.CreateCookie(
                        ck.Name, ck.Value, ck.Domain, ck.Path);
                    cookie.IsSecure = ck.Secure;
                    cookie.IsHttpOnly = ck.HttpOnly;
                    if (ck.ExpiresUtc is DateTime exp)
                        cookie.Expires = exp;
                    _core.CookieManager.AddOrUpdateCookie(cookie);
                    ok++;
                }
                catch (Exception ex)
                {
                    fail++;
                    LogLine($"  cookie {ck.Name} ({ck.Source}) failed: {ex.Message}");
                }
            }
            LogLine($"InjectEdgeCookies: injected {ok} OK, {fail} failed");
            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            LogLine($"InjectEdgeCookiesAsync: {ex.Message}");
        }
    }

    private static readonly string LogPath = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BrainX", "WebView2", "ClaudeUsageProbe", "probe.log");

    private static void LogLine(string msg)
    {
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(LogPath)!);
            System.IO.File.AppendAllText(LogPath,
                $"[{DateTime.Now:HH:mm:ss}] {msg}{Environment.NewLine}");
        }
        catch { }
        System.Diagnostics.Debug.WriteLine($"[ClaudeUsageProbe] {msg}");
    }

    /// <summary>
    /// Re-navigate the hidden WebView2 to the usage URL and fire an
    /// immediate scrape. Called by the dashboard after the login modal
    /// closes so the dot flips to green without waiting up to 60 s for
    /// the next scheduled tick.
    /// </summary>
    public async Task ReloadAsync()
    {
        if (_core == null) return;
        try
        {
            _core.Navigate(UsageUrl);
            await Task.Delay(2500);
            await TickAsync();
        }
        catch (Exception ex)
        {
            LogLine($"ReloadAsync: {ex.Message}");
        }
    }

    private async void OnNavigated(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!e.IsSuccess) return;
        if (_core == null) return;

        // First successful nav → wait a beat for the SPA to hydrate, then scrape.
        if (!_bootstrapped)
        {
            _bootstrapped = true;
            await Task.Delay(2500);
        }
        else
        {
            await Task.Delay(800);
        }
        await TickAsync();
    }

    private async Task TickAsync()
    {
        if (_core == null) return;
        try
        {
            string raw = await _core.ExecuteScriptAsync(ScrapeScript);
            LogLine($"tick: source={_core.Source} raw={Truncate(raw, 400)}");
            if (string.IsNullOrEmpty(raw) || raw == "null") return;
            // ExecuteScriptAsync returns a JSON-encoded string of the JS
            // return value, so the result is double-encoded JSON. Unwrap once.
            string json = raw;
            if (json.StartsWith("\"") && json.EndsWith("\""))
            {
                json = JsonSerializer.Deserialize<string>(json) ?? "";
            }
            if (string.IsNullOrWhiteSpace(json)) return;

            var snap = ParseSnapshot(json);
            if (snap == null) return;

            if (!snap.Authenticated)
            {
                _consecutiveFailures++;
                // After 3 failed scrapes, the page probably bounced to
                // login — try refreshing cookies + reload once.
                if (_consecutiveFailures == 3)
                {
                    await InjectEdgeCookiesAsync();
                    _core.Reload();
                }
            }
            else
            {
                _consecutiveFailures = 0;
            }

            try { Updated?.Invoke(this, snap); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"probe event: {ex.Message}"); }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"ClaudeUsageProbe.TickAsync: {ex.Message}");
        }
    }

    private void EmitFailureSnapshot()
    {
        try { Updated?.Invoke(this, new UsageSnapshot { Authenticated = false }); }
        catch { }
    }

    private static UsageSnapshot? ParseSnapshot(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var snap = new UsageSnapshot
            {
                Authenticated = root.TryGetProperty("auth", out var a) && a.GetBoolean(),
                PlanLabel = root.TryGetProperty("plan", out var p) && p.ValueKind == JsonValueKind.String
                    ? p.GetString() : null,
                Session = ReadRow(root, "session"),
                WeeklyAll = ReadRow(root, "weeklyAll"),
                SonnetOnly = ReadRow(root, "sonnet"),
                Credits = ReadRow(root, "credits"),
            };
            return snap;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"ParseSnapshot: {ex.Message} :: {json}");
            return null;
        }
    }

    private static UsageRow? ReadRow(JsonElement root, string key)
    {
        if (!root.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Object)
            return null;
        var row = new UsageRow();
        if (el.TryGetProperty("pct", out var pct) && pct.ValueKind == JsonValueKind.Number)
            row.Percent = pct.GetDouble();
        if (el.TryGetProperty("reset", out var rs) && rs.ValueKind == JsonValueKind.String)
            row.ResetLabel = rs.GetString();
        return row;
    }

    // JS that walks the settings/usage DOM and emits a compact JSON
    // snapshot. Resilient to small markup tweaks by anchoring on the
    // section heading texts ("Current session", "All models", "Sonnet
    // only") and finding the SMALLEST ancestor that contains EXACTLY
    // ONE "% used" — that's the row container, not the section root
    // (Weekly limits with three rows would otherwise grab the FIRST
    // "% used" it sees, which is All Models, and report it for Sonnet
    // too — exact bug user reported). The fourth row is "Usage credits",
    // scraped separately via findCredits() because it has a
    // $spent / $limit shape rather than a plain "% used" row.
    //
    // Returns "" if we appear to be on the login page so the caller
    // can react.
    private const string ScrapeScript = """
(function () {
  try {
    // Login bounce check.
    if (document.querySelector('input[type=password], button[type=submit][data-testid*="login"]')) {
      return JSON.stringify({ auth: false });
    }
    // The signed-in usage page always has the "Plan usage limits" heading.
    var bodyText = document.body ? document.body.innerText || "" : "";
    if (!/plan usage limits/i.test(bodyText)) {
      return JSON.stringify({ auth: false });
    }

    function findRow(labelRx) {
      // Find the LABEL node (e.g. "Sonnet only"), then walk up only
      // far enough that the ancestor's text has EXACTLY ONE "% used".
      // That's the row container. Stopping at the first ancestor
      // with any "% used" match grabs the parent SECTION's first row
      // for every label — that's how Sonnet/Design ended up reading
      // All Models's 88 % in the user's screenshot.
      var all = document.querySelectorAll('div,span,h1,h2,h3,h4,p,li');
      for (var i = 0; i < all.length; i++) {
        var n = all[i];
        var t = (n.innerText || "").trim();
        if (!t || t.length > 200) continue;
        if (!labelRx.test(t)) continue;

        var cur = n;
        for (var depth = 0; depth < 10 && cur; depth++) {
          var box = cur.innerText || "";
          // Skip until the label is INSIDE this ancestor (which it is
          // by construction) and we have at least one % match.
          var pctMatches = box.match(/(\d{1,3})\s*%\s*used/gi);
          if (!pctMatches) { cur = cur.parentElement; continue; }
          if (pctMatches.length === 1) {
            var pct = parseInt(pctMatches[0].match(/(\d+)/)[1], 10);
            var resetMatch = box.match(/Resets[^\n]+/i);
            return { pct: pct, reset: resetMatch ? resetMatch[0].trim() : null };
          }
          // Multiple % marks → we're at section level, walk up further
          // (or rather: do NOT use this ancestor's first match).
          // Try the LABEL's parent's siblings instead for the row.
          // If we've walked too many levels with no isolation, bail.
          cur = cur.parentElement;
        }
        // Fallback: sibling scan. The row probably looks like
        //   <label>Sonnet only</label><progress>88%</progress>
        // Walk forward from the label node looking for the next
        // "% used" within the same flex/grid row.
        var sib = n;
        for (var s = 0; s < 12 && sib; s++) {
          sib = sib.nextElementSibling;
          if (!sib) break;
          var st = sib.innerText || "";
          var sm = st.match(/(\d{1,3})\s*%\s*used/i);
          if (sm) {
            var spct = parseInt(sm[1], 10);
            // Reset text might be on a sibling AFTER the % (or before).
            var sreset = (n.parentElement && n.parentElement.innerText
                          ? n.parentElement.innerText.match(/Resets[^\n]+/i)
                          : null);
            return { pct: spct, reset: sreset ? sreset[0].trim() : null };
          }
        }
      }
      return null;
    }

    function planLabel() {
      // "Plan usage limits Max (5x)" → grab the model on the right side
      // of the heading or the dedicated chip.
      var hdr = bodyText.match(/Plan usage limits\s+([A-Za-z0-9 ()+\-]+?)(?:\n|$)/i);
      if (hdr) return hdr[1].trim();
      // Fallback: any "(5x)" / "(20x)" pattern is the plan chip.
      var chip = bodyText.match(/\b(Max|Pro|Team|Enterprise)\s*\(\s*\d+\s*x\s*\)/i);
      return chip ? chip[0] : null;
    }

    // "Usage credits" block — distinct $spent / $limit shape, not a
    // plain "% used" row, so it gets its own extractor. Pick the
    // SMALLEST element mentioning "usage credits" AND both "spent" and
    // "monthly spend limit" (= the credits card, not the whole settings
    // body), then fold the dollar figures + % + reset into one row.
    function findCredits() {
      var nodes = document.querySelectorAll('div,section,li');
      var best = null;
      for (var i = 0; i < nodes.length; i++) {
        var t = nodes[i].innerText || '';
        if (!/usage credits/i.test(t)) continue;
        if (!/spent/i.test(t) || !/monthly spend limit/i.test(t)) continue;
        if (best === null || t.length < best.length) best = t;
      }
      if (best === null) return null;
      var spentM = best.match(/\$\s*([\d.,]+)\s*spent/i);
      var limitM = best.match(/\$\s*([\d.,]+)\s+Monthly spend limit/i);
      var pctM   = best.match(/(\d{1,3})\s*%\s*used/i);
      var resetM = best.match(/Resets[^\n]+/i);
      var spent = spentM ? ('$' + spentM[1]) : null;
      var limit = limitM ? ('$' + limitM[1]) : null;
      var parts = [];
      if (spent && limit) parts.push(spent + ' / ' + limit);
      else if (spent) parts.push(spent);
      if (resetM) parts.push(resetM[0].trim());
      return {
        pct: pctM ? parseInt(pctM[1], 10) : 0,
        reset: parts.length ? parts.join(' · ') : null
      };
    }

    var session = findRow(/current session/i);
    var weeklyAll = findRow(/all models/i);
    var sonnet = findRow(/sonnet only/i);
    var credits = findCredits();

    return JSON.stringify({
      auth: true,
      plan: planLabel(),
      session: session,
      weeklyAll: weeklyAll,
      sonnet: sonnet,
      credits: credits
    });
  } catch (e) {
    return JSON.stringify({ auth: false, error: String(e && e.message || e) });
  }
})()
""";

    private static string Truncate(string? s, int n) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s.Substring(0, n) + "…");

    public void Dispose()
    {
        try { _pollTimer?.Stop(); } catch { }
        _pollTimer = null;
    }
}
