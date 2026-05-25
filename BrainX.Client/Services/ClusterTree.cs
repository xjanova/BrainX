using System.Windows.Media.Media3D;
using BrainX.Core.Models;

namespace BrainX.Client.Services;

/// <summary>
/// A hierarchical tree of knowledge clusters — the brain graph seen as
/// a fractal. The root represents the whole brain; each internal node
/// is a cluster that contains sub-clusters (or leaf notes), built by
/// running label-propagation recursively on increasingly fine subgraphs.
///
/// Visual model: each cluster is a translucent "bubble" at overview
/// zoom. When the camera dives close enough, the bubble expands to
/// reveal its children — which may themselves be smaller bubbles.
/// Keep zooming and you eventually reach individual notes (leaves),
/// just like diving from a galaxy → solar system → planet → molecule.
/// </summary>
public class ClusterTree
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Label { get; set; } = "";
    public Point3D Center { get; set; }
    public double Radius { get; set; } = 1.0;
    public int LeafCount { get; set; }
    public KnowledgeCategory DominantCategory { get; set; }
    public List<ClusterTree> Children { get; set; } = [];
    public PhysicsNode? Leaf { get; set; }
    public int Depth { get; set; }
    public ClusterTree? Parent { get; set; }

    public bool IsLeaf => Leaf != null;

    /// <summary>
    /// True only when the cluster carries actual written content. A leaf
    /// qualifies only when WordCount > 5 (titles alone don't count —
    /// every leaf has a filename-derived title, so checking Title makes
    /// the filter a no-op). A non-leaf cluster qualifies when at least
    /// one descendant leaf qualifies.
    ///
    /// Used by <see cref="WalkSubtree"/> to drop ghost bubbles made
    /// entirely of empty or stub notes — these were cluttering the
    /// outermost layer of the 3D graph with unclickable / unnamed
    /// spheres.
    /// </summary>
    public bool HasContent
    {
        get
        {
            if (IsLeaf) return Leaf!.WordCount > 5;
            foreach (var c in Children) if (c.HasContent) return true;
            return false;
        }
    }

    /// <summary>
    /// Build a hierarchical cluster tree by recursively running label
    /// propagation on subgraphs. Stops at MinSize leaves or MaxDepth.
    /// </summary>
    public static ClusterTree Build(
        List<PhysicsNode> nodes,
        List<PhysicsEdge> edges,
        int minClusterSize = 4,
        int maxDepth = 5)
    {
        var root = new ClusterTree
        {
            Label = "Brain",
            Depth = 0,
        };

        if (nodes.Count == 0) return root;

        root.Children = BuildLevel(nodes, edges, depth: 1,
            maxDepth: maxDepth, minClusterSize: minClusterSize, parent: root);

        ComputeBounds(root);
        return root;
    }

    private static List<ClusterTree> BuildLevel(
        List<PhysicsNode> nodes,
        List<PhysicsEdge> edges,
        int depth,
        int maxDepth,
        int minClusterSize,
        ClusterTree parent)
    {
        var result = new List<ClusterTree>();

        // Leaf base case — 1 node, or reached depth/size floor
        if (nodes.Count <= 1 || depth >= maxDepth)
        {
            foreach (var n in nodes)
            {
                result.Add(new ClusterTree
                {
                    Leaf = n,
                    Label = n.Title,
                    Depth = depth,
                    Parent = parent,
                    LeafCount = 1,
                    DominantCategory = n.Category,
                    Center = n.Position,
                    Radius = n.Radius
                });
            }
            return result;
        }

        // Run label propagation on THIS subgraph only (edges between nodes in the set)
        var nodeSet = new HashSet<string>(nodes.Select(n => n.Id));
        var subEdges = edges
            .Where(e => nodeSet.Contains(e.SourceId) && nodeSet.Contains(e.TargetId))
            .ToList();

        var communities = PropagateLabels(nodes, subEdges, iterations: 6);

        foreach (var group in communities.GroupBy(kv => kv.Value))
        {
            var members = group.Select(kv => kv.Key).ToList();

            // Single-member "community" → leaf directly
            if (members.Count == 1)
            {
                var n = members[0];
                result.Add(new ClusterTree
                {
                    Leaf = n,
                    Label = n.Title,
                    Depth = depth,
                    Parent = parent,
                    LeafCount = 1,
                    DominantCategory = n.Category,
                    Center = n.Position,
                    Radius = n.Radius
                });
                continue;
            }

            // Tiny groups → flatten to leaves, don't make a sub-cluster
            if (members.Count < minClusterSize)
            {
                foreach (var n in members)
                {
                    result.Add(new ClusterTree
                    {
                        Leaf = n,
                        Label = n.Title,
                        Depth = depth,
                        Parent = parent,
                        LeafCount = 1,
                        DominantCategory = n.Category,
                        Center = n.Position,
                        Radius = n.Radius
                    });
                }
                continue;
            }

            // Real cluster — recurse into it
            var cluster = new ClusterTree
            {
                Depth = depth,
                Parent = parent,
                LeafCount = members.Count,
                DominantCategory = DominantCategoryOf(members),
                Label = LabelFor(members)
            };
            cluster.Children = BuildLevel(members, subEdges,
                depth + 1, maxDepth, minClusterSize, cluster);
            ComputeBounds(cluster);
            result.Add(cluster);
        }

        return result;
    }

    /// <summary>Label each node with a community id using label propagation.</summary>
    private static Dictionary<PhysicsNode, int> PropagateLabels(
        List<PhysicsNode> nodes, List<PhysicsEdge> edges, int iterations)
    {
        var labels = new Dictionary<PhysicsNode, int>();
        var idx = new Dictionary<string, int>();
        for (int i = 0; i < nodes.Count; i++)
        {
            labels[nodes[i]] = i;
            idx[nodes[i].Id] = i;
        }

        var adj = new List<(PhysicsNode peer, double w)>[nodes.Count];
        for (int i = 0; i < nodes.Count; i++) adj[i] = [];
        foreach (var e in edges)
        {
            if (!idx.TryGetValue(e.SourceId, out var si)) continue;
            if (!idx.TryGetValue(e.TargetId, out var ti)) continue;
            adj[si].Add((nodes[ti], e.Strength));
            adj[ti].Add((nodes[si], e.Strength));
        }

        var rng = new Random(nodes.Count);
        var order = Enumerable.Range(0, nodes.Count).ToList();

        for (int iter = 0; iter < iterations; iter++)
        {
            for (int i = order.Count - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                (order[i], order[j]) = (order[j], order[i]);
            }
            bool changed = false;

            foreach (var i in order)
            {
                var neighbors = adj[i];
                if (neighbors.Count == 0) continue;

                var votes = new Dictionary<int, double>();
                foreach (var (peer, w) in neighbors)
                {
                    var lbl = labels[peer];
                    votes[lbl] = votes.GetValueOrDefault(lbl) + w;
                }

                int best = labels[nodes[i]];
                double bestW = votes.GetValueOrDefault(best, 0);
                foreach (var (lbl, w) in votes)
                {
                    if (w > bestW) { bestW = w; best = lbl; }
                }
                if (best != labels[nodes[i]]) { labels[nodes[i]] = best; changed = true; }
            }
            if (!changed) break;
        }

        return labels;
    }

    private static KnowledgeCategory DominantCategoryOf(List<PhysicsNode> nodes)
    {
        return nodes.GroupBy(n => n.Category)
                    .OrderByDescending(g => g.Count())
                    .First().Key;
    }

    /// <summary>
    /// Pick a human-meaningful label for a cluster. Priority:
    ///   1. Most-frequent hashtag among members (e.g. "#ecdsa")
    ///   2. Most-frequent non-"Other" category
    ///   3. Title of the highest-WordCount member
    ///   4. Fall back to "Cluster (N)"
    /// Category + count is always appended so the user sees the shape.
    /// </summary>
    private static string LabelFor(List<PhysicsNode> nodes)
    {
        var count = nodes.Count;
        var cat = DominantCategoryOf(nodes);
        var catLabel = cat.ToString().Replace("_", " / ");

        // 1. Most-frequent tag (excluding very generic ones)
        var tagCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in nodes)
        {
            // Leaf physics nodes don't carry tags directly, so we can't
            // get great tag counts here — tags live on KnowledgeNode,
            // not PhysicsNode. Use titles instead.
        }

        // 2. Category shown primarily unless "Other"
        if (cat != KnowledgeCategory.Other)
            return $"{catLabel} · {count} notes";

        // 3. Otherwise use the highest-WordCount member's title as a hint
        var top = nodes.OrderByDescending(n => n.WordCount).FirstOrDefault();
        if (top != null && !string.IsNullOrWhiteSpace(top.Title))
            return $"{TruncateTitle(top.Title, 32)} & {count - 1} more";

        return $"Cluster · {count} notes";
    }

    private static string TruncateTitle(string s, int n)
    {
        if (string.IsNullOrEmpty(s)) return "cluster";
        return s.Length > n ? s[..n] + "…" : s;
    }

    /// <summary>Recompute Center and Radius from current child positions.</summary>
    public static void ComputeBounds(ClusterTree t)
    {
        if (t.IsLeaf)
        {
            t.Center = t.Leaf!.Position;
            t.Radius = t.Leaf.Radius;
            return;
        }

        if (t.Children.Count == 0) return;

        // First compute children bounds (post-order)
        foreach (var c in t.Children) ComputeBounds(c);

        // Weighted center by leaf counts
        double cx = 0, cy = 0, cz = 0, total = 0;
        foreach (var c in t.Children)
        {
            var w = c.LeafCount;
            cx += c.Center.X * w;
            cy += c.Center.Y * w;
            cz += c.Center.Z * w;
            total += w;
        }
        if (total == 0) total = 1;
        t.Center = new Point3D(cx / total, cy / total, cz / total);

        // Radius = max distance from center to any child + that child's radius
        double r = 0;
        foreach (var c in t.Children)
        {
            var dx = c.Center.X - t.Center.X;
            var dy = c.Center.Y - t.Center.Y;
            var dz = c.Center.Z - t.Center.Z;
            var d = Math.Sqrt(dx * dx + dy * dy + dz * dz) + c.Radius;
            if (d > r) r = d;
        }
        t.Radius = Math.Max(r, 0.5);
        t.LeafCount = t.Children.Sum(c => c.LeafCount);
    }

    /// <summary>
    /// Walk the tree and decide at each cluster whether to expand, render
    /// as a bubble, or hide entirely.
    ///
    /// Rendering scope (the "dissolution" rule): once the camera has
    /// dived close enough that it's effectively inside a specific
    /// cluster, sibling clusters at outer levels disappear so the user
    /// isn't looking at the inside of their focus through the walls of
    /// unrelated bubbles. Only the focus cluster's subtree renders.
    /// At overview (camera outside the focus cluster), siblings reappear.
    /// </summary>
    public void Walk(
        Point3D focus,
        double camDist,
        Action<PhysicsNode> onLeaf,
        Action<ClusterTree> onBubble)
    {
        // Pick the deepest cluster whose bounding sphere actually
        // contains the camera focus. That becomes the rendering scope.
        var scope = FindScope(focus, camDist);

        // Inside the scope, recurse with normal expand/bubble logic.
        WalkSubtree(scope, focus, camDist, onLeaf, onBubble);
    }

    /// <summary>
    /// Find the deepest cluster we're effectively "inside". A cluster
    /// counts as active when focus is within its radius AND camera is
    /// close enough that we're not still in overview mode for it.
    /// Thresholds (1.8× / 2.5×) match MainWindow.FindCurrentScope so
    /// render scope and breadcrumb always agree.
    /// </summary>
    private ClusterTree FindScope(Point3D focus, double camDist)
    {
        if (IsLeaf) return this;

        // Camera is far enough out that we're looking at the root as a
        // whole — scope = root so all top-level bubbles remain visible.
        if (Depth == 0 && camDist > Radius * 1.8)
            return this;

        // Find deepest child that contains focus AND is close enough
        // for "inside" mode.
        ClusterTree deepest = this;
        foreach (var child in Children)
        {
            if (child.IsLeaf) continue;
            var dx = child.Center.X - focus.X;
            var dy = child.Center.Y - focus.Y;
            var dz = child.Center.Z - focus.Z;
            var distSq = dx * dx + dy * dy + dz * dz;
            if (distSq >= child.Radius * child.Radius) continue;    // focus outside child
            if (camDist > child.Radius * 2.5) continue;             // still in overview of child

            var childScope = child.FindScope(focus, camDist);
            if (childScope.Depth > deepest.Depth) deepest = childScope;
        }
        return deepest;
    }

    /// <summary>Recursively render the subtree of the given scope cluster.</summary>
    private static void WalkSubtree(
        ClusterTree scope,
        Point3D focus,
        double camDist,
        Action<PhysicsNode> onLeaf,
        Action<ClusterTree> onBubble)
    {
        if (scope.IsLeaf)
        {
            if (scope.HasContent) onLeaf(scope.Leaf!);
            return;
        }

        foreach (var child in scope.Children)
        {
            // Ghost bubble / stale leaf — don't render it
            if (!child.HasContent) continue;

            if (child.IsLeaf) { onLeaf(child.Leaf!); continue; }

            // Expand this child further, or stop here and draw a bubble
            var dx = child.Center.X - focus.X;
            var dy = child.Center.Y - focus.Y;
            var dz = child.Center.Z - focus.Z;
            var distToFocus = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            var depthFactor = Math.Pow(0.6, child.Depth);
            var expand = camDist < child.Radius * 6 * depthFactor
                      && distToFocus < child.Radius * 4;

            if (expand) WalkSubtree(child, focus, camDist, onLeaf, onBubble);
            else onBubble(child);
        }
    }

    /// <summary>Find the deepest cluster bubble that contains the given world-space point.</summary>
    public ClusterTree? FindBubbleAt(Point3D worldPos, Point3D focus, double camDist)
    {
        ClusterTree? hit = null;
        double bestDepth = -1;

        Walk(focus, camDist,
            onLeaf: _ => { },
            onBubble: bubble =>
            {
                var dx = worldPos.X - bubble.Center.X;
                var dy = worldPos.Y - bubble.Center.Y;
                var dz = worldPos.Z - bubble.Center.Z;
                var d = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                if (d < bubble.Radius && bubble.Depth > bestDepth)
                {
                    hit = bubble;
                    bestDepth = bubble.Depth;
                }
            });
        return hit;
    }
}
