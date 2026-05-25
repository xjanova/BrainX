using System.Windows.Media.Media3D;
using BrainX.Core.Models;

namespace BrainX.Client.Services;

public class PhysicsNode
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public KnowledgeCategory Category { get; set; }
    public Point3D Position { get; set; }
    public Vector3D Velocity { get; set; }
    public double Mass { get; set; } = 1.0;
    public double Radius { get; set; } = 0.15;
    public bool IsDragging { get; set; }
    public bool IsHovered { get; set; }
    public double PulsePhase { get; set; }
    public int WordCount { get; set; }
    public List<string> LinkedIds { get; set; } = [];

    // Knowledge-pulse state
    public double AccessIntensity { get; set; }
    public int AccessCount { get; set; }
    public DateTime LastAccessedAt { get; set; } = DateTime.MinValue;
    /// <summary>Last MCP op: "search", "get_note", "write". Real Brain uses this
    /// to decide how deep to zoom — get_note/write = deep, search = surface.</summary>
    public string LastOp { get; set; } = "";

    // Community / cluster id (from label propagation)
    public int CommunityId { get; set; } = -1;

    // Importance score (for max-visible-nodes filter)
    public double Importance { get; set; }

    /// <summary>If set, the node matched a user-defined custom category.</summary>
    public string? CustomCategoryId { get; set; }

    // ── Lifecycle animation state ──
    /// <summary>When this node first appeared in the graph. null = pre-existing.</summary>
    public DateTime? BirthAt { get; set; }
    /// <summary>When this node was marked for removal. Still rendered while the death animation plays.</summary>
    public DateTime? DyingAt { get; set; }
    /// <summary>Last time the node's content (title/word count/links/importance) changed on disk.
    /// Used by Real Brain mode to focus the camera on edit activity in addition to MCP access.</summary>
    public DateTime? EditedAt { get; set; }

    /// <summary>Total duration of the birth animation in seconds.</summary>
    public const double BirthDurationSec = 1.8;
    /// <summary>Total duration of the death animation in seconds.</summary>
    public const double DeathDurationSec = 1.8;
    /// <summary>How long an edit stays "fresh" for Real Brain attention scoring.</summary>
    public const double EditFreshnessSec = 4.0;

    /// <summary>0 = just edited, 1 = edit fully faded. Controls the edit-pulse visual.</summary>
    public double EditProgress
    {
        get
        {
            if (EditedAt == null) return 1.0;
            var age = (DateTime.UtcNow - EditedAt.Value).TotalSeconds;
            return Math.Min(1.0, age / EditFreshnessSec);
        }
    }

    /// <summary>0 = just born, 1 = fully alive. Controls scale/glow.</summary>
    public double BirthProgress
    {
        get
        {
            if (BirthAt == null) return 1.0;
            var age = (DateTime.UtcNow - BirthAt.Value).TotalSeconds;
            return Math.Min(1.0, age / BirthDurationSec);
        }
    }

    /// <summary>1 = not dying, 0 = fully gone. Controls shrink/fade-out.</summary>
    public double DeathProgress
    {
        get
        {
            if (DyingAt == null) return 1.0;
            var age = (DateTime.UtcNow - DyingAt.Value).TotalSeconds;
            return Math.Max(0.0, 1.0 - age / DeathDurationSec);
        }
    }
}

public class PhysicsEdge
{
    public string SourceId { get; set; } = "";
    public string TargetId { get; set; } = "";
    public double RestLength { get; set; } = 2.0;
    public double Strength { get; set; } = 1.0;
    public string RelationType { get; set; } = "wiki-link";
    public bool IsAuto => RelationType.StartsWith("auto", StringComparison.Ordinal);

    /// <summary>Activity glow — bumped when an arc fires along this edge,
    /// decayed by the same plateau+fade scheme as PhysicsNode.AccessIntensity.
    /// Lets the renderer paint a persistent "trail" along recently-used
    /// edges, separate from the short-lived ElectricArc flash.</summary>
    public double AccessIntensity { get; set; }
    public DateTime LastAccessedAt { get; set; } = DateTime.MinValue;

    /// <summary>Edge formation animation — null = pre-existing.</summary>
    public DateTime? BirthAt { get; set; }

    /// <summary>Edge removal animation — null = alive.</summary>
    public DateTime? DyingAt { get; set; }

    public const double FormDurationSec = 1.2;
    public const double DeathDurationSec = 1.2;

