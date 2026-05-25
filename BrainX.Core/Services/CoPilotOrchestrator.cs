using System.Text;
using System.Text.Json;

namespace BrainX.Core.Services;

/// <summary>
/// Orchestrates the Co-Pilot Arena flow:
///
///     user spec
///        │
///        ▼
///     Intern (local LLM, e.g. Ollama qwen2.5:7b)
///        │  refines into structured plan: spec / files / acceptance
///        ▼
///     Worker (CluadeX via named pipe)
///        │  runs the 48-tool agent loop, returns final reply
///        ▼
///     TaskRunResult
///
/// Why a delegate planner (instead of a typed AiHubService dependency):
/// the WPF client lives in a separate process from the AiHubService
/// (which runs inside ObsidianX.Server). The client reaches the hub via
/// HTTP at <c>/api/ai/chat</c>. By accepting the planner as a
/// <c>Func&lt;string, CancellationToken, Task&lt;InternPlan&gt;&gt;</c>
/// we keep this orchestrator transport-agnostic — it can be wired to
/// HTTP, in-proc, gRPC, or a stub for tests, without recompiling Core.
///
/// Why <see cref="IProgress{T}"/>: the WPF UI subscribes once and
/// receives bubble-shaped events on the dispatcher thread. Decouples
/// the orchestrator's threading from how the UI renders updates, and
/// gives a clean seam for headless / CLI consumers.
/// </summary>
public sealed class CoPilotOrchestrator
{
    /// <summary>Local-model planner. Takes the user's raw spec, returns
    /// a parsed <see cref="InternPlan"/>. Implementations should call
    /// <see cref="InternPlan.ParseFromLlm"/> to handle imperfect JSON
    /// gracefully — small models can't always close their braces.</summary>
    public Func<string, CancellationToken, Task<InternPlan>>? Planner { get; init; }

    /// <summary>CluadeX worker client. Required.</summary>
    public CluadeXClient? Worker { get; init; }

    /// <summary>Optional. When set, every worker output is queued for the
    /// senior reviewer (Claude Desktop) and the orchestrator polls for a
    /// verdict before returning. When null, Phase 3 is skipped and the
    /// orchestrator returns as soon as the worker finishes — useful for
    /// experiments and the Phase 1B workflow.</summary>
    public ReviewQueueClient? ReviewQueue { get; init; }

    /// <summary>How often to re-read the queue file while waiting for a
    /// verdict. 3 s is responsive enough for an interactive UI without
    /// pegging the disk.</summary>
    public TimeSpan VerdictPollInterval { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>Optional. Self-improvement loop's CAPTURE side. When set,
    /// any task that ends with status=Done after at least one revise
    /// round triggers extraction of generalisable lessons saved as
    /// markdown notes under Notes/Coding-Lessons/. Future tasks pick
    /// these up via the Injector.</summary>
    public LessonExtractor? Extractor { get; init; }

    /// <summary>Optional. Self-improvement loop's INJECT side. Before
    /// the first worker call, look up brain lessons matching the user's
    /// spec and prepend them to the worker's lessons[] payload.</summary>
    public LessonInjector? Injector { get; init; }

    public OrchestrationOptions Options { get; init; } = new();

    /// <summary>Run one orchestrated task end-to-end. Always returns a
    /// <see cref="TaskRunResult"/> — failures are reported via
    /// <see cref="TaskRunStatus.Failed"/> / <see cref="TaskRunStatus.TimedOut"/>
    /// rather than thrown, so the UI can render them in the same bubble
    /// flow as success.</summary>
    public async Task<TaskRunResult> RunAsync(
        string userSpec,
        string? workingDirectory,
        IProgress<OrchestrationEvent>? progress,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userSpec))
            throw new ArgumentException("userSpec cannot be empty", nameof(userSpec));
        if (Planner is null) throw new InvalidOperationException("Planner not configured");
        if (Worker is null) throw new InvalidOperationException("Worker not configured");

