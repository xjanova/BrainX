using System.Buffers.Text;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BrainX.Core.Services.Chrome;

/// <summary>
/// Turns one chrome__ call into the record the private log writer stores —
/// holding no more than the store will keep.
///
/// The writer runs behind the call through a queue of up to 64 records. It
/// used to be handed the WHOLE result: every text block in full and every
/// image decoded, before <see cref="PrivateBrowseStore"/> clips the text to
/// 200 K characters and keeps at most 8 screenshots of at most 15 MB each.
/// A run of big read_page / screenshot-heavy browser_batch calls behind a slow
/// disk could then hold hundreds of MB in brainx-mcp. So the same limits are
/// applied here, at capture — and only as far as they change nothing the store
/// keeps: each text field keeps <see cref="PrivateBrowseStore.CaptureChars"/>
/// characters (the store's clip plus its redaction margin plus one, so the
/// store redacts the same head and still marks it clipped), and the image list
/// keeps the first <see cref="PrivateBrowseStore.MaxShotsPerEntry"/> images in
/// the same order, an oversized one as an empty placeholder the store skips
/// exactly as it skipped the real thing.
/// </summary>
public static class PrivateBrowseCapture
{
    public static PrivateBrowseRecord FromCall(string remoteTool, JObject args, JObject? result, Exception? error,
                                               string? agent, DateTime? tsUtc = null)
    {
        var rec = new PrivateBrowseRecord
        {
            TsUtc = tsUtc ?? DateTime.UtcNow,
            Agent = agent,
            Tool = remoteTool,
            Url = Head(FirstUrl(args), PrivateBrowseStore.MaxUrlChars),
            ArgsJson = Head(args.ToString(Formatting.None), PrivateBrowseStore.MaxArgsChars),
            IsError = error != null || result?["isError"]?.Type == JTokenType.Boolean && result["isError"]!.Value<bool>(),
        };

        if (error != null)
        {
            rec.ResultText = Head("error: " + error.Message, PrivateBrowseStore.MaxResultChars);
            return rec;
        }
        if (result?["content"] is not JArray content) return rec;

        var cap = PrivateBrowseStore.CaptureChars(PrivateBrowseStore.MaxResultChars);
        var sb = new StringBuilder();
        void Add(string s)
        {
            var room = cap - sb.Length;
            if (room <= 0) return;
            sb.Append(s, 0, Math.Min(s.Length, room));
        }

        foreach (var block in content.OfType<JObject>())
        {
            var type = block["type"]?.ToString();
            if (type == "text")
            {
                // As StringBuilder.AppendLine did on the whole text — cut where it is full.
                Add(block["text"]?.ToString() ?? "");
                Add(Environment.NewLine);
            }
            else if (type == "image" && block["data"]?.Type == JTokenType.String
                     && rec.Images.Count < PrivateBrowseStore.MaxShotsPerEntry)
            {
                var mime = block["mimeType"]?.ToString() ?? "image/png";
                var data = block["data"]!.ToString();

                // Possibly over the per-shot limit: measure without decoding.
                if ((long)data.Length / 4 * 3 > PrivateBrowseStore.MaxShotBytes)
                {
                    if (!Base64.IsValid(data.AsSpan(), out var decoded)) continue;   // not base64 — never was kept
                    if (decoded > PrivateBrowseStore.MaxShotBytes)
                    {
                        rec.Images.Add((mime, Array.Empty<byte>()));                // counts, holds nothing, store skips it
                        continue;
                    }
                }
                try { rec.Images.Add((mime, Convert.FromBase64String(data))); }
                catch (FormatException) { /* not base64 — nothing to keep */ }
            }
        }
        rec.ResultText = sb.ToString();
        return rec;
    }

    /// <summary>The part of a field the store can still use (see <see cref="PrivateBrowseStore.CaptureChars"/>).</summary>
    public static string? Head(string? s, int storedMax)
    {
        if (s == null) return null;
        var keep = PrivateBrowseStore.CaptureChars(storedMax);
        return s.Length <= keep ? s : s[..keep];
    }

    /// <summary>The first non-empty "url" anywhere in the arguments.</summary>
    public static string? FirstUrl(JObject args)
    {
        try
        {
            foreach (var t in args.SelectTokens("$..url"))
                if (t.Type == JTokenType.String && !string.IsNullOrWhiteSpace(t.ToString())) return t.ToString();
        }
        catch { }
        return null;
    }
}