    /// <summary>0 = just formed, 1 = fully drawn. Controls how far the edge is drawn along the line.</summary>
    public double FormProgress
    {
        get
        {
            if (BirthAt == null) return 1.0;
            var age = (DateTime.UtcNow - BirthAt.Value).TotalSeconds;
            return Math.Min(1.0, age / FormDurationSec);
        }
    }

    /// <summary>1 = alive, 0 = fully gone. Controls fade-out + shrink-toward-midpoint.</summary>
    public double DeathProgress
    {
        get
        {
            if (DyingAt == null) return 1.0;
            var age = (DateTime.UtcNow - DyingAt.Value).TotalSeconds;
            return Math.Max(0.0, 1.0 - age / DeathDurationSec);
        }
    }
}

/// <summary>
/// 3D force-directed graph layout with cluster-forming dynamics.
///
/// Forces:
///   • Fruchterman-Reingold style — attractive F = d²/k along edges,
///     repulsive F = -k²/d between all pairs (approximated via
///     Barnes-Hut octree when N is large)
///   • Per-community gravity — each cluster pulls its own members
///     toward the cluster center-of-mass, not the global origin.
///     This produces the "Obsidian-style" visual clusters.
///   • Simulated annealing temperature — large moves early, settles
///     to precise positions over time
///
/// Performance:
///   • Barnes-Hut octree (theta = 0.8) gives O(N log N) repulsion
///     vs O(N²) for N > 80 (auto-switches)
///   • Step decimation: for N > 200 the engine steps every 2nd tick;
///     N > 500 every 3rd — the renderer still runs at 60fps
///   • CommunityId cached between Rebuilds — re-detected only on
///     explicit graph reload or topology change
/// </summary>
public class PhysicsEngine
{
    public List<PhysicsNode> Nodes { get; } = [];
    public List<PhysicsEdge> Edges { get; } = [];

    /// <summary>Optional semantic-similarity springs computed by
    /// <c>SemanticSpringComputer</c>. Apply ON TOP OF structural Edges,
    /// at a small fraction of the structural spring strength so they
    /// nudge similar notes closer without overriding link topology.
    /// Empty when no embeddings are available — physics still works.</summary>
    public List<BrainX.Core.Services.SemanticSpring> SemanticSprings { get; set; } = [];

    /// <summary>Strength multiplier applied to semantic springs relative to
    /// structural ones. 0.12 means a "very similar" pair (similarity 1.0)
    /// pulls about 12% as hard as a real wiki-link does.</summary>
    public double SemanticSpringStrength { get; set; } = 0.12;

    public double Repulsion { get; set; } = 8.0;
    public double SpringStrength { get; set; } = 0.06;
    public double SpringLength { get; set; } = 2.5;
    public double Damping { get; set; } = 0.88;
    public double CenterGravity { get; set; } = 0.02;
    public double MaxVelocity { get; set; } = 2.0;
    public double BounceEnergy { get; set; } = 0.7;

    /// <summary>Per-cluster gravity strength (pulls nodes to community center).</summary>
    public double ClusterGravity { get; set; } = 0.04;

    /// <summary>FR ideal spring length — also used by Barnes-Hut for repulsion scale.</summary>
    public double IdealLength { get; set; } = 2.5;

    /// <summary>Barnes-Hut opening criterion (0 = exact O(N²), 1 = very coarse).</summary>
    public double Theta { get; set; } = 0.8;

    /// <summary>Switch to Barnes-Hut approximation when N exceeds this.</summary>
    public int BarnesHutThreshold { get; set; } = 80;

    /// <summary>Simulated annealing temperature — shrinks over time.</summary>
    public double Temperature { get; set; } = 1.0;
    public double CoolingRate { get; set; } = 0.995;

    private readonly Random _rng = new();
    private double _totalEnergy;
    private int _stepTick;
    public double TotalEnergy => _totalEnergy;
    public bool IsSettled => _totalEnergy < 0.01;

    // Per-community center-of-mass cache (rebuilt each step)
    private Dictionary<int, Point3D> _communityCenter = [];
    private Dictionary<int, int> _communityCount = [];

    /// <summary>
    /// Hierarchical cluster tree built after layout settles. Drives
    /// the fractal zoom: each bubble contains sub-bubbles or leaves.
    /// Rebuilt on LoadFromGraph and RebuildClusterTree().
    /// </summary>
    public ClusterTree? ClusterTree { get; private set; }

    public void RebuildClusterTree()
    {
        ClusterTree = ClusterTree.Build([.. Nodes], [.. Edges]);
    }