        // InvariantCulture so the Thai calendar (พ.ศ.) doesn't turn 2026
        // into "69". Also use UtcNow so two machines collaborating on the
        // same orchestration won't end up with conflicting ids in mixed
        // timezones — the id is opaque to the user, only timestamps in
        // the bubble headers are localised for display.
        string taskId = "task-" + DateTime.UtcNow.ToString(
            "yyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        var started = DateTime.UtcNow;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        progress?.Report(new TaskCreated(DateTime.UtcNow, taskId, userSpec));

        // Hard wall-clock budget. Linked CTS so callers can still cancel
        // manually; we layer the budget on top.
        using var budgetCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budgetCts.CancelAfter(Options.MaxWallClock);
        var token = budgetCts.Token;

        // ─── Phase 1: intern plans ────────────────────────────────────
        InternPlan plan;
        try
        {
            progress?.Report(new InternStarted(DateTime.UtcNow));
            plan = await Planner(userSpec, token);
            progress?.Report(new InternFinished(DateTime.UtcNow, plan));
        }
        catch (OperationCanceledException) when (budgetCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            progress?.Report(new OrchestratorError(DateTime.UtcNow, "intern", "wall-clock budget exhausted during planning"));
            progress?.Report(new OrchestratorFinished(DateTime.UtcNow, TaskRunStatus.TimedOut));
            return new TaskRunResult { TaskId = taskId, Status = TaskRunStatus.TimedOut, Started = started, Ended = DateTime.UtcNow };
        }
        catch (Exception ex)
        {
            progress?.Report(new OrchestratorError(DateTime.UtcNow, "intern", ex.Message));
            progress?.Report(new OrchestratorFinished(DateTime.UtcNow, TaskRunStatus.Failed));
            return new TaskRunResult { TaskId = taskId, Status = TaskRunStatus.Failed, Started = started, Ended = DateTime.UtcNow, Error = ex.Message };
        }

        // ─── Phase 1.5 (Phase 1D inject): brain-derived lessons ───────
        // Pull prior lessons matching this spec from the brain. Prepend
        // them to the worker's lessons[] payload — they ride alongside
        // any revise notes the reviewer accumulates within this run.
        IReadOnlyList<string> brainLessons = Array.Empty<string>();
        if (Injector != null)
        {
            try
            {
                brainLessons = Injector.SuggestForSpec(userSpec);
                if (brainLessons.Count > 0)
                    progress?.Report(new LessonsInjected(DateTime.UtcNow, brainLessons.Count));
            }
            catch
            {
                // Injection is best-effort; never fail a task on it.
                brainLessons = Array.Empty<string>();
            }
        }

        // ─── Phase 2 + 3: worker codes, optional reviewer in a loop ───
        //
        // When ReviewQueue is configured, this loop runs until:
        //   • approved verdict   → ship it, status = Done
        //   • rejected verdict   → bail, status = Failed (reviewer notes)
        //   • max revise rounds  → bail, status = Failed (round cap)
        //   • wall-clock exhausts → bail, status = TimedOut
        //
        // When ReviewQueue is null, the loop runs once with no review and
        // exits as soon as the worker returns — preserving the original
        // Phase 1B behaviour for callers that opted out.
        var revisionLessons = new List<string>();
        var rounds = new List<RoundRecord>(); // for lesson extraction post-Done
        string workerOutput = "";
        ReviewItem? lastVerdict = null;
        int round = 1;
        while (true)
        {
            // ─── Worker call ─────────────────────────────────────────
            // Distinct CluadeX session id per round so the user can
            // scroll back and compare ‑r1, ‑r2 etc. The review-queue id
            // stays the SAME so the file overwrites cleanly on resubmit.
            string cluadeSessionId = round == 1 ? taskId : $"{taskId}-r{round}";
            string workerSpec = BuildWorkerSpec(plan, round, lastVerdict);
            try
            {
                progress?.Report(new WorkerStarted(DateTime.UtcNow, cluadeSessionId, round));
                int remainingMs = Math.Max(5_000, (int)Math.Min(int.MaxValue, (Options.MaxWallClock - sw.Elapsed).TotalMilliseconds));

                // Combine brain-derived lessons (constant for the whole
                // task) with this run's revise notes (grow per round).
                // Brain lessons go FIRST so the worker reads timeless
                // guidance before this run's specific corrections.
                var combined = brainLessons.Concat(revisionLessons).ToList();

                workerOutput = await Worker.WriteCodeAsync(
                    cluadeSessionId,
                    workerSpec,
                    lessons: combined.Count > 0 ? combined : null,
                    contextFiles: plan.Files,
                    workingDirectory: workingDirectory,
                    timeoutMs: remainingMs,
                    ct: token);
                progress?.Report(new WorkerFinished(DateTime.UtcNow, workerOutput, round));
            }
            catch (OperationCanceledException) when (budgetCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                progress?.Report(new OrchestratorError(DateTime.UtcNow, "worker", $"wall-clock budget exhausted during round {round}"));
                progress?.Report(new OrchestratorFinished(DateTime.UtcNow, TaskRunStatus.TimedOut));
                return new TaskRunResult { TaskId = taskId, Status = TaskRunStatus.TimedOut, Started = started, Ended = DateTime.UtcNow, Plan = plan, RevisionsUsed = round - 1 };
            }
            catch (Exception ex)
            {
                progress?.Report(new OrchestratorError(DateTime.UtcNow, "worker", ex.Message));
                progress?.Report(new OrchestratorFinished(DateTime.UtcNow, TaskRunStatus.Failed));
                return new TaskRunResult { TaskId = taskId, Status = TaskRunStatus.Failed, Started = started, Ended = DateTime.UtcNow, Plan = plan, Error = ex.Message, RevisionsUsed = round - 1 };
            }

            // ─── Review (optional) ───────────────────────────────────
            if (ReviewQueue is null)
            {
                // No review path — record this round (with no verdict
                // notes since there's no reviewer) and exit.
                rounds.Add(new RoundRecord(round, workerOutput, null));
                break;
            }

            try
            {
                ReviewQueue.Submit(new ReviewSubmission
                {
                    TaskId = taskId,
                    Intent = userSpec,
                    Spec = plan.Spec,
                    Diff = workerOutput,
                    Files = plan.Files,
                    TranscriptRef = cluadeSessionId,
                    RevisionRound = round,
                    PreviousOutput = round > 1 ? null : null, // could include older diff later
                });
                progress?.Report(new ReviewSubmitted(DateTime.UtcNow, taskId, round));
            }
            catch (Exception ex)
            {
                progress?.Report(new OrchestratorError(DateTime.UtcNow, "review-submit", ex.Message));
                progress?.Report(new OrchestratorFinished(DateTime.UtcNow, TaskRunStatus.Failed));
                return new TaskRunResult { TaskId = taskId, Status = TaskRunStatus.Failed, Started = started, Ended = DateTime.UtcNow, Plan = plan, WorkerOutput = workerOutput, Error = ex.Message, RevisionsUsed = round - 1 };
            }

            ReviewItem? verdict;
            try
            {
                verdict = await ReviewQueue.WaitForVerdictAsync(taskId, VerdictPollInterval, token);
            }
            catch (OperationCanceledException) when (budgetCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                progress?.Report(new OrchestratorError(DateTime.UtcNow, "review-wait", "wall-clock budget exhausted while waiting for reviewer verdict"));
                progress?.Report(new OrchestratorFinished(DateTime.UtcNow, TaskRunStatus.TimedOut));
                return new TaskRunResult { TaskId = taskId, Status = TaskRunStatus.TimedOut, Started = started, Ended = DateTime.UtcNow, Plan = plan, WorkerOutput = workerOutput, RevisionsUsed = round - 1 };
            }

            if (verdict is null)
            {
                // Cancelled by caller (not budget) — bubble up as cancelled.
                progress?.Report(new OrchestratorError(DateTime.UtcNow, "review-wait", "cancelled while waiting for verdict"));
                progress?.Report(new OrchestratorFinished(DateTime.UtcNow, TaskRunStatus.Failed));
                return new TaskRunResult { TaskId = taskId, Status = TaskRunStatus.Failed, Started = started, Ended = DateTime.UtcNow, Plan = plan, WorkerOutput = workerOutput, RevisionsUsed = round - 1 };
            }

            lastVerdict = verdict;
            // Capture this round's full record now that we know the
            // reviewer's verdict notes — the lesson extractor needs the
            // pair (output, verdict) per round to learn from.
            rounds.Add(new RoundRecord(round, workerOutput, verdict.VerdictNotes));
            progress?.Report(new ReviewVerdict(DateTime.UtcNow, verdict.Verdict ?? verdict.Status, verdict.VerdictNotes, round));

            string verdictKind = (verdict.Verdict ?? verdict.Status ?? "").ToLowerInvariant();
            if (verdictKind == "approved")
                break; // ship it

            if (verdictKind == "rejected")
            {
                progress?.Report(new OrchestratorFinished(DateTime.UtcNow, TaskRunStatus.Failed));
                return new TaskRunResult { TaskId = taskId, Status = TaskRunStatus.Failed, Started = started, Ended = DateTime.UtcNow, Plan = plan, WorkerOutput = workerOutput, Error = "Reviewer rejected: " + (verdict.VerdictNotes ?? "(no notes)"), RevisionsUsed = round - 1 };
            }

            // verdictKind == "revise"
            if (round >= Options.MaxReviseRounds)
            {
                progress?.Report(new OrchestratorError(DateTime.UtcNow, "review", $"max revise rounds ({Options.MaxReviseRounds}) reached without approval"));
                progress?.Report(new OrchestratorFinished(DateTime.UtcNow, TaskRunStatus.Failed));
                return new TaskRunResult { TaskId = taskId, Status = TaskRunStatus.Failed, Started = started, Ended = DateTime.UtcNow, Plan = plan, WorkerOutput = workerOutput, Error = $"Reviewer asked for revision past round-{Options.MaxReviseRounds} cap", RevisionsUsed = round };
            }

            revisionLessons.Add($"Round {round} reviewer (revise) notes: {verdict.VerdictNotes ?? "(no notes provided)"}");
            round++;
            // loop continues — re-call worker with the accumulated notes
        }

        sw.Stop();

        // ─── Phase 4 (Phase 1D capture): extract lessons ─────────────
        // Only meaningful when there was at least one revise round —
        // a single approved round has no negative example to learn from.
        // Best-effort: extraction failure must not change the run's
        // success status.
        if (Extractor != null && rounds.Count >= 2)
        {
            try
            {
                var finalNotes = lastVerdict?.VerdictNotes ?? "";
                var saved = await Extractor.ExtractAndSaveAsync(taskId, plan, rounds, finalNotes, ct);
                if (saved.Count > 0)
                {
                    progress?.Report(new LessonsCaptured(DateTime.UtcNow, saved.Count, saved.Select(s => s.Path).ToList()));
                }
            }
            catch (Exception ex)
            {
                // Don't surface as OrchestratorError — that would imply
                // the run failed. Just log a soft notice.
                progress?.Report(new LessonsCaptured(DateTime.UtcNow, 0, [$"extraction failed: {ex.Message}"]));
            }
        }

        progress?.Report(new OrchestratorFinished(DateTime.UtcNow, TaskRunStatus.Done));
        return new TaskRunResult
        {
            TaskId = taskId,
            Status = TaskRunStatus.Done,
            Started = started,
            Ended = DateTime.UtcNow,
            Elapsed = sw.Elapsed,
            Plan = plan,
            WorkerOutput = workerOutput,
            RevisionsUsed = round - 1,
        };
    }

