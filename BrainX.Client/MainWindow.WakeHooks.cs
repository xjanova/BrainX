// MainWindow.WakeHooks.cs — the two Claude Code hooks that make a hand-off
// finish itself.
//
// Owner's report (2026-08-14): "ตอนนี้ ฉันต้องคอยกำกับบอกทั้งสองฝั่ง ... เมื่อ
// ฝั่งหนึ่งเสร็จจะทำงานต่อ ต้องส่งต่อปลุกให้อีกฝั่งหนึ่งรู้และทำงานต่อ ... ตอนนี้
// ฉันต้องมากระตุ้นเอง ไม่งั้นงานไม่เดิน".
//
// task_handoff put the work in the vault and the taskQueue notice announced it
// — on the assignee's NEXT tool call. An agent that has finished answering
// never makes one, so every queued task waited for a human to type something.
// The queue was real and nothing consumed it.
//
// A hook is a process the HARNESS starts, which is the one thing on this
// machine that does not need a model to already be running:
//
//   Stop          → `brainx-mcp hook-stop`. Exit 2 puts its stderr back in
//                   front of the model and the turn continues. Finishing with
//                   a task queued for you is now how you find out about it.
//   SessionStart  → `brainx-mcp hook-session-start`. stdout becomes context,
//                   so a session that opens with work waiting starts knowing.
//
// Both point at the SAME brainx-mcp.exe already registered as the MCP server,
// so the decision (parse frontmatter, check status/assignee, apply cooldown)
// lives in compiled, CI-built code rather than in a PowerShell one-liner. That
// is not a style preference: the inline auto-ingest hook has cost two silent
// bugs already — v1 read an env var Claude Code never sets, v3 wrote a BOM
// that ate the first line of every feed.
//
// This file only writes settings.json. All the logic is in Program.Wake.cs, and
// what an entry looks like is in BrainX.Core's ClaudeWakeHooks.
//
// It also keeps the hooks of `brainx-mcp install-hooks` current. They live in
// the same file and nothing else would: an update reaches no settings.json, and a
// machine stayed on PowerShell until someone ran the installer by hand.

using System;
using System.IO;
using BrainX.Core.Services;
using Newtonsoft.Json.Linq;

namespace BrainX.Client;

public partial class MainWindow
{
    /// <summary>
    /// Install (or upgrade) the Stop + SessionStart hooks. Idempotent: returns
    /// false and touches nothing when the current version is already present.
    ///
    /// Silent and best-effort by contract — it runs at every startup, and a
    /// hook that cannot be installed must never be able to fail the app's boot.
    /// </summary>
    private bool EnsureTaskWakeHooksInstalled()
    {
        try
        {
            var mcpExe = ResolveBestMcpExe();
            // No MCP binary means no hook could run anyway. Writing one that
            // points at a path that does not exist would make Claude Code
            // report a broken hook on every single turn.
            if (mcpExe is null || !File.Exists(mcpExe)) return false;

            var path = ClaudeSettingsPath();
            var text = File.Exists(path) ? File.ReadAllText(path) : "";

            // Current = this version's tag AND this exe. The exe half is new
            // (2026-10-10): the MCP moved out of %LOCALAPPDATA%\BrainX\mcp, where
            // every update kills it, and a tag-only test would have kept these
            // hooks pointing at the old copy forever.
            if (ClaudeWakeHooks.IsCurrent(text, mcpExe, ClaudeWakeHooks.ExecTag)) return false;

            // Shell form stays only where exec form cannot run. This starts
            // processes, so it comes after the check above that needs none.
            var nativeExe = NativeHookExe(mcpExe);
            if (nativeExe == null && ClaudeWakeHooks.IsCurrent(text, mcpExe, ClaudeWakeHooks.ShellTag)) return false;

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            JObject root;
            if (File.Exists(path))
            {
                try { root = JObject.Parse(File.ReadAllText(path)); }
                catch { BackupCorruptFile(path); root = new JObject(); }
            }
            else root = new JObject();

            var hooks = root["hooks"] as JObject ?? new JObject();

            hooks["Stop"] = ClaudeWakeHooks.Upsert(
                hooks["Stop"] as JArray,
                ClaudeWakeHooks.Handler(mcpExe, "hook-stop", _vaultPath, nativeExe != null));
            hooks["SessionStart"] = ClaudeWakeHooks.Upsert(
                hooks["SessionStart"] as JArray,
                ClaudeWakeHooks.Handler(mcpExe, "hook-session-start", _vaultPath, nativeExe != null));

            root["hooks"] = hooks;

            // Backup + tmp-then-replace. This is the owner's GLOBAL Claude
            // config — their hooks, their permissions — and this path runs
            // unattended, which makes it the more dangerous of the two writers,
            // not the less.
            WriteJsonSafely(path, root.ToString(Newtonsoft.Json.Formatting.Indented));
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Bring the hooks of `install-hooks` up to this build. Only on a machine that
    /// already has them: no hooks is a choice, and so is the auto-register switch,
    /// which means "do not touch my agent configs". Silent and best-effort for the
    /// same reason as above. Starts processes: off the UI thread.
    /// </summary>
    private bool EnsureInstallerHooksCurrent()
    {
        if (!_mcpAutoRegisterEnabled) return false;
        try { return HookHealth.UpgradeInstallerHooks(HookHealth.DefaultClaudeDir(), ResolveBestMcpExe(), _vaultPath); }
        catch { return false; }
    }
}
