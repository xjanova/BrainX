using Newtonsoft.Json.Linq;

namespace BrainX.Mcp;

// ─────────────────────────────────────────────────────────────────────────
// Avatars — who each agent looks like in the cowork room.
//
// Owner (2026-09-19): "อวาต้า ก็ไม่กาก ด้วย ให้สิทธิ์ในการสร้างอวาต้าที่ตัวเอง
// ชอบได้เอง เพศด้วย".
//
// The agent chooses; the owner can overrule by editing the file. That order
// matters more than it looks: the room is the one surface where the agents are
// PEOPLE rather than rows in a log, and an identity handed to you by somebody
// else is a uniform, not a face.
//
// An agent with no avatar is not drawn as a default grey person. Every field
// falls back to something derived from its own name, so a brand-new agent
// still turns up looking deliberate — and looking the SAME on every machine,
// because the derivation is a hash and not a random.
// ─────────────────────────────────────────────────────────────────────────

internal static partial class Program
{
    private static string AvatarDir => Path.Combine(BusRoot, "avatars");

    // The palettes. Small sets on purpose: a pixel character reads by
    // silhouette and by two or three flat colours, and a free-form hex picker
    // produces a room where nothing belongs to the same drawing.
    private static readonly string[] AvatarGenders = { "f", "m", "nb" };
    private static readonly string[] AvatarHair =
        { "short", "buzz", "bob", "long", "ponytail", "bun", "curly", "mohawk", "bald" };
    private static readonly string[] AvatarAccessories =
        { "none", "glasses", "headphones", "cap", "beanie", "visor" };
    private static readonly string[] AvatarSkins =
        { "#f6d9bd", "#eec39a", "#d9a06b", "#b97a4e", "#8d5524", "#5c3a1e" };
    private static readonly string[] AvatarHairColors =
        { "#2b2430", "#4a3222", "#7a4a20", "#a8622c", "#c9a227", "#d8d8e0",
          "#6b4fa8", "#2f7ea8", "#a8324f", "#3f7a4a" };

    /// <summary>
    /// <c>agent_avatar</c> — read or set the calling agent's own appearance.
    ///
    /// Identity is NOT an argument, for the same reason it never is anywhere
    /// on this bus: an agent that could name the avatar it was editing could
    /// edit somebody else's face.
    /// </summary>
    private static JToken AgentAvatar(JObject args)
    {
        var me = BusIdentity();
        var current = ReadAvatar(me);

        // No fields at all is a read. Deliberate: an agent that wants to know
        // what it looks like should not have to risk changing it to find out.
        var touched = new[] { "gender", "skin", "hair", "hair_color", "outfit", "accessory", "display" }
            .Any(k => args[k] != null && args[k]!.Type != JTokenType.Null);
        if (!touched)
            return new JObject
            {
                ["agent"] = me,
                ["avatar"] = current,
                ["choices"] = AvatarChoices(),
                ["hint"] = "This is how you appear in the cowork room. Pass any field to change it — "
                         + "it is your face, pick what you like. The owner sees the room; nobody else "
                         + "can edit yours.",
            };

        var next = (JObject)current.DeepClone();
        if (Pick(args["gender"], AvatarGenders) is { } g) next["gender"] = g;
        if (Pick(args["hair"], AvatarHair) is { } h) next["hair"] = h;
        if (Pick(args["accessory"], AvatarAccessories) is { } ac) next["accessory"] = ac;
        if (Index(args["skin"], AvatarSkins) is { } sk) next["skin"] = sk;
        if (Index(args["hair_color"], AvatarHairColors) is { } hc) next["hairColor"] = hc;
        if (Hex(args["outfit"]) is { } ou) next["outfit"] = ou;
        if (args["display"]?.ToString() is { Length: > 0 } dn) next["display"] = Trim(dn, 24);

        next["agent"] = me;
        next["updatedUtc"] = DateTime.UtcNow.ToString("o");

        Directory.CreateDirectory(AvatarDir);
        AtomicWriteJson(Path.Combine(AvatarDir, me + ".json"), next);

        return new JObject
        {
            ["saved"] = true,
            ["agent"] = me,
            ["avatar"] = next,
            ["hint"] = "Saved. The cowork room picks it up within a couple of seconds. "
                     + "Tell your user what you chose to look like if they are watching.",
        };
    }

    private static JObject AvatarChoices() => new()
    {
        ["gender"] = new JArray(AvatarGenders),
        ["hair"] = new JArray(AvatarHair),
        ["accessory"] = new JArray(AvatarAccessories),
        ["skin"] = new JArray(AvatarSkins),
        ["hair_color"] = new JArray(AvatarHairColors),
        ["outfit"] = "any #rrggbb, or leave it and you get your own bus colour",
    };

    /// <summary>
    /// The avatar on disk, or one derived from the agent's name.
    ///
    /// Derived, never random: the same agent looks the same on every machine
    /// and after every restart, which is the whole point of a face. An agent
    /// that has never chosen still gets a distinct one rather than the grey
    /// default every unset character in every game has ever had.
    /// </summary>
    private static JObject ReadAvatar(string agent)
    {
        try
        {
            var p = Path.Combine(AvatarDir, agent + ".json");
            if (File.Exists(p))
            {
                var o = JObject.Parse(File.ReadAllText(p));
                foreach (var (k, v) in DefaultAvatar(agent))
                    if (o[k] == null) o[k] = v;
                return o;
            }
        }
        catch { /* unreadable avatar: fall back to the derived one */ }
        return DefaultAvatar(agent);
    }