    /// <summary>
    /// Standard prompt the planner delegate should send to the local
    /// model. Exposed so callers can adapt the wrapping (system vs user
    /// role, history shape) for the transport they're using.
    /// </summary>
    public static string BuildPlannerPrompt(string userSpec) =>
        $$"""
        You are an intern engineer who breaks a coding request down for a senior coder. Output ONLY a JSON object — no prose, no markdown fences — with these fields:

          {
            "spec": "<one to three sentences refining the request for the senior>",
            "files": ["<repo-relative paths or globs the senior should likely touch>"],
            "acceptance": ["<bulleted criteria for done — 1 to 4 items>"],
            "rationale": "<why this approach, one sentence>"
          }

        If the request is too vague to plan, set "spec" to a clarifying question and leave the arrays empty.

        REQUEST:
        {{userSpec}}
        """;

    private static string BuildWorkerSpec(InternPlan plan, int round = 1, ReviewItem? lastVerdict = null)
    {
        var sb = new StringBuilder();
        sb.Append(plan.Spec);
        if (plan.Acceptance.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine();
            sb.AppendLine("Acceptance criteria:");
            foreach (var a in plan.Acceptance) sb.AppendLine("- " + a);
        }
        if (!string.IsNullOrWhiteSpace(plan.Rationale))
        {
            sb.AppendLine();
            sb.Append("Rationale from intern: ").Append(plan.Rationale);
        }
        // On revise rounds, prepend reviewer notes prominently. Worker
        // sessions are fresh — they can't see the previous round, so the
        // notes have to ride in the spec itself.
        if (round > 1 && lastVerdict != null && !string.IsNullOrWhiteSpace(lastVerdict.VerdictNotes))
        {
            sb.AppendLine();
            sb.AppendLine();
            sb.AppendLine($"REVISE ROUND {round} — the senior reviewer rejected your previous attempt with these notes:");
            sb.AppendLine(lastVerdict.VerdictNotes);
            sb.AppendLine();
            sb.AppendLine("Address these specifically in this round. Don't restate the original spec — just fix the issues called out above.");
        }
        return sb.ToString();
    }
}

