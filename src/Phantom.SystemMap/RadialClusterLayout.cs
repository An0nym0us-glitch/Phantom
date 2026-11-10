using Phantom.Core.Models;

namespace Phantom.SystemMap;

public readonly record struct MapPoint(double X, double Y);

public sealed record ClusterBounds(ProcessKey Root, MapPoint Center, double Radius, int NodeCount);

public sealed class GraphLayout
{
    public static readonly GraphLayout Empty = new(new Dictionary<ProcessKey, MapPoint>(), [], 0, 0);

    public GraphLayout(IReadOnlyDictionary<ProcessKey, MapPoint> positions, IReadOnlyList<ClusterBounds> clusters,
        double width, double height)
    {
        Positions = positions;
        Clusters = clusters;
        Width = width;
        Height = height;
    }

    public IReadOnlyDictionary<ProcessKey, MapPoint> Positions { get; }
    public IReadOnlyList<ClusterBounds> Clusters { get; }
    /// <summary>Extent of the layout, centred on the origin.</summary>
    public double Width { get; }
    public double Height { get; }
}

/// <summary>
/// Deterministic O(n) layout. Each process tree becomes a radial cluster (root in the centre,
/// descendants on concentric rings, angular space shared in proportion to subtree size), and
/// clusters are packed into rows. Determinism matters: the same topology always produces the
/// same picture, so the map does not shuffle when only metrics change.
/// </summary>
public static class RadialClusterLayout
{
    /// <summary>Minimum distance between neighbouring node centres.</summary>
    public const double NodeSpacing = 46;
    public const double RingGap = 78;
    private const double StaggerGap = 36;
    private const double ClusterMargin = 44;
    private const int MaxStagger = 3;

    private sealed class Item
    {
        public required GraphNode Node;
        public List<Item> Children = [];
        public int Leaves = 1;
        public double Start;
        public double Sweep;
    }

    public static GraphLayout Compute(ProcessGraph graph)
    {
        if (graph.Nodes.Count == 0)
        {
            return GraphLayout.Empty;
        }

        var positions = new Dictionary<ProcessKey, MapPoint>(graph.Nodes.Count);
        var clusters = new List<(GraphNode Root, Dictionary<ProcessKey, MapPoint> Local, double Radius)>();
        foreach (var root in graph.Roots)
        {
            var local = new Dictionary<ProcessKey, MapPoint>();
            double radius = LayoutTree(graph, root, local);
            clusters.Add((root, local, radius));
        }

        // Quantising radii keeps the packing order stable when a cluster grows by a node or two.
        static double Quantise(double r) => Math.Ceiling(r / 40) * 40;
        var ordered = clusters
            .OrderByDescending(c => Quantise(c.Radius))
            .ThenBy(c => c.Root.Process.StartTimeUtc ?? DateTime.MinValue)
            .ThenBy(c => c.Root.Process.Pid)
            .ToList();

        double area = ordered.Sum(c => 4 * Quantise(c.Radius) * Quantise(c.Radius));
        double rowWidth = Math.Max(Math.Sqrt(area) * 1.25, Quantise(ordered[0].Radius) * 2);

        var bounds = new List<ClusterBounds>(ordered.Count);
        double x = 0, y = 0, rowHeight = 0, maxWidth = 0;
        foreach (var (root, local, rawRadius) in ordered)
        {
            double r = Quantise(rawRadius);
            if (x > 0 && x + (2 * r) > rowWidth)
            {
                x = 0;
                y += rowHeight;
                rowHeight = 0;
            }

            var center = new MapPoint(x + r, y + r);
            foreach (var (key, p) in local)
            {
                positions[key] = new MapPoint(center.X + p.X, center.Y + p.Y);
            }

            bounds.Add(new ClusterBounds(root.Key, center, rawRadius, local.Count));
            x += 2 * r;
            rowHeight = Math.Max(rowHeight, 2 * r);
            maxWidth = Math.Max(maxWidth, x);
        }

        double height = y + rowHeight;

        // Centre everything on the origin so "reset view" has a natural anchor.
        double dx = maxWidth / 2, dy = height / 2;
        foreach (var key in positions.Keys.ToList())
        {
            var p = positions[key];
            positions[key] = new MapPoint(p.X - dx, p.Y - dy);
        }

        for (int i = 0; i < bounds.Count; i++)
        {
            var b = bounds[i];
            bounds[i] = b with { Center = new MapPoint(b.Center.X - dx, b.Center.Y - dy) };
        }

        return new GraphLayout(positions, bounds, maxWidth, height);
    }