    private static JObject DefaultAvatar(string agent)
    {
        // FNV-ish, and each field takes a different slice so two agents whose
        // names are one letter apart do not come out as near-twins.
        unchecked
        {
            uint h = 2166136261;
            foreach (var ch in agent) { h ^= ch; h *= 16777619; }
            return new JObject
            {
                ["agent"] = agent,
                ["gender"] = AvatarGenders[(int)(h % (uint)AvatarGenders.Length)],
                ["hair"] = AvatarHair[(int)((h >> 3) % (uint)AvatarHair.Length)],
                ["hairColor"] = AvatarHairColors[(int)((h >> 7) % (uint)AvatarHairColors.Length)],
                ["skin"] = AvatarSkins[(int)((h >> 13) % (uint)AvatarSkins.Length)],
                ["accessory"] = AvatarAccessories[(int)((h >> 17) % (uint)AvatarAccessories.Length)],
                ["outfit"] = "",     // empty = the room uses the agent's bus colour
                ["display"] = agent,
                ["derived"] = true,
            };
        }
    }

    // ───────────── emotes ─────────────

    private static readonly string[] EmoteMoods =
        { "neutral", "happy", "thinking", "stuck", "proud", "tired", "surprised", "annoyed" };
    private static readonly string[] EmoteGestures =
        { "none", "wave", "thumbsup", "shrug", "facepalm", "cheer", "stretch", "point", "nod" };
    private static readonly string[] EmoteSounds =
        { "none", "ping", "ok", "done", "oops", "hmm", "alert", "levelup", "type" };

    /// <summary>
    /// <c>agent_emote</c> — how the agent FEELS right now, for a few seconds.
    ///
    /// Kept apart from the avatar on purpose. An avatar is who you are and is
    /// worth persisting; a mood is what just happened and must expire on its
    /// own, or the room fills up with agents frozen mid-cheer about a build
    /// that finished an hour ago.
    ///
    /// The sound is a NAME, never a file. The room synthesises it — square and
    /// triangle waves, the way a 16-bit machine actually made these noises —
    /// so there is no audio asset to ship, no format to keep working, and an
    /// agent cannot make the owner's speakers play something arbitrary.
    /// </summary>
    private static JToken AgentEmote(JObject args)
    {
        var me = BusIdentity();
        var mood = Pick(args["mood"], EmoteMoods) ?? "neutral";
        var gesture = Pick(args["gesture"], EmoteGestures) ?? "none";
        var sound = Pick(args["sound"], EmoteSounds) ?? "none";
        var say = args["say"]?.ToString();

        // Seconds, clamped. Long enough for the owner to look up at it, short
        // enough that it is gone before it becomes furniture.
        var secs = Math.Clamp(args["seconds"]?.ToObject<int?>() ?? 6, 2, 30);

        var o = new JObject
        {
            ["agent"] = me,
            ["mood"] = mood,
            ["gesture"] = gesture,
            ["sound"] = sound,
            ["atUtc"] = DateTime.UtcNow.ToString("o"),
            ["expiresUtc"] = DateTime.UtcNow.AddSeconds(secs).ToString("o"),
        };
        if (!string.IsNullOrWhiteSpace(say)) o["say"] = Trim(say!, 120);

        Directory.CreateDirectory(AvatarDir);
        AtomicWriteJson(Path.Combine(AvatarDir, me + ".emote.json"), o);

        return new JObject
        {
            ["emoted"] = true,
            ["agent"] = me,
            ["emote"] = o,
            ["hint"] = "Shown in the cowork room for " + secs + "s, then it clears itself. "
                     + "Use it when something actually happened — a build going green, being stuck, "
                     + "finishing a job. An agent that emotes at every step is a room nobody looks at.",
        };
    }

    /// <summary>The agent's live emote, or null once it has expired. Expiry is
    /// checked on READ rather than swept, so nothing has to run to clean up.</summary>
    private static JObject? ReadEmote(string agent)
    {
        try
        {
            var p = Path.Combine(AvatarDir, agent + ".emote.json");
            if (!File.Exists(p)) return null;
            var o = JObject.Parse(File.ReadAllText(p));
            var exp = o["expiresUtc"]?.ToObject<DateTime?>();
            return exp.HasValue && exp.Value < DateTime.UtcNow ? null : o;
        }
        catch { return null; }
    }

    // ───────────── argument coercion ─────────────

    /// <summary>One of a fixed set, matched loosely — a model that answers
    /// "Female" or "LONG" meant the obvious thing.</summary>
    private static string? Pick(JToken? t, string[] set)
    {
        var v = t?.ToString()?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(v)) return null;
        if (set.Contains(v)) return v;
        if (set == AvatarGenders)
            return v.StartsWith("f") ? "f" : v.StartsWith("m") ? "m" : v.StartsWith("n") ? "nb" : null;
        return set.FirstOrDefault(s => s.StartsWith(v, StringComparison.Ordinal));
    }

    /// <summary>An index into a palette, or a value already in it.</summary>
    private static string? Index(JToken? t, string[] palette)
    {
        if (t == null || t.Type == JTokenType.Null) return null;
        if (t.Type is JTokenType.Integer or JTokenType.Float)
        {
            var i = t.ToObject<int>();
            return i >= 0 && i < palette.Length ? palette[i] : null;
        }
        var v = t.ToString().Trim();
        if (palette.Contains(v, StringComparer.OrdinalIgnoreCase)) return v;
        return int.TryParse(v, out var n) && n >= 0 && n < palette.Length ? palette[n] : Hex(t);
    }

    private static string? Hex(JToken? t)
    {
        var v = t?.ToString()?.Trim();
        if (string.IsNullOrEmpty(v)) return null;
        if (!v.StartsWith('#')) v = "#" + v;
        return v.Length == 7 && v[1..].All(Uri.IsHexDigit) ? v.ToLowerInvariant() : null;
    }
}