/// <summary>
/// Structured plan produced by the local-LLM intern. <see cref="ParseFromLlm"/>
/// is intentionally permissive — small models often emit broken JSON or
/// wrap output in code fences. The fallback path treats free-form text
/// as a single-spec plan so the worker still receives a sensible
/// delegation rather than failing the whole run.
/// </summary>
public sealed record InternPlan(
    string Spec,
    IReadOnlyList<string> Files,
    IReadOnlyList<string> Acceptance,
    string Rationale)
{
    /// <summary>Rendered for a human-facing UI bubble. Keep markdown-ish
    /// so a Markdown viewer can format it; consumers that show plain
    /// text will still get a readable layout because of the bullet
    /// glyphs.</summary>
    public string ToDisplay()
    {
        var sb = new StringBuilder();
        sb.AppendLine("**Spec:** " + Spec);
        if (Files.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("**Files:** " + string.Join(", ", Files));
        }
        if (Acceptance.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("**Acceptance:**");
            foreach (var a in Acceptance) sb.AppendLine("• " + a);
        }
        if (!string.IsNullOrWhiteSpace(Rationale))
        {
            sb.AppendLine();
            sb.AppendLine("**Why:** " + Rationale);
        }
        return sb.ToString().TrimEnd();
    }

    public static InternPlan ParseFromLlm(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return new InternPlan("(empty plan from intern)", Array.Empty<string>(), Array.Empty<string>(), "");

        // Strip code fences like ```json … ``` if present.
        var trimmed = raw.Trim();
        var firstFence = trimmed.IndexOf("```", StringComparison.Ordinal);
        if (firstFence >= 0)
        {
            var lastFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
            if (lastFence > firstFence)
            {
                var inside = trimmed.Substring(firstFence + 3, lastFence - firstFence - 3);
                // Drop a single language tag on the first line, e.g. "json\n".
                var nl = inside.IndexOf('\n');
                if (nl >= 0 && nl < 14 && inside[..nl].Trim().All(char.IsLetter))
                    inside = inside[(nl + 1)..];
                trimmed = inside.Trim();
            }
        }

        // Find the first JSON object. Models sometimes prepend "Here's the
        // plan:" before the {.
        int openBrace = trimmed.IndexOf('{');
        int closeBrace = trimmed.LastIndexOf('}');
        if (openBrace < 0 || closeBrace <= openBrace)
        {
            return new InternPlan(
                Spec: raw.Trim(),
                Files: Array.Empty<string>(),
                Acceptance: Array.Empty<string>(),
                Rationale: "(intern emitted free-form text, not JSON — passing as-is)");
        }
        var json = trimmed.Substring(openBrace, closeBrace - openBrace + 1);
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string spec = root.TryGetProperty("spec", out var s) ? (s.GetString() ?? "") : "";
            string why = root.TryGetProperty("rationale", out var r) ? (r.GetString() ?? "") : "";

            var files = root.TryGetProperty("files", out var f) && f.ValueKind == JsonValueKind.Array
                ? f.EnumerateArray()
                    .Select(x => x.ValueKind == JsonValueKind.String ? (x.GetString() ?? "") : "")
                    .Where(x => x.Length > 0)
                    .ToArray()
                : Array.Empty<string>();
            var acc = root.TryGetProperty("acceptance", out var a) && a.ValueKind == JsonValueKind.Array
                ? a.EnumerateArray()
                    .Select(x => x.ValueKind == JsonValueKind.String ? (x.GetString() ?? "") : "")
                    .Where(x => x.Length > 0)
                    .ToArray()
                : Array.Empty<string>();

            if (string.IsNullOrWhiteSpace(spec)) spec = raw.Trim();
            return new InternPlan(spec, files, acc, why);
        }
        catch
        {
            return new InternPlan(
                Spec: raw.Trim(),
                Files: Array.Empty<string>(),
                Acceptance: Array.Empty<string>(),
                Rationale: "(intern emitted invalid JSON — passing raw as spec)");
        }
    }
}