    // ── Cached ID → index map ──
    // Rebuilding this Dictionary every frame (at 60fps × 500 nodes × 2 views)
    // was a top GC-pressure hotspot causing periodic stutter. We now bump
    // NodeGeneration whenever the Nodes list mutates and lazily rebuild
    // the map only when stale.
    public int NodeGeneration { get; private set; }
    private void BumpNodeGeneration() => NodeGeneration++;

    private Dictionary<string, int>? _idToIndex;
    private int _cachedIdxGen = -1;

    /// <summary>ID → list-index map. Rebuilt only when the Nodes list changes.</summary>
    public Dictionary<string, int> IdToIndex
    {
        get
        {
            if (_idToIndex == null || _cachedIdxGen != NodeGeneration)
            {
                _idToIndex ??= new Dictionary<string, int>(Math.Max(16, Nodes.Count));
                _idToIndex.Clear();
                for (int i = 0; i < Nodes.Count; i++) _idToIndex[Nodes[i].Id] = i;
                _cachedIdxGen = NodeGeneration;
            }
            return _idToIndex;
        }
    }

    /// <summary>
    /// Diff-aware reload: keeps existing nodes' positions, marks new
    /// ones with BirthAt, marks missing ones with DyingAt (they'll still
    /// render for ~1s while the shrink-fade animation plays). Same for
    /// edges. Use this when the graph changed incrementally (file
    /// save, new import) instead of the hard Reset LoadFromGraph does.
    /// </summary>
    public void LoadFromGraphDiff(KnowledgeGraph graph)
    {
        var now = DateTime.UtcNow;
        var byId = Nodes.ToDictionary(n => n.Id);
        var newIds = new HashSet<string>(graph.Nodes.Select(n => n.Id));

        // Mark deaths — node no longer in graph
        foreach (var n in Nodes.Where(n => !newIds.Contains(n.Id)))
        {
            n.DyingAt ??= now;
        }
        // Keep dying nodes alive long enough that Real Brain can fly the
        // camera over and the user sees the death animation play out fully.
        // The renderer already fades to 0 opacity, so the extra retention
        // just holds the "ghost" slot in the graph for a few seconds.
        Nodes.RemoveAll(n => n.DyingAt.HasValue &&
            (now - n.DyingAt.Value).TotalSeconds > PhysicsNode.DeathDurationSec + 3.0);

        // Update existing + add births
        int total = Math.Max(1, graph.Nodes.Count);
        int i = 0;
        foreach (var gn in graph.Nodes)
        {
            if (byId.TryGetValue(gn.Id, out var existing))
            {
                // Detect a content edit by comparing the fields that actually
                // change when a user saves the .md file. If anything moved,
                // stamp EditedAt so Real Brain can fly the camera over.
                bool edited =
                    existing.Title != gn.Title
                    || existing.WordCount != gn.WordCount
                    || existing.Category != gn.PrimaryCategory
                    || existing.CustomCategoryId != gn.CustomCategoryId
                    || Math.Abs(existing.Importance - gn.Importance) > 0.001
                    || existing.LinkedIds.Count != gn.LinkedNodeIds.Count;

                // Preserve position + velocity; refresh metadata
                existing.Title = gn.Title;
                existing.Category = gn.PrimaryCategory;
                existing.WordCount = gn.WordCount;
                existing.Importance = gn.Importance;
                existing.LinkedIds = gn.LinkedNodeIds;
                existing.CustomCategoryId = gn.CustomCategoryId;
                existing.DyingAt = null;   // cancel pending death if it re-appeared
                if (edited) existing.EditedAt = now;
            }
            else
            {
                // Birth — place on Fibonacci sphere, flag as freshly born
                double radius = 3.0 + Math.Sqrt(total) * 0.15;
                double phi = Math.Acos(1 - 2.0 * (i + 0.5) / total);
                double theta = Math.PI * (1 + Math.Sqrt(5)) * i;
                double r = radius + (_rng.NextDouble() - 0.5) * 0.3;

                Nodes.Add(new PhysicsNode
                {
                    Id = gn.Id,
                    Title = gn.Title,
                    Category = gn.PrimaryCategory,
                    WordCount = gn.WordCount,
                    Importance = gn.Importance,
                    Position = new Point3D(
                        r * Math.Sin(phi) * Math.Cos(theta),
                        r * Math.Cos(phi),
                        r * Math.Sin(phi) * Math.Sin(theta)),
                    Velocity = new Vector3D(
                        (_rng.NextDouble() - 0.5) * 0.05,
                        (_rng.NextDouble() - 0.5) * 0.05,
                        (_rng.NextDouble() - 0.5) * 0.05),
                    Mass = Math.Max(0.5, Math.Log(1 + gn.WordCount) * 0.3),
                    Radius = Math.Max(0.08, Math.Min(0.35, Math.Log(1 + gn.WordCount) * 0.035)),
                    PulsePhase = _rng.NextDouble() * Math.PI * 2,
                    LinkedIds = gn.LinkedNodeIds,
                    CustomCategoryId = gn.CustomCategoryId,
                    BirthAt = now
                });
            }
            i++;
        }

        // Diff edges — match by (src, tgt, relation)
        var existingEdges = Edges
            .GroupBy(e => (e.SourceId, e.TargetId, e.RelationType))
            .ToDictionary(g => g.Key, g => g.First());

        var graphKeys = new HashSet<(string, string, string)>();
        foreach (var ge in graph.Edges)
            graphKeys.Add((ge.SourceId, ge.TargetId, ge.RelationType));

        // Mark edges not present in new graph as dying — they keep rendering
        // until the fade-out animation finishes, then get dropped below.
        foreach (var e in Edges)
        {
            var key = (e.SourceId, e.TargetId, e.RelationType);
            if (!graphKeys.Contains(key)) e.DyingAt ??= now;
            else if (existingEdges.ContainsKey(key)) e.DyingAt = null; // edge re-added — cancel death
        }
        Edges.RemoveAll(e => e.DyingAt.HasValue &&
            (now - e.DyingAt.Value).TotalSeconds > PhysicsEdge.DeathDurationSec + 0.15);

        // Add new edges with birth timestamp
        foreach (var ge in graph.Edges)
        {
            var key = (ge.SourceId, ge.TargetId, ge.RelationType);
            if (!existingEdges.ContainsKey(key))
            {
                Edges.Add(new PhysicsEdge
                {
                    SourceId = ge.SourceId,
                    TargetId = ge.TargetId,
                    RestLength = IdealLength,
                    Strength = ge.Strength,
                    RelationType = ge.RelationType,
                    BirthAt = now
                });
            }
        }

        BumpNodeGeneration();
        DetectCommunities();
        AutoTune();
        RebuildClusterTree();
    }

