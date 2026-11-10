using System.Globalization;
using Phantom.Core.Models;
using Phantom.Core.Processes;

namespace Phantom.SystemMap;

public enum ProcessFilter
{
    All,
    /// <summary>Processes running under the current user's account.</summary>
    User,
    /// <summary>System, service and other-account processes.</summary>
    System,
}

public sealed class GraphNode
{
    public required ProcessInfo Process { get; init; }
    public ProcessKey Key => Process.Key;
    public ProcessKey? ParentKey { get; init; }
    public int Depth { get; init; }
    /// <summary>Direct children that survive the current filter (whether or not they are drawn).</summary>
    public int ChildCount { get; init; }
    public bool IsCollapsed { get; init; }
    /// <summary>Descendants hidden because this node is collapsed.</summary>
    public int HiddenDescendantCount { get; init; }
}

/// <summary>
/// The visible process graph for one snapshot: a forest of process trees after filtering
/// and collapsing. Immutable.
/// </summary>
public sealed class ProcessGraph
{
    public static readonly ProcessGraph Empty = Build([], ProcessFilter.All, null);

    private static readonly IReadOnlyList<GraphNode> None = [];

    private readonly Dictionary<ProcessKey, GraphNode> _byKey;
    private readonly Dictionary<ProcessKey, List<GraphNode>> _children;

    private ProcessGraph(List<GraphNode> nodes, List<GraphNode> roots,
        Dictionary<ProcessKey, GraphNode> byKey, Dictionary<ProcessKey, List<GraphNode>> children, long signature)
    {
        Nodes = nodes;
        Roots = roots;
        _byKey = byKey;
        _children = children;
        TopologySignature = signature;
    }

    /// <summary>Visible nodes in depth-first order (parents before children).</summary>
    public IReadOnlyList<GraphNode> Nodes { get; }
    public IReadOnlyList<GraphNode> Roots { get; }

    /// <summary>
    /// Changes only when nodes or edges change. Metric-only refreshes keep the same signature,
    /// which lets the view skip layout entirely.
    /// </summary>
    public long TopologySignature { get; }

    public GraphNode? Find(ProcessKey key) => _byKey.GetValueOrDefault(key);

    public IReadOnlyList<GraphNode> GetChildren(ProcessKey key)
        => _children.TryGetValue(key, out var list) ? list : None;

    public static bool MatchesFilter(ProcessInfo process, ProcessFilter filter) => filter switch
    {
        ProcessFilter.User => process.Owner == ProcessOwnerKind.CurrentUser,
        ProcessFilter.System => process.Owner != ProcessOwnerKind.CurrentUser,
        _ => true,
    };

    public static ProcessGraph Build(IReadOnlyList<ProcessInfo> processes, ProcessFilter filter,
        IReadOnlySet<ProcessKey>? collapsed)
    {
        var filtered = filter == ProcessFilter.All
            ? processes
            : processes.Where(p => MatchesFilter(p, filter)).ToList();

        // A process whose parent was filtered out becomes a root of its own tree.
        var hierarchy = ProcessHierarchy.Build(filtered);

        var nodes = new List<GraphNode>(filtered.Count);
        var roots = new List<GraphNode>();
        var byKey = new Dictionary<ProcessKey, GraphNode>(filtered.Count);
        var children = new Dictionary<ProcessKey, List<GraphNode>>();
        long signature = 17;

        var stack = new Stack<(ProcessInfo Process, ProcessKey? Parent, int Depth)>();
        for (int i = hierarchy.Roots.Count - 1; i >= 0; i--)
        {
            stack.Push((hierarchy.Roots[i], null, 0));
        }

        while (stack.Count > 0)
        {
            var (process, parentKey, depth) = stack.Pop();
            var kids = hierarchy.GetChildren(process.Pid);
            bool isCollapsed = kids.Count > 0 && collapsed is not null && collapsed.Contains(process.Key);

            var node = new GraphNode
            {
                Process = process,
                ParentKey = parentKey,
                Depth = depth,
                ChildCount = kids.Count,
                IsCollapsed = isCollapsed,
                HiddenDescendantCount = isCollapsed ? hierarchy.GetDescendants(process.Pid).Count : 0,
            };

            nodes.Add(node);
            byKey[node.Key] = node;
            if (parentKey is { } pk)
            {
                if (!children.TryGetValue(pk, out var list))
                {
                    children[pk] = list = [];
                }

                list.Add(node);
            }
            else
            {
                roots.Add(node);
            }

            unchecked
            {
                signature = (signature * 31) + node.Key.GetHashCode();
                signature = (signature * 31) + (parentKey?.GetHashCode() ?? 0);
                signature = (signature * 31) + (isCollapsed ? 1 : 0);
            }

            if (!isCollapsed)
            {
                for (int i = kids.Count - 1; i >= 0; i--)
                {
                    stack.Push((kids[i], node.Key, depth + 1));
                }
            }
        }

        return new ProcessGraph(nodes, roots, byKey, children, signature);
    }

    /// <summary>Keys from the node's parent up to its root.</summary>
    public IReadOnlyList<ProcessKey> GetAncestors(ProcessKey key)
    {
        var result = new List<ProcessKey>();
        var node = Find(key);
        while (node?.ParentKey is { } parent && result.Count <= _byKey.Count)
        {
            result.Add(parent);
            node = Find(parent);
        }

        return result;
    }

    /// <summary>Keys of every visible descendant.</summary>
    public IReadOnlyList<ProcessKey> GetDescendants(ProcessKey key)
    {
        var result = new List<ProcessKey>();
        var stack = new Stack<ProcessKey>();
        stack.Push(key);
        while (stack.Count > 0)
        {
            foreach (var child in GetChildren(stack.Pop()))
            {
                result.Add(child.Key);
                stack.Push(child.Key);
            }
        }

        return result;
    }

    /// <summary>The selected node together with its ancestors and descendants.</summary>
    public HashSet<ProcessKey> GetLineage(ProcessKey key)
    {
        var set = new HashSet<ProcessKey>();
        if (Find(key) is null)
        {
            return set;
        }

        set.Add(key);
        set.UnionWith(GetAncestors(key));
        set.UnionWith(GetDescendants(key));
        return set;
    }

    /// <summary>Matches a search term against the process name (substring) or PID (exact).</summary>
    public static bool MatchesSearch(ProcessInfo process, string? term)
    {
        if (string.IsNullOrWhiteSpace(term))
        {
            return false;
        }

        term = term.Trim();
        return process.Name.Contains(term, StringComparison.OrdinalIgnoreCase)
            || process.Pid.ToString(CultureInfo.InvariantCulture) == term;
    }
}
