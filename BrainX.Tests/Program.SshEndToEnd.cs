using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using BrainX.Core.Services;
using Newtonsoft.Json.Linq;

namespace BrainX.Tests;

/// <summary>
/// ssh_run through the REAL server binary, over stdio, against a temp vault
/// and a local tarpit (a socket that accepts and never speaks SSH). Nothing
/// here dials a real host: gated commands never connect, and the ones that do
/// connect to 127.0.0.1 and hang until the deadline — which is the point.
/// </summary>
internal static partial class Program
{
    private static void RegisterSshEndToEnd(List<(string Name, Func<Task> Check)> checks) =>
        checks.Add(("ssh_run end to end: gate, owner approval, real deadline, redacted audit", SshEndToEnd));

    private static async Task SshEndToEnd()
    {
        var exe = FindMcpExe();
        if (exe == null)
        {
            Check("brainx-mcp.exe (built) exists for the end-to-end check", false, "build BrainX.Mcp first");
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), "brainx-e2e-" + Guid.NewGuid().ToString("N"));
        var vault = Path.Combine(root, "vault");
        var approvals = Path.Combine(root, "approvals");
        Directory.CreateDirectory(Path.Combine(vault, ".obsidianx"));
        var key = Path.Combine(root, "id_test");

        var tarpit = new TcpListener(IPAddress.Loopback, 0);
        tarpit.Start();
        var port = ((IPEndPoint)tarpit.LocalEndpoint).Port;
        var held = new List<TcpClient>();
        _ = Task.Run(async () =>
        {
            while (true)
            {
                try { var c = await tarpit.AcceptTcpClientAsync(); lock (held) held.Add(c); }
                catch { break; }
            }
        });