    public void LoadFromGraph(KnowledgeGraph graph)
    {
        Nodes.Clear();
        Edges.Clear();
        _communityCenter.Clear();
        _communityCount.Clear();

        int total = Math.Max(1, graph.Nodes.Count);
        double velJitter = total > 40 ? 0.02 : total > 15 ? 0.1 : 0.3;

        int i = 0;
        foreach (var node in graph.Nodes)
        {
            double radius = 3.0 + Math.Sqrt(total) * 0.15;
            double phi = Math.Acos(1 - 2.0 * (i + 0.5) / total);
            double theta = Math.PI * (1 + Math.Sqrt(5)) * i;
            double r = radius + (_rng.NextDouble() - 0.5) * 0.3;

            Nodes.Add(new PhysicsNode
            {
                Id = node.Id,
                Title = node.Title,
                Category = node.PrimaryCategory,
                WordCount = node.WordCount,
                Importance = node.Importance,
                Position = new Point3D(
                    r * Math.Sin(phi) * Math.Cos(theta),
                    r * Math.Cos(phi),
                    r * Math.Sin(phi) * Math.Sin(theta)),
                Velocity = new Vector3D(
                    (_rng.NextDouble() - 0.5) * velJitter,
                    (_rng.NextDouble() - 0.5) * velJitter,
                    (_rng.NextDouble() - 0.5) * velJitter),
                Mass = Math.Max(0.5, Math.Log(1 + node.WordCount) * 0.3),
                Radius = Math.Max(0.08, Math.Min(0.35, Math.Log(1 + node.WordCount) * 0.035)),
                PulsePhase = _rng.NextDouble() * Math.PI * 2,
                LinkedIds = node.LinkedNodeIds,
                CustomCategoryId = node.CustomCategoryId
            });
            i++;
        }

        foreach (var edge in graph.Edges)
        {
            Edges.Add(new PhysicsEdge
            {
                SourceId = edge.SourceId,
                TargetId = edge.TargetId,
                RestLength = IdealLength,
                Strength = edge.Strength,
                RelationType = edge.RelationType
            });
        }

        BumpNodeGeneration();
        DetectCommunities();
        AutoTune();
        Warmup(80);
        RebuildClusterTree();
    }

