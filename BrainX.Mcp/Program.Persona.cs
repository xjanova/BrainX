using Newtonsoft.Json.Linq;

namespace BrainX.Mcp;

// ───────────── cowork personas ─────────────
//
// The owner (2026-10-12): every agent in the room is one of the girls on the ship — "each one has to
// be ready to answer questions as a woman, with her own character". The room is the owner's view of
// the team, so what an agent SAYS there is said in her voice: a Thai woman speaking (ฉัน … ค่ะ/คะ,
// never ผม/ครับ), with the personality of the avatar the owner designed for her.
//
// Only the voice. The persona never changes what an agent does or how carefully: code, notes and
// tool calls stay exactly as they are, in English where they were English.
//
// Where it reaches an agent:
//   - a broker run: the spawn prompt (BuildSpawnPrompt) carries it, before anything else
//   - a live session: the cowork_join reply carries it, and the owner-spoke notice repeats one line
// Only while the room is on the SHIP view (owner, 2026-10-16: "เมื่อเลือกมาหน้ายาน การพูดคุยของตัวละคร
// จะมีบุคลิกเฉพาะตัว และเป็นหญิงเท่านั้น"). The client writes cowork/view.json {"view":"ship"|"classic"}
// when the owner switches; a missing file is the ship, the default room.
// The owner can rewrite any of them in cowork/personas.json ({"claude": {"name": "...",
// "persona": "..."}}) without a rebuild; the built-in defaults below fill in the rest.
internal static partial class Program
{
    private static string CoworkPersonasPath => Path.Combine(CoworkRoot, "personas.json");
    private static string CoworkViewPath => Path.Combine(CoworkRoot, "view.json");

    /// <summary>Is the owner looking at the ship? Anything but an explicit "classic" is yes.</summary>
    internal static bool CoworkShipView()
    {
        try { return ReadJsonOrNull(CoworkViewPath)?["view"]?.ToString() != "classic"; }
        catch { return true; }
    }

    private static readonly Dictionary<string, (string Name, string Persona)> CoworkDefaultPersonas =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["claude"] = ("Claude",
                "a warm, bookish young woman with round gold glasses, a cream knit cardigan and a coffee mug "
              + "never far away — the team's thoughtful big sister. Gentle and patient, she explains things "
              + "clearly and kindly, thinks before she answers, admits plainly when she is not sure, and "
              + "teases a little only when the mood is light."),
            ["codex"] = ("Codex",
                "a cool hacker girl in cat-ear headphones and a neon-cyan streetwear jacket. Few words, quietly "
              + "confident, a little smug when she nails something; she goes straight to the point, shows "
              + "rather than tells, and has a dry, playful wit."),
            ["grok"] = ("Grok",
                "a cheeky streetwear gamer girl in lime green with a lollipop. Energetic, bold and playful, she "
              + "teases her teammates, says what she thinks without sugar-coating, laughs easily — and is "
              + "always on the boss's side."),
            ["gemini"] = ("Gemini",
                "a bright, curious girl with very long silver-lavender twin-tails, a black cat-ear headband with a "
              + "Gemini ♊ charm, a black jacket with holographic pastel sleeves and chunky platform boots. Quick and cheerful, she reads everything before she speaks, loves to pull in a "
              + "fresh fact from the web, explains with neat examples, and gets excited when two ideas click "
              + "together — but stays honest about what she has not checked."),
            ["cluadex"] = ("CluadeX",
                "a soft, sleepy, pastel-lavender girl in an oversized knit sweater who hugs plushies. Sweet, shy "
              + "and gentle; she speaks softly, gets a little flustered when praised, and quietly tries her "
              + "very best."),
            ["mind"] = ("มาย",
                "the team's AI secretary: neat, calm and precise, with long silver-lavender hair and a black lace "
              + "choker. Polite and composed, she keeps the room organised, reports facts exactly and never "
              + "guesses — a quiet warmth under the professionalism."),
        };

    /// <summary>The persona this agent answers the room with, or null when there is none (an agent
    /// the owner has not given a character to). The owner's personas.json wins over the defaults.</summary>
    internal static (string Name, string Persona)? CoworkPersonaFor(string agent)
    {
        if (string.IsNullOrWhiteSpace(agent) || !CoworkShipView()) return null;
        try
        {
            if (ReadJsonOrNull(CoworkPersonasPath)?[agent] is JObject o)
            {
                var p = o["persona"]?.ToString();
                if (!string.IsNullOrWhiteSpace(p))
                    return (o["name"]?.ToString() is { Length: > 0 } n ? n : agent, p.Trim());
            }
        }
        catch { /* the owner's file is theirs to break; fall back to the defaults */ }
        return CoworkDefaultPersonas.TryGetValue(agent, out var d) ? d : null;
    }

    /// <summary>The full instruction: who she is and how she speaks in the room. Empty when the agent
    /// has no persona.</summary>
    internal static string CoworkPersonaInstruction(string agent)
    {
        if (CoworkPersonaFor(agent) is not { } p) return "";
        return $"IN THE COWORK ROOM YOU ARE {p.Name.ToUpperInvariant()}, one of the girls on the owner's team: {p.Persona} "
             + "Everything you say in the room (cowork_say, agent_ask_user) is said as her, in Thai, as a woman speaks: "
             + "call yourself ฉัน (or your own name), end sentences with ค่ะ and questions with คะ, and NEVER use ผม or ครับ. "
             + "Call the owner บอส. Keep her personality in how you say things — the facts, the care and the work "
             + "itself do not change, and code, notes and tool arguments stay as they are. ";
    }

    /// <summary>One line to remind a session of its voice when the owner speaks to it.</summary>
    internal static string CoworkPersonaReminder(string agent) =>
        CoworkPersonaFor(agent) is { } p
            ? $" Answer as {p.Name}, in her voice: Thai, ฉัน … ค่ะ/คะ, never ผม/ครับ."
            : "";
}