        Process? server = null;
        try
        {
            if (!MakeKey(key))
            {
                Check("ssh-keygen available to make a throwaway key", false);
                return;
            }

            File.WriteAllText(Path.Combine(vault, ".obsidianx", "ssh-profiles.json"), new JObject
            {
                ["profiles"] = new JArray
                {
                    Profile("tarpit", port, key, requireConfirmation: false),
                    Profile("confirm-all", port, key, requireConfirmation: true),
                }
            }.ToString());

            var psi = new ProcessStartInfo(exe, "--serve")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false),
                // What an MCP client sends. Left unset, .NET encodes stdin in the
                // console's code page — 874 on a Thai machine — and the server,
                // which reads UTF-8, receives U+FFFD for anything outside ASCII.
                StandardInputEncoding = new UTF8Encoding(false),
            };
            psi.Environment["BRAINX_VAULT"] = vault;
            psi.Environment["BRAINX_SSH_APPROVALS_DIR"] = approvals;
            psi.Environment["BRAINX_MCP_LAUNCHER_CHILD"] = "1";
            // Throwaway vault: nothing outside it may be touched (see BRAINX_SANDBOX).
            psi.Environment["BRAINX_SANDBOX"] = "1";
            psi.Environment.Remove(StubMcpServer.EnvFlag);
            server = Process.Start(psi)!;
            server.StandardInput.AutoFlush = true;
            _ = Task.Run(async () => { try { while (await server.StandardError.ReadLineAsync() != null) { } } catch { } });

            var init = await Rpc(server, 1, "initialize", new JObject
            {
                ["protocolVersion"] = "2025-06-18",
                ["capabilities"] = new JObject(),
                ["clientInfo"] = new JObject { ["name"] = "brainx-tests", ["version"] = "1" }
            });
            Check("the server answers initialize", init?["result"] != null, init?.ToString());
            await server.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");

            int id = 10;
            async Task<JObject> Ssh(JObject args, string tool = "ssh_run") =>
                ToolJson(await Rpc(server, ++id, "tools/call", new JObject { ["name"] = tool, ["arguments"] = args }));

            var gated = await Ssh(new JObject { ["profile_id"] = "tarpit", ["command"] = "rm -rf /var/www/site" });
            var confirmId = gated["confirm_id"]?.ToString();
            Check("a destructive command answers needs_confirmation", gated["needs_confirmation"]?.Value<bool>() == true, gated.ToString());
            Check("…with a well-formed confirm_id", SshApprovalStore.IsWellFormedId(confirmId), confirmId);
            Check("…and the command the owner runs to approve it", gated["approve_command"]?.ToString().Contains("ssh-approve " + confirmId) == true);
            Check("…and it did not run", gated["exit_code"] == null && gated["stdout"] == null);

            var again = await Ssh(new JObject { ["profile_id"] = "tarpit", ["command"] = "rm -rf /var/www/site" });
            Check("asking again returns the same request", again["confirm_id"]?.ToString() == confirmId);

            var refused = await Ssh(new JObject { ["profile_id"] = "tarpit", ["command"] = "shutdown -h now" });
            Check("the profile's deny list is final — no approval offered", refused["allowed"]?.Value<bool>() == false
                  && refused["needs_confirmation"] == null && refused["error"]?.ToString().Contains("blocked by guard") == true, refused.ToString());

            var follow = await Ssh(new JObject { ["profile_id"] = "tarpit", ["command"] = "tail -f /var/log/app.log" });
            Check("a command that never exits is refused", follow["error"]?.ToString().Contains("never exits") == true, follow.ToString());

            var tailOpt = await Ssh(new JObject { ["profile_id"] = "tarpit", ["path"] = "-f" }, "ssh_tail");
            Check("ssh_tail refuses an option as its path", tailOpt["allowed"]?.Value<bool>() == false, tailOpt.ToString());

            // The gate's premise: an agent's shell cannot approve. Its stdin is
            // redirected, so the CLI must refuse before reading anything.
            var cli = new ProcessStartInfo(exe, $"ssh-approve {confirmId}")
            {
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
            };
            cli.Environment["BRAINX_SSH_APPROVALS_DIR"] = approvals;
            cli.Environment.Remove(StubMcpServer.EnvFlag);
            using (var p = Process.Start(cli)!)
            {
                await p.StandardInput.WriteLineAsync("yes");
                p.StandardInput.Close();
                await p.WaitForExitAsync();
                Check("ssh-approve refuses a piped `yes` (exit 3)", p.ExitCode == 3, $"exit {p.ExitCode}");
            }
            Check("…and the request is still pending", new SshApprovalStore(approvals).Load(confirmId!)?.Status == SshApprovalRecord.Pending);

            // The owner approves (what the interactive CLI does after "yes").
            new SshApprovalStore(approvals).Approve(confirmId!, "test-owner");
            var clock = Stopwatch.StartNew();
            var ran = await Ssh(new JObject { ["profile_id"] = "tarpit", ["command"] = "rm -rf /var/www/site", ["confirm_id"] = confirmId });
            clock.Stop();
            Check("the approved command runs (dials the tarpit)", ran["allowed"]?.Value<bool>() == true && ran["confirm_id"]?.ToString() == confirmId, ran.ToString());
            Check("…and the deadline ends it (timed_out)", ran["timed_out"]?.Value<bool>() == true, ran.ToString());
            Check($"…within the 3 s profile deadline, not the old unbounded wait ({clock.ElapsedMilliseconds} ms)", clock.ElapsedMilliseconds < 12_000);

            var spent = await Ssh(new JObject { ["profile_id"] = "tarpit", ["command"] = "rm -rf /var/www/site", ["confirm_id"] = confirmId });
            Check("the approval is spent after one run", spent["error"]?.ToString().Contains("already used") == true, spent.ToString());

            var everyCall = await Ssh(new JObject { ["profile_id"] = "confirm-all", ["command"] = "uptime" });
            Check("require_confirmation gates even a harmless command", everyCall["needs_confirmation"]?.Value<bool>() == true, everyCall.ToString());

            var secret = await Ssh(new JObject { ["profile_id"] = "tarpit", ["command"] = "mysql -uroot -pS3cr3tPass! -e 'DROP DATABASE shop'" });
            Check("a catastrophic command is flagged as such", secret["catastrophic"]?.Value<bool>() == true, secret.ToString());

            var audit = Path.Combine(vault, ".obsidianx", "ssh-audit.ndjson");
            var auditText = File.Exists(audit) ? File.ReadAllText(audit) : "";
            Check("the audit trail has its own file", auditText.Length > 0);
            Check("it records the request, the refusal and the run",
                  auditText.Contains("ssh_confirm_requested") && auditText.Contains("ssh_denied") && auditText.Contains("\"timed_out\":true"));
            Check("no credential reaches the audit file", !auditText.Contains("S3cr3tPass!"));

            var access = Path.Combine(vault, ".obsidianx", "access-log.ndjson");
            var accessText = File.Exists(access) ? File.ReadAllText(access) : "";
            Check("the access log keeps only marked pulses for SSH", accessText.Contains("\"pulse\":true") && !accessText.Contains("S3cr3tPass!"));
        }
        finally
        {
            try { server?.Kill(entireProcessTree: true); } catch { }
            server?.Dispose();
            tarpit.Stop();
            lock (held) foreach (var c in held) c.Dispose();
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static JObject Profile(string id, int port, string key, bool requireConfirmation) => new()
    {
        ["id"] = id,
        ["host"] = "127.0.0.1",
        ["port"] = port,
        ["user"] = "nobody",
        ["key_path"] = key,
        ["allow_patterns"] = new JArray("^.+"),
        ["deny_patterns"] = new JArray(@"\bshutdown\b"),
        ["max_runtime_sec"] = 3,
        ["require_confirmation"] = requireConfirmation,
        ["audit_to_brain"] = true,
        ["description"] = "end-to-end tarpit"
    };

    private static bool MakeKey(string path)
    {
        foreach (var keygen in new[] { "ssh-keygen", @"C:\Windows\System32\OpenSSH\ssh-keygen.exe", @"C:\Program Files\Git\usr\bin\ssh-keygen.exe" })
        {
            try
            {
                var psi = new ProcessStartInfo(keygen) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var a in new[] { "-q", "-t", "ed25519", "-N", "", "-f", path }) psi.ArgumentList.Add(a);
                using var p = Process.Start(psi)!;
                p.WaitForExit(15_000);
                if (p.ExitCode == 0 && File.Exists(path)) return true;
            }
            catch { /* try the next one */ }
        }
        return false;
    }

    /// <summary>
    /// The server the end-to-end checks drive: built in the harness's own
    /// configuration first — CI builds Release, a developer usually Debug, and
    /// a Debug-only lookup failed every one of these checks on the CI runner —
    /// then the other. Whatever target framework BrainX.Mcp is on.
    /// </summary>
    private static string? FindMcpExe()
    {
        var sep = Path.DirectorySeparatorChar;
        var own = AppContext.BaseDirectory.Contains($"{sep}Release{sep}", StringComparison.OrdinalIgnoreCase) ? "Release" : "Debug";
        foreach (var config in new[] { own, own == "Release" ? "Debug" : "Release" })
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                var bin = Path.Combine(dir.FullName, "BrainX.Mcp", "bin", config);
                if (!Directory.Exists(bin)) continue;
                var exe = Directory.GetDirectories(bin)
                    .Select(tfm => Path.Combine(tfm, "brainx-mcp.exe"))
                    .Where(File.Exists)
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .FirstOrDefault();
                if (exe != null) return exe;
            }
        }
        return null;
    }

    private static async Task<JObject?> Rpc(Process server, int id, string method, JObject parameters)
    {
        var request = new JObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters };
        await server.StandardInput.WriteLineAsync(request.ToString(Newtonsoft.Json.Formatting.None));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        while (!cts.IsCancellationRequested)
        {
            var line = await server.StandardOutput.ReadLineAsync(cts.Token);
            if (line == null) return null;
            JObject msg;
            try { msg = JObject.Parse(line); } catch { continue; }
            if (msg["id"]?.Type == JTokenType.Integer && (int)msg["id"]! == id) return msg;
        }
        return null;
    }

    private static JObject ToolJson(JObject? response)
    {
        var text = response?["result"]?["content"]?[0]?["text"]?.ToString();
        if (text == null) return new JObject { ["error"] = response?.ToString() ?? "no response" };
        try { return JObject.Parse(text); }
        catch { return new JObject { ["error"] = text }; }
    }
}