    public void AutoTune()
    {
        var n = Math.Max(1, Nodes.Count);
        var inv = 1.0 / Math.Sqrt(n);

        // FR-style repulsion scales with ideal length²; damping rises with N
        Repulsion       = Math.Max(0.8, IdealLength * IdealLength * 0.6);
        MaxVelocity     = Math.Max(0.35, 2.0 * inv * 1.8);
        Damping         = n > 200 ? 0.76 : n > 50 ? 0.82 : 0.86;
        CenterGravity   = Math.Min(0.015, 0.005 + n * 0.00005);   // WEAK — clusters dominate
        ClusterGravity  = 0.06;                                    // strong cluster pull
        BounceEnergy    = n > 30 ? 0.25 : 0.5;
        SpringLength    = IdealLength;
        SpringStrength  = 0.15;                                    // stronger springs for clustering
    }

    public void Warmup(int steps)
    {
        for (int i = 0; i < steps; i++) Step(0.016);
    }

    public void Step(double dt = 0.016)
    {
        if (Nodes.Count == 0) return;

        // Step decimation for huge graphs — skip every other tick or two
        _stepTick++;
        var skipEvery = Nodes.Count > 500 ? 3 : Nodes.Count > 200 ? 2 : 1;
        if (_stepTick % skipEvery != 0) return;

        _totalEnergy = 0;
        var forces = new Vector3D[Nodes.Count];

        RebuildCommunityCenters();

        // ── 1. Repulsion — Barnes-Hut for large graphs, O(N²) otherwise ──
        if (Nodes.Count > BarnesHutThreshold)
        {
            ApplyBarnesHutRepulsion(forces);
        }
        else
        {
            for (int i = 0; i < Nodes.Count; i++)
            {
                for (int j = i + 1; j < Nodes.Count; j++)
                {
                    var delta = Nodes[i].Position - Nodes[j].Position;
                    var dist = Math.Max(0.05, delta.Length);

                    // FR repulsion: F = k²/d, capped
                    var k = IdealLength;
                    var forceMag = Math.Min(80, (k * k) / dist);

                    var dir = delta; dir.Normalize();
                    var force = dir * forceMag * 0.08;
                    forces[i] += force;
                    forces[j] -= force;
                }
            }
        }

        // ── 2. Spring attraction along edges — FR style F = d²/k ──
        var nodeIndex = new Dictionary<string, int>(Nodes.Count);
        for (int i = 0; i < Nodes.Count; i++) nodeIndex[Nodes[i].Id] = i;

        foreach (var edge in Edges)
        {
            if (!nodeIndex.TryGetValue(edge.SourceId, out var si)) continue;
            if (!nodeIndex.TryGetValue(edge.TargetId, out var ti)) continue;

            var delta = Nodes[ti].Position - Nodes[si].Position;
            var dist = Math.Max(0.05, delta.Length);

            // FR attractive force: d² / k, weighted by edge strength
            var k = IdealLength;
            var forceMag = (dist * dist) / k * SpringStrength * edge.Strength;

            var dir = delta; dir.Normalize();
            var force = dir * forceMag;
            forces[si] += force;
            forces[ti] -= force;
        }

        // ── 2b. Semantic springs — soft attraction between embedding-similar
        //        pairs that AREN'T already wiki-linked. Same FR style force as
        //        structural springs but scaled by SemanticSpringStrength so the
        //        layout still favours real link topology. No-op when the
        //        SemanticSprings list is empty (no embeddings generated yet).
        if (SemanticSprings.Count > 0)
        {
            foreach (var spring in SemanticSprings)
            {
                if (!nodeIndex.TryGetValue(spring.SourceId, out var si)) continue;
                if (!nodeIndex.TryGetValue(spring.TargetId, out var ti)) continue;

                var delta = Nodes[ti].Position - Nodes[si].Position;
                var dist = Math.Max(0.05, delta.Length);
                var k = IdealLength;
                var forceMag = (dist * dist) / k * SpringStrength
                             * SemanticSpringStrength * spring.Similarity;

                var dir = delta; dir.Normalize();
                var force = dir * forceMag;
                forces[si] += force;
                forces[ti] -= force;
            }
        }

        // ── 3. Per-community gravity — pulls cluster members together ──
        for (int i = 0; i < Nodes.Count; i++)
        {
            var n = Nodes[i];
            if (n.CommunityId >= 0 && _communityCenter.TryGetValue(n.CommunityId, out var center))
            {
                var toCenter = center - n.Position;
                forces[i] += toCenter * ClusterGravity;
            }

            // Weak global gravity so disconnected communities don't drift to infinity
            var toOrigin = new Vector3D(-n.Position.X, -n.Position.Y, -n.Position.Z);
            forces[i] += toOrigin * CenterGravity;
        }

        // ── 3b. Inter-cluster repulsion — push whole communities apart so
        // top-level bubbles don't overlap and become one unreadable blob.
        // Each node gets a small extra shove away from other communities'
        // centers, scaled by their distance.
        if (_communityCenter.Count > 1)
        {
            for (int i = 0; i < Nodes.Count; i++)
            {
                var n = Nodes[i];
                foreach (var (cid, center) in _communityCenter)
                {
                    if (cid == n.CommunityId) continue;
                    var dx = n.Position.X - center.X;
                    var dy = n.Position.Y - center.Y;
                    var dz = n.Position.Z - center.Z;
                    var distSq = dx * dx + dy * dy + dz * dz;
                    if (distSq < 0.01) continue;
                    // Inverse-distance push, capped so close clusters really bounce apart
                    var mag = Math.Min(1.5, 4.0 / distSq);
                    forces[i] += new Vector3D(dx, dy, dz) * (mag / Math.Sqrt(distSq));
                }
            }
        }

        // ── 4. Integrate ──
        for (int i = 0; i < Nodes.Count; i++)
        {
            var node = Nodes[i];
            if (node.IsDragging) continue;

            var acceleration = forces[i] / node.Mass;

            // Apply temperature (simulated annealing)
            node.Velocity += acceleration * dt * 60 * Temperature;
            node.Velocity *= Damping;

            if (node.Velocity.Length > MaxVelocity)
            {
                var v = node.Velocity;
                v.Normalize();
                node.Velocity = v * MaxVelocity;
            }

            node.Position += node.Velocity * dt * 60;

            var distFromCenter = ((Vector3D)node.Position).Length;
            var boundary = 6.0 + Math.Sqrt(Nodes.Count) * 0.3;   // bigger boundary for bigger graphs
            if (distFromCenter > boundary)
            {
                var normal = (Vector3D)node.Position;
                normal.Normalize();
                node.Position = (Point3D)(normal * boundary);
                var dot = Vector3D.DotProduct(node.Velocity, normal);
                node.Velocity -= normal * (2 * dot);
                node.Velocity *= BounceEnergy;
            }

            _totalEnergy += node.Velocity.LengthSquared * node.Mass;
        }

        // Cool down over time
        if (Temperature > 0.15) Temperature *= CoolingRate;

        // Keep cluster tree bounds fresh — cheap post-order walk
        if (ClusterTree != null) ClusterTree.ComputeBounds(ClusterTree);
    }