    /// <summary>Lays out one tree around (0,0) and returns the radius of its bounding circle.</summary>
    private static double LayoutTree(ProcessGraph graph, GraphNode rootNode, Dictionary<ProcessKey, MapPoint> output)
    {
        var root = BuildItems(graph, rootNode, out var levels);
        output[rootNode.Key] = new MapPoint(0, 0);
        if (levels.Count == 1)
        {
            return ClusterMargin;
        }

        root.Start = 0;
        root.Sweep = 2 * Math.PI;

        // Assign angular wedges top-down, proportional to leaf counts.
        for (int d = 0; d < levels.Count - 1; d++)
        {
            foreach (var item in levels[d])
            {
                double cursor = item.Start;
                foreach (var child in item.Children)
                {
                    child.Sweep = item.Sweep * child.Leaves / item.Leaves;
                    child.Start = cursor;
                    cursor += child.Sweep;
                }
            }
        }

        // Size each ring so its tightest wedge still has room for a node. Crowded rings are
        // split into up to MaxStagger sub-rings, assigned in angular order, so that nodes
        // sharing a sub-ring are always at least NodeSpacing apart along the arc.
        double previousOuter = 0;
        double extent = 0;
        for (int d = 1; d < levels.Count; d++)
        {
            var ring = levels[d]; // Breadth-first order is also angular order.
            double minSweep = ring.Min(i => i.Sweep);
            double radius = Math.Max(previousOuter + RingGap, NodeSpacing / (minSweep * MaxStagger));
            int subRings = Math.Clamp((int)Math.Ceiling(NodeSpacing / (minSweep * radius) - 1e-9), 1, MaxStagger);

            for (int i = 0; i < ring.Count; i++)
            {
                var item = ring[i];
                int level = i % subRings;
                if (subRings > 1 && i == ring.Count - 1 && level == 0 && ring.Count > 1)
                {
                    level = 1; // Avoid meeting the first node on the same sub-ring across the wrap-around.
                }

                double r = radius + (level * StaggerGap);
                double angle = item.Start + (item.Sweep / 2) - (Math.PI / 2);
                output[item.Node.Key] = new MapPoint(r * Math.Cos(angle), r * Math.Sin(angle));
                extent = Math.Max(extent, r);
            }

            previousOuter = radius + ((subRings - 1) * StaggerGap);
        }

        return extent + ClusterMargin;
    }

    private static Item BuildItems(ProcessGraph graph, GraphNode rootNode, out List<List<Item>> levels)
    {
        levels = [];
        var root = new Item { Node = rootNode };
        var order = new List<Item>();
        var queue = new Queue<(Item Item, int Depth)>();
        queue.Enqueue((root, 0));
        while (queue.Count > 0)
        {
            var (item, depth) = queue.Dequeue();
            if (levels.Count <= depth)
            {
                levels.Add([]);
            }

            levels[depth].Add(item);
            order.Add(item);
            foreach (var childNode in graph.GetChildren(item.Node.Key))
            {
                var child = new Item { Node = childNode };
                item.Children.Add(child);
                queue.Enqueue((child, depth + 1));
            }
        }

        // Breadth-first order reversed visits children before parents.
        for (int i = order.Count - 1; i >= 0; i--)
        {
            var item = order[i];
            if (item.Children.Count > 0)
            {
                item.Leaves = item.Children.Sum(c => c.Leaves);
            }
        }

        return root;
    }
}
