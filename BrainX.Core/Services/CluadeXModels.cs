using Newtonsoft.Json.Linq;

namespace BrainX.Core.Services;

/// <summary>
/// CluadeX in the cowork room's model panel. CluadeX is not a runner the broker starts — the owner runs it — so its row
/// comes from asking the app itself over its pipe (cluadex.get_active_model) instead of runners.json, and a pick goes
/// back to it (cluadex.choose_model) instead of into models.json. Owner (2026-10-11): "brainx เห็น claude แต่ ต้องเห็น
/// โมเดลที่ cluadex ใช้อยู่ด้วย สิ … มันตั้งไม่ได้".
/// </summary>
public static class CluadeXModels
{
    /// <summary>CluadeX's name on the bus (its presence file, its seat).</summary>
    public const string Agent = "cluadex";
    /// <summary>CluadeX's "the model decides" level: shown as the panel's default, not as a pick.</summary>
    public const string AutoEffort = "auto";

    /// <summary>
    /// The panel row, in the shape the runner rows have (office.js draws both) plus <c>app</c>, <c>current</c> and
    /// <c>note</c>. <c>chosen</c> is what CluadeX answers with NOW — not a stored pick — so the page only says
    /// "saved" once CluadeX has really switched. A model on that PC is never offered; while one answers, the row says
    /// so and picks are API models only. <paramref name="note"/> is the last refusal, shown under the row.
    /// </summary>
    public static JObject Row(CluadeXModelInfo info, string? note = null)
    {
        var options = (info.Options ?? new()).Where(o => !string.IsNullOrWhiteSpace(o.Model))
            .GroupBy(o => o.Model).Select(g => g.First()).ToList();
        bool listed = options.Any(o => o.Model == info.Model);
        var efforts = (info.Efforts ?? new()).Where(e => !string.IsNullOrWhiteSpace(e) && e != AutoEffort).ToList();
        string effort = !string.IsNullOrEmpty(info.Effort) && info.Effort != AutoEffort ? info.Effort : "";

        return new JObject
        {
            ["agent"] = Agent,
            ["app"] = "CluadeX",
            ["canChoose"] = options.Count > 0,
            ["chosen"] = listed ? info.Model : "",
            ["current"] = info.Model ?? "",
            ["currentProvider"] = info.Provider ?? "",
            ["configured"] = "",
            ["cliDefault"] = "",
            ["onCall"] = false,
            ["running"] = "",
            ["options"] = new JArray(options.Select(o => new JObject
            {
                ["id"] = o.Model,
                ["label"] = string.IsNullOrWhiteSpace(o.Label) ? o.Model : $"{o.Label} · {o.Model}",
                ["note"] = string.IsNullOrWhiteSpace(o.Price) ? "" : $"ราคา {o.Price} ต่อล้านโทเค็น (เข้า / ออก)",
            })),
            ["effortCanChoose"] = efforts.Count > 0,
            ["effortChosen"] = effort,
            ["effortConfigured"] = "",
            ["effortCliDefault"] = "",
            ["effortRunning"] = "",
            ["effortOptions"] = new JArray(efforts.Select(e => new JObject { ["id"] = e, ["label"] = e, ["note"] = "" })),
            ["note"] = note ?? "",
        };
    }

    /// <summary>A pick from the page that CluadeX can be asked for: a model it offered (or none), an effort it offered,
    /// or "" for its auto level. Anything else is dropped before it reaches the pipe.</summary>
    public static bool IsOffered(CluadeXModelInfo info, string? model, string? effort)
    {
        if (!string.IsNullOrEmpty(model) && !(info.Options ?? new()).Any(o => o.Model == model)) return false;
        if (effort != null && effort.Length > 0 && !(info.Efforts ?? new()).Contains(effort)) return false;
        return true;
    }
}