    // ─────────── Community detection (label propagation) ───────────
    //
    // Single-pass label propagation: each node adopts the most common
    // label among its neighbors. Converges quickly and produces
    // meaningful clusters for graphs with clear community structure.

    private void DetectCommunities()
    {
        if (Nodes.Count == 0) return;

        // Seed: each node has its own community
        for (int i = 0; i < Nodes.Count; i++) Nodes[i].CommunityId = i;

        var nodeIndex = new Dictionary<string, int>(Nodes.Count);
        for (int i = 0; i < Nodes.Count; i++) nodeIndex[Nodes[i].Id] = i;

        // Build adjacency with edge weights
        var adj = new List<(int peer, double w)>[Nodes.Count];
        for (int i = 0; i < Nodes.Count; i++) adj[i] = [];

        foreach (var edge in Edges)
        {
            if (!nodeIndex.TryGetValue(edge.SourceId, out var si)) continue;
            if (!nodeIndex.TryGetValue(edge.TargetId, out var ti)) continue;
            adj[si].Add((ti, edge.Strength));
            adj[ti].Add((si, edge.Strength));
        }

        // Propagate labels — 8 iterations is plenty for most graphs
        var order = Enumerable.Range(0, Nodes.Count).ToList();
        for (int iter = 0; iter < 8; iter++)
        {
            // Random order per iteration to break ties fairly
            Shuffle(order);
            bool changed = false;

            foreach (var i in order)
            {
                if (adj[i].Count == 0) continue;

                // Count weighted votes per label among neighbors
                var votes = new Dictionary<int, double>();
                foreach (var (peer, w) in adj[i])
                {
                    var lbl = Nodes[peer].CommunityId;
                    votes[lbl] = votes.GetValueOrDefault(lbl) + w;
                }

                // Adopt strongest label
                int bestLabel = Nodes[i].CommunityId;
                double bestVote = votes.GetValueOrDefault(bestLabel, 0);
                foreach (var (lbl, w) in votes)
                {
                    if (w > bestVote) { bestVote = w; bestLabel = lbl; }
                }
                if (bestLabel != Nodes[i].CommunityId)
                {
                    Nodes[i].CommunityId = bestLabel;
                    changed = true;
                }
            }
            if (!changed) break;
        }
    }