public sealed class OrchestrationOptions
{
    /// <summary>Per-task ceiling on the worker's iteration count.
    /// Forwarded to CluadeX (which has its own 15-iter cap; this is a
    /// belt-and-braces upper bound).</summary>
    public int MaxTurnsPerTask { get; set; } = 15;

    /// <summary>Hard end-to-end deadline (intern + worker combined).</summary>
    public TimeSpan MaxWallClock { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Informational only until Anthropic billing is wired in;
    /// surfaces in the UI gauge so the user has an at-a-glance budget
    /// awareness for cloud-provider runs.</summary>
    public decimal MaxUsdPerTask { get; set; } = 0.20m;

    /// <summary>How many times the reviewer (Phase 1C) may send the
    /// worker back for revisions before escalating to the user.</summary>
    public int MaxReviseRounds { get; set; } = 3;
}

public enum TaskRunStatus
{
    Planning,
    Working,
    Done,
    Failed,
    TimedOut,
}

public sealed class TaskRunResult
{
    public string TaskId { get; set; } = "";
    public TaskRunStatus Status { get; set; }
    public DateTime Started { get; set; }
    public DateTime Ended { get; set; }
    public TimeSpan Elapsed { get; set; }
    public InternPlan? Plan { get; set; }
    public string? WorkerOutput { get; set; }
    public string? Error { get; set; }
    /// <summary>How many revise rounds the reviewer requested before
    /// the run ended. 0 = approved on the first try (or no review).</summary>
    public int RevisionsUsed { get; set; }
}

// ─── Events ──────────────────────────────────────────────────────────
// Discriminated by record type so consumers can pattern-match. Each
// carries the timestamp it was emitted at — the UI uses this to render
// the bubble's footer ("0:23 ago") without needing a clock of its own.

public abstract record OrchestrationEvent(DateTime Ts);
public sealed record TaskCreated(DateTime Ts, string TaskId, string UserSpec) : OrchestrationEvent(Ts);
public sealed record InternStarted(DateTime Ts) : OrchestrationEvent(Ts);
public sealed record InternFinished(DateTime Ts, InternPlan Plan) : OrchestrationEvent(Ts);
public sealed record WorkerStarted(DateTime Ts, string TaskId, int Round = 1) : OrchestrationEvent(Ts);
public sealed record WorkerFinished(DateTime Ts, string Output, int Round = 1) : OrchestrationEvent(Ts);
/// <summary>Worker output has been queued for the senior reviewer. UI should
/// switch its budget gauge to "waiting for verdict…" and disable Send.</summary>
public sealed record ReviewSubmitted(DateTime Ts, string TaskId, int Round) : OrchestrationEvent(Ts);
/// <summary>Reviewer posted a verdict. UI should render a Reviewer bubble
/// with verdict colour-coded + notes. <c>Verdict</c> is one of approved /
/// revise / rejected.</summary>
public sealed record ReviewVerdict(DateTime Ts, string Verdict, string? Notes, int Round) : OrchestrationEvent(Ts);
/// <summary>Brain-derived lessons were prepended to the worker prompt.
/// Surfaces as a small status bubble so the user knows the worker is
/// running with prior corrections in scope.</summary>
public sealed record LessonsInjected(DateTime Ts, int Count) : OrchestrationEvent(Ts);
/// <summary>Lesson extractor finished — surfaces a celebratory bubble
/// when new generalisable lessons land in Notes/Coding-Lessons/.
/// <c>Count</c> = files written (0 means nothing generalisable found).</summary>
public sealed record LessonsCaptured(DateTime Ts, int Count, IReadOnlyList<string> Paths) : OrchestrationEvent(Ts);
public sealed record OrchestratorError(DateTime Ts, string Phase, string Message) : OrchestrationEvent(Ts);
public sealed record OrchestratorFinished(DateTime Ts, TaskRunStatus Status) : OrchestrationEvent(Ts);
