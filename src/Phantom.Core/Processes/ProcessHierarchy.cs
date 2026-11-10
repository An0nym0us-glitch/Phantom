using Phantom.Core.Models;

namespace Phantom.Core.Processes;

/// <summary>
/// Parent/child relationships for one snapshot.
/// <para>
/// Windows records only the creator's PID, and PIDs are recycled, so a recorded parent PID
/// can point at an unrelated newer process. A relationship is accepted only when the parent
/// is present and was created no later than the child; otherwise the child is treated as a root.
/// </para>
/// </summary>
public sealed class ProcessHierarchy
{
    public static readonly ProcessHierarchy Empty = Build([]);

    private static readonly IReadOnlyList<ProcessInfo> NoChildren = [];

    private readonly Dictionary<int, ProcessInfo> _byPid;
    private readonly Dictionary<int, int> _parentOf;
    private readonly Dictionary<int, List<ProcessInfo>> _childrenOf;

    private ProcessHierarchy(
        Dictionary<int, ProcessInfo> byPid,
        Dictionary<int, int> parentOf,
        Dictionary<int, List<ProcessInfo>> childrenOf,
        List<ProcessInfo> roots)
    {
        _byPid = byPid;
        _parentOf = parentOf;
        _childrenOf = childrenOf;
        Roots = roots;
    }

    public IReadOnlyList<ProcessInfo> Roots { get; }
    public int Count => _byPid.Count;

    public static ProcessHierarchy Build(IReadOnlyList<ProcessInfo> processes)
    {
        var byPid = new Dictionary<int, ProcessInfo>(processes.Count);
        foreach (var p in processes)
        {
            byPid[p.Pid] = p;
        }

        var parentOf = new Dictionary<int, int>(processes.Count);
        foreach (var p in byPid.Values)
        {
            if (IsValidParent(p, byPid))
            {
                parentOf[p.Pid] = p.ParentPid;
            }
        }

        // Start-time ordering makes cycles impossible when times are known. When they are
        // not (pseudo-processes), break any cycle defensively by promoting a member to root.
        foreach (int pid in byPid.Keys)
        {
            int steps = 0;
            int cursor = pid;
            while (parentOf.TryGetValue(cursor, out int parent))
            {
                cursor = parent;
                if (++steps > byPid.Count)
                {
                    parentOf.Remove(pid);
                    break;
                }
            }
        }

        var childrenOf = new Dictionary<int, List<ProcessInfo>>();
        var roots = new List<ProcessInfo>();
        foreach (var p in byPid.Values)
        {
            if (parentOf.TryGetValue(p.Pid, out int parent))
            {
                if (!childrenOf.TryGetValue(parent, out var list))
                {
                    childrenOf[parent] = list = [];
                }

                list.Add(p);
            }
            else
            {
                roots.Add(p);
            }
        }

        // Deterministic ordering keeps layouts and tests stable between refreshes.
        static int Order(ProcessInfo a, ProcessInfo b)
        {
            int c = Nullable.Compare(a.StartTimeUtc, b.StartTimeUtc);
            return c != 0 ? c : a.Pid.CompareTo(b.Pid);
        }

        roots.Sort(Order);
        foreach (var list in childrenOf.Values)
        {
            list.Sort(Order);
        }

        return new ProcessHierarchy(byPid, parentOf, childrenOf, roots);
    }

    private static bool IsValidParent(ProcessInfo child, Dictionary<int, ProcessInfo> byPid)
    {
        if (child.ParentPid == child.Pid || !byPid.TryGetValue(child.ParentPid, out var parent))
        {
            return false;
        }

        if (child.StartTimeUtc is { } childStart && parent.StartTimeUtc is { } parentStart)
        {
            return parentStart <= childStart;
        }

        return true;
    }

    public ProcessInfo? Find(int pid) => _byPid.GetValueOrDefault(pid);

    /// <summary>The validated parent, or null when the process is a root.</summary>
    public ProcessInfo? GetParent(int pid)
        => _parentOf.TryGetValue(pid, out int parent) ? _byPid.GetValueOrDefault(parent) : null;

    public IReadOnlyList<ProcessInfo> GetChildren(int pid)
        => _childrenOf.TryGetValue(pid, out var list) ? list : NoChildren;

    /// <summary>Ancestors from the immediate parent up to the root.</summary>
    public IReadOnlyList<ProcessInfo> GetAncestors(int pid)
    {
        var result = new List<ProcessInfo>();
        var cursor = GetParent(pid);
        while (cursor is not null && result.Count <= _byPid.Count)
        {
            result.Add(cursor);
            cursor = GetParent(cursor.Pid);
        }

        return result;
    }

    /// <summary>All descendants in depth-first order.</summary>
    public IReadOnlyList<ProcessInfo> GetDescendants(int pid)
    {
        var result = new List<ProcessInfo>();
        var stack = new Stack<ProcessInfo>();
        PushChildren(pid);
        while (stack.Count > 0)
        {
            var p = stack.Pop();
            result.Add(p);
            PushChildren(p.Pid);
        }

        return result;

        void PushChildren(int parentPid)
        {
            var children = GetChildren(parentPid);
            for (int i = children.Count - 1; i >= 0; i--)
            {
                stack.Push(children[i]);
            }
        }
    }

    public int GetDepth(int pid) => GetAncestors(pid).Count;
}