    private void Shuffle(List<int> list)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = _rng.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    private void RebuildCommunityCenters()
    {
        _communityCenter.Clear();
        _communityCount.Clear();

        foreach (var n in Nodes)
        {
            if (n.CommunityId < 0) continue;
            _communityCenter.TryGetValue(n.CommunityId, out var c);
            _communityCenter[n.CommunityId] = new Point3D(
                c.X + n.Position.X, c.Y + n.Position.Y, c.Z + n.Position.Z);
            _communityCount[n.CommunityId] = _communityCount.GetValueOrDefault(n.CommunityId) + 1;
        }

        // Divide by count to get actual center
        var keys = _communityCenter.Keys.ToList();
        foreach (var k in keys)
        {
            var cnt = _communityCount[k];
            var sum = _communityCenter[k];
            _communityCenter[k] = new Point3D(sum.X / cnt, sum.Y / cnt, sum.Z / cnt);
        }
    }

    // ─────────── Barnes-Hut 3D octree ───────────

    private void ApplyBarnesHutRepulsion(Vector3D[] forces)
    {
        // Find bounds
        double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
        foreach (var n in Nodes)
        {
            if (n.Position.X < minX) minX = n.Position.X;
            if (n.Position.Y < minY) minY = n.Position.Y;
            if (n.Position.Z < minZ) minZ = n.Position.Z;
            if (n.Position.X > maxX) maxX = n.Position.X;
            if (n.Position.Y > maxY) maxY = n.Position.Y;
            if (n.Position.Z > maxZ) maxZ = n.Position.Z;
        }
        var size = Math.Max(maxX - minX, Math.Max(maxY - minY, maxZ - minZ)) + 0.01;
        var rootCenter = new Point3D((minX + maxX) / 2, (minY + maxY) / 2, (minZ + maxZ) / 2);

        var root = new OctreeNode { Center = rootCenter, Size = size };
        for (int i = 0; i < Nodes.Count; i++) root.Insert(Nodes[i].Position, 1.0, 0);

        var k = IdealLength;
        for (int i = 0; i < Nodes.Count; i++)
            forces[i] += root.ComputeForce(Nodes[i].Position, k, Theta);
    }

    private sealed class OctreeNode
    {
        public Point3D Center;
        public double Size;
        public Point3D CenterOfMass;
        public double TotalMass;
        public OctreeNode[]? Children;
        public Point3D LeafPos;
        public double LeafMass;
        public bool HasLeaf;

        private const int MaxDepth = 16;

        public void Insert(Point3D pos, double mass, int depth)
        {
            TotalMass += mass;
            CenterOfMass = new Point3D(
                (CenterOfMass.X * (TotalMass - mass) + pos.X * mass) / TotalMass,
                (CenterOfMass.Y * (TotalMass - mass) + pos.Y * mass) / TotalMass,
                (CenterOfMass.Z * (TotalMass - mass) + pos.Z * mass) / TotalMass);

            if (!HasLeaf && Children == null)
            {
                LeafPos = pos; LeafMass = mass; HasLeaf = true;
                return;
            }
            if (depth >= MaxDepth) return;

            if (Children == null)
            {
                Children = new OctreeNode[8];
                var half = Size / 2;
                for (int oct = 0; oct < 8; oct++)
                {
                    double dx = (oct & 1) == 0 ? -half / 2 : half / 2;
                    double dy = (oct & 2) == 0 ? -half / 2 : half / 2;
                    double dz = (oct & 4) == 0 ? -half / 2 : half / 2;
                    Children[oct] = new OctreeNode
                    {
                        Center = new Point3D(Center.X + dx, Center.Y + dy, Center.Z + dz),
                        Size = half
                    };
                }
                // Re-insert existing leaf
                if (HasLeaf)
                {
                    var idx = Octant(LeafPos);
                    Children[idx].Insert(LeafPos, LeafMass, depth + 1);
                    HasLeaf = false;
                }
            }

            var oct2 = Octant(pos);
            Children[oct2].Insert(pos, mass, depth + 1);
        }

