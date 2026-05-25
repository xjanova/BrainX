using Newtonsoft.Json.Linq;
using BrainX.Core.Models;

namespace BrainX.Core.Services;

/// <summary>
/// Indexes Obsidian-compatible <c>.canvas</c> files (JSON Canvas 1.0 spec).
///
/// A canvas is a 2D arrangement of nodes (text cards, file references,
/// external links, group containers) connected by directional edges. We
/// turn the canvas itself into one <see cref="KnowledgeNode"/> so it
/// shows up alongside markdown notes in the brain graph, and emit
/// <see cref="KnowledgeEdge"/>s for two distinct relationships:
///
///   - <c>canvas-ref</c> — from the canvas node to every markdown file
///     referenced as a "file" node inside it. Tells the user "this
///     canvas pulls in those notes".
///   - <c>canvas-link</c> — between two markdown nodes that the canvas
///     joins with a JSON edge. The canvas's structural intent (these
///     two notes are related because the user drew a line between them)
///     becomes a real edge in the knowledge graph.
///
/// Reference: https://jsoncanvas.org/spec/1.0/ — open format published
/// by Obsidian, deliberately flat JSON so we can parse with JObject.
/// </summary>
public static class CanvasIndexer
{
    /// <summary>Schema check + JSON parse, swallowing format errors.</summary>
    public static JObject? TryRead(string canvasPath)
    {
        try
        {
            var text = File.ReadAllText(canvasPath);
            var obj = JObject.Parse(text);
            // The spec says both arrays are optional but at least one
            // must exist for the file to be a canvas. Empty canvas =
            // both missing = not interesting, treat as malformed.
            if (obj["nodes"] == null && obj["edges"] == null) return null;
            return obj;
        }
        catch { return null; }
    }

    /// <summary>
    /// Build a <see cref="KnowledgeNode"/> for the canvas itself plus a
    /// list of edges describing what it references and how the user
    /// drew connections between those references.
    /// <paramref name="resolveByPath"/> takes a path string (relative
    /// or filename) and returns the matching note's id, or null if no
    /// note tracks that file. It matches the same lookup KnowledgeIndexer
    /// uses for wiki-links.
    /// </summary>
    public static (KnowledgeNode canvasNode, List<KnowledgeEdge> edges) Index(
        string canvasPath, string vaultPath, Func<string, string?> resolveByPath)
    {
        var fileInfo = new FileInfo(canvasPath);
        var title = Path.GetFileNameWithoutExtension(canvasPath);

        var canvasNode = new KnowledgeNode
        {
            Id = KnowledgeNode.IdFromPath(canvasPath),
            Title = title,
            FilePath = canvasPath,
            // Canvas files are visual / structural artefacts — Design_Art
            // is the closest built-in category. The indexer's category
            // scorer would flag them as "Other" because there's no
            // markdown body to scan, so we set it explicitly here.
            PrimaryCategory = KnowledgeCategory.Design_Art,
            CreatedAt = fileInfo.CreationTimeUtc,
            ModifiedAt = fileInfo.LastWriteTimeUtc,
            // Canvas files don't have a word count in the markdown sense.
            // We approximate by counting characters in text cards so the
            // importance heuristic still produces a sensible value.
            Properties = new Dictionary<string, object?> { ["isCanvas"] = true }
        };

        var edges = new List<KnowledgeEdge>();
        var json = TryRead(canvasPath);
        if (json == null) return (canvasNode, edges);

        // Map canvas node id → the markdown note id it references (if
        // it's a file-type node and we can resolve the path). Used to
        // turn JSON-canvas edges between file nodes into KnowledgeEdges
        // between their underlying notes.
        var canvasFileNodes = new Dictionary<string, string>();
        int textCardChars = 0;

        var nodes = json["nodes"] as JArray;
        if (nodes != null)
        {
            foreach (var n in nodes)
            {
                var type = n["type"]?.ToString();
                var canvasId = n["id"]?.ToString();
                if (string.IsNullOrEmpty(canvasId)) continue;

                if (type == "text")
                {
                    var text = n["text"]?.ToString() ?? "";
                    textCardChars += text.Length;
                }
                else if (type == "file")
                {
                    var filePath = n["file"]?.ToString();
                    if (string.IsNullOrEmpty(filePath)) continue;
                    var noteId = resolveByPath(filePath);
                    if (noteId == null) continue;
                    canvasFileNodes[canvasId] = noteId;

                    // Direct edge from canvas → referenced note.
                    edges.Add(new KnowledgeEdge
                    {
                        SourceId = canvasNode.Id,
                        TargetId = noteId,
                        RelationType = "canvas-ref",
                        Strength = 0.7,
                        Alias = n["subpath"]?.ToString()
                    });
                    if (!canvasNode.LinkedNodeIds.Contains(noteId))
                        canvasNode.LinkedNodeIds.Add(noteId);
                }
                else if (type == "link")
                {
                    // External URL — we don't track external sites in
                    // the brain graph yet. Skip silently.
                }
            }
        }

        var canvasEdges = json["edges"] as JArray;
        if (canvasEdges != null)
        {
            foreach (var e in canvasEdges)
            {
                var fromId = e["fromNode"]?.ToString();
                var toId = e["toNode"]?.ToString();
                if (string.IsNullOrEmpty(fromId) || string.IsNullOrEmpty(toId)) continue;
                // Promote a canvas line into a graph edge ONLY when both
                // ends point at file-type canvas nodes that resolved to
                // tracked notes. Edges to text cards or external links
                // can't be represented in the brain graph.
                if (!canvasFileNodes.TryGetValue(fromId, out var srcNote)) continue;
                if (!canvasFileNodes.TryGetValue(toId, out var tgtNote)) continue;
                if (srcNote == tgtNote) continue;

                edges.Add(new KnowledgeEdge
                {
                    SourceId = srcNote,
                    TargetId = tgtNote,
                    RelationType = "canvas-link",
                    Strength = 0.6,
                    Alias = e["label"]?.ToString()
                });
            }
        }

        // Importance proxy: log of total text-card characters + a flat
        // boost per resolved file reference. Empty canvases stay near
        // zero; busy ones with many references rank similarly to a
        // medium markdown note.
        var approxWords = textCardChars / 5;  // rough chars→words
        canvasNode.WordCount = approxWords;
        canvasNode.Importance = Math.Log(1 + approxWords)
                              + canvasFileNodes.Count * 0.5;

        return (canvasNode, edges);
    }
}