        private int Octant(Point3D p)
        {
            int o = 0;
            if (p.X > Center.X) o |= 1;
            if (p.Y > Center.Y) o |= 2;
            if (p.Z > Center.Z) o |= 4;
            return o;
        }

        public Vector3D ComputeForce(Point3D target, double k, double theta)
        {
            if (TotalMass == 0) return default;

            var delta = CenterOfMass - target;
            var dist = delta.Length;
            if (dist < 0.001) return default;   // skip self / coincident

            // If cell is "far enough" (Barnes-Hut opening criterion),
            // treat it as a single particle at the center of mass.
            if (Children == null || Size / dist < theta)
            {
                var mag = Math.Min(80, (k * k) / dist) * TotalMass * 0.08;
                delta.Normalize();
                return delta * -mag;  // negative = repel
            }

            var total = default(Vector3D);
            foreach (var c in Children!) total += c.ComputeForce(target, k, theta);
            return total;
        }
    }

    // ─────────── Public interaction API ───────────

    public void Disturb(double intensity = 1.0)
    {
        Temperature = Math.Min(1.5, Temperature + intensity * 0.3);
        foreach (var node in Nodes)
        {
            node.Velocity += new Vector3D(
                (_rng.NextDouble() - 0.5) * intensity * 2,
                (_rng.NextDouble() - 0.5) * intensity * 2,
                (_rng.NextDouble() - 0.5) * intensity * 2);
        }
    }

    public void KickNode(int index, double intensity = 3.0)
    {
        if (index < 0 || index >= Nodes.Count) return;
        var node = Nodes[index];
        node.Velocity += new Vector3D(
            (_rng.NextDouble() - 0.5) * intensity,
            (_rng.NextDouble() - 0.5) * intensity,
            (_rng.NextDouble() - 0.5) * intensity);

        foreach (var edge in Edges)
        {
            int? neighborIdx = null;
            if (edge.SourceId == node.Id && Nodes.FindIndex(n => n.Id == edge.TargetId) is >= 0 and var ti)
                neighborIdx = ti;
            else if (edge.TargetId == node.Id && Nodes.FindIndex(n => n.Id == edge.SourceId) is >= 0 and var si)
                neighborIdx = si;

            if (neighborIdx.HasValue)
            {
                Nodes[neighborIdx.Value].Velocity += new Vector3D(
                    (_rng.NextDouble() - 0.5) * intensity * 0.5,
                    (_rng.NextDouble() - 0.5) * intensity * 0.5,
                    (_rng.NextDouble() - 0.5) * intensity * 0.5);
            }
        }
    }

    /// <summary>
    /// Hit test that uses each node's own radius (× slack) as the tolerance.
    /// Tight nodes get tight hit zones — empty-space hovers no longer
    /// accidentally light up distant tiny nodes.
    /// </summary>
    public int? HitTestPerRadius(Point3D rayOrigin, Vector3D rayDir, double slack = 1.3)
    {
        rayDir.Normalize();
        int? closest = null;
        double bestRatio = 1.0;   // we pick the most "inside" node

        for (int i = 0; i < Nodes.Count; i++)
        {
            var toNode = Nodes[i].Position - rayOrigin;
            var proj = Vector3D.DotProduct(toNode, rayDir);
            if (proj < 0) continue;

            var closestPoint = rayOrigin + rayDir * proj;
            var dist = (Nodes[i].Position - closestPoint).Length;
            var tolerance = Math.Max(0.06, Nodes[i].Radius * slack);
            if (dist <= tolerance)
            {
                var ratio = dist / tolerance;   // 0 = dead center, 1 = edge
                if (ratio < bestRatio) { bestRatio = ratio; closest = i; }
            }
        }
        return closest;
    }

    public int? HitTest(Point3D rayOrigin, Vector3D rayDir, double threshold = 0.4)
    {
        rayDir.Normalize();
        int? closest = null;
        double minDist = threshold;

        for (int i = 0; i < Nodes.Count; i++)
        {
            var toNode = Nodes[i].Position - rayOrigin;
            var proj = Vector3D.DotProduct(toNode, rayDir);
            if (proj < 0) continue;

            var closestPoint = rayOrigin + rayDir * proj;
            var dist = (Nodes[i].Position - closestPoint).Length;

            if (dist < minDist)
            {
                minDist = dist;
                closest = i;
            }
        }
        return closest;
    }
}
