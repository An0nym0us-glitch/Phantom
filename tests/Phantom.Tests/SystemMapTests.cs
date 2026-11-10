using Phantom.Core.Models;
using Phantom.SystemMap;

namespace Phantom.Tests;

public class SystemMapTests
{
    private static ProcessInfo[] Sample() =>
    [
        Make.Process(1, 0, "services.exe", 0, owner: ProcessOwnerKind.System),
        Make.Process(2, 1, "svchost.exe", 1, owner: ProcessOwnerKind.System),
        Make.Process(3, 1, "svchost.exe", 2, owner: ProcessOwnerKind.System),
        Make.Process(10, 99, "explorer.exe", 3),
        Make.Process(11, 10, "chrome.exe", 4),
        Make.Process(12, 11, "chrome.exe", 5),
        Make.Process(13, 11, "chrome.exe", 6),
        Make.Process(20, 2, "usertask.exe", 7),
    ];

    [Fact]
    public void Graph_contains_every_process_with_correct_edges()
    {
        var g = ProcessGraph.Build(Sample(), ProcessFilter.All, null);

        Assert.Equal(8, g.Nodes.Count);
        Assert.Equal([1, 10], g.Roots.Select(n => n.Process.Pid));
        var chrome = g.Nodes.Single(n => n.Process.Pid == 11);
        Assert.Equal(10, g.Find(chrome.ParentKey!.Value)!.Process.Pid);
        Assert.Equal(1, chrome.Depth);
        Assert.Equal(2, chrome.ChildCount);
        Assert.Equal([12, 13], g.GetChildren(chrome.Key).Select(n => n.Process.Pid));
        // Parents always precede their children.
        var seen = new HashSet<ProcessKey>();
        foreach (var n in g.Nodes)
        {
            Assert.True(n.ParentKey is null || seen.Contains(n.ParentKey.Value));
            seen.Add(n.Key);
        }
    }

    [Fact]
    public void User_filter_keeps_only_current_user_processes_and_re_roots_orphans()
    {
        var g = ProcessGraph.Build(Sample(), ProcessFilter.User, null);

        Assert.Equal([10, 11, 12, 13, 20], g.Nodes.Select(n => n.Process.Pid).Order());
        // usertask.exe's parent (svchost) was filtered out, so it becomes a root.
        Assert.Contains(g.Roots, r => r.Process.Pid == 20);
    }

    [Fact]
    public void System_filter_excludes_current_user_processes()
    {
        var g = ProcessGraph.Build(Sample(), ProcessFilter.System, null);
        Assert.Equal([1, 2, 3], g.Nodes.Select(n => n.Process.Pid).Order());
    }

    [Fact]
    public void Collapsing_hides_descendants_and_reports_how_many()
    {
        var all = Sample();
        var explorer = all.Single(p => p.Pid == 10);
        var g = ProcessGraph.Build(all, ProcessFilter.All, new HashSet<ProcessKey> { explorer.Key });

        Assert.Equal(5, g.Nodes.Count);
        var node = g.Find(explorer.Key)!;
        Assert.True(node.IsCollapsed);
        Assert.Equal(3, node.HiddenDescendantCount);
        Assert.Empty(g.GetChildren(explorer.Key));
    }

    [Fact]
    public void Collapsing_a_leaf_has_no_effect()
    {
        var all = Sample();
        var leaf = all.Single(p => p.Pid == 13);
        var g = ProcessGraph.Build(all, ProcessFilter.All, new HashSet<ProcessKey> { leaf.Key });
        Assert.False(g.Find(leaf.Key)!.IsCollapsed);
        Assert.Equal(8, g.Nodes.Count);
    }

    [Fact]
    public void Lineage_includes_ancestors_and_descendants_only()
    {
        var all = Sample();
        var g = ProcessGraph.Build(all, ProcessFilter.All, null);
        var lineage = g.GetLineage(all.Single(p => p.Pid == 11).Key);

        Assert.Equal([10, 11, 12, 13], lineage.Select(k => k.Pid).Order());
        Assert.Empty(g.GetLineage(new ProcessKey(4242, 0)));
    }

    [Fact]
    public void Topology_signature_ignores_metrics_but_reflects_structure()
    {
        var a = ProcessGraph.Build(Sample(), ProcessFilter.All, null);
        var changedMetrics = Sample().Select(p => p with { CpuPercent = 99, WorkingSetBytes = 5 }).ToArray();
        var b = ProcessGraph.Build(changedMetrics, ProcessFilter.All, null);
        Assert.Equal(a.TopologySignature, b.TopologySignature);

        var added = Sample().Append(Make.Process(30, 10, startSeconds: 9)).ToArray();
        Assert.NotEqual(a.TopologySignature, ProcessGraph.Build(added, ProcessFilter.All, null).TopologySignature);

        var collapsed = ProcessGraph.Build(Sample(), ProcessFilter.All, new HashSet<ProcessKey> { Sample()[0].Key });
        Assert.NotEqual(a.TopologySignature, collapsed.TopologySignature);
    }

    [Theory]
    [InlineData("chrome", true)]
    [InlineData("CHROME.EXE", true)]
    [InlineData("11", true)]
    [InlineData("1", false)]
    [InlineData("", false)]
    [InlineData("notepad", false)]
    public void Search_matches_name_substring_or_exact_pid(string term, bool expected)
        => Assert.Equal(expected, ProcessGraph.MatchesSearch(Make.Process(11, 10, "chrome.exe"), term));

    [Fact]
    public void Layout_positions_every_node_deterministically()
    {
        var g = ProcessGraph.Build(Sample(), ProcessFilter.All, null);
        var a = RadialClusterLayout.Compute(g);
        var b = RadialClusterLayout.Compute(ProcessGraph.Build(Sample(), ProcessFilter.All, null));

        Assert.Equal(g.Nodes.Count, a.Positions.Count);
        Assert.Equal(2, a.Clusters.Count);
        foreach (var (key, p) in a.Positions)
        {
            Assert.Equal(p, b.Positions[key]);
            Assert.True(double.IsFinite(p.X) && double.IsFinite(p.Y));
        }
    }

    [Fact]
    public void Layout_of_an_empty_graph_is_empty()
    {
        var layout = RadialClusterLayout.Compute(ProcessGraph.Empty);
        Assert.Empty(layout.Positions);
        Assert.Empty(layout.Clusters);
    }

    [Fact]
    public void Layout_keeps_nodes_apart_in_a_realistic_process_tree()
    {
        // services.exe with 120 children, a few of which have their own children, plus 80 standalone processes.
        var list = new List<ProcessInfo> { Make.Process(1, 0, "services.exe") };
        for (int i = 0; i < 120; i++)
        {
            list.Add(Make.Process(100 + i, 1, "svchost.exe", i + 1));
        }

        for (int i = 0; i < 12; i++)
        {
            list.Add(Make.Process(1000 + i, 100 + (i % 4), "worker.exe", 500 + i));
        }

        for (int i = 0; i < 80; i++)
        {
            list.Add(Make.Process(5000 + i, 99999, "standalone.exe", 600 + i));
        }

        var graph = ProcessGraph.Build(list, ProcessFilter.All, null);
        var layout = RadialClusterLayout.Compute(graph);
        var points = layout.Positions.Values.ToArray();
        Assert.Equal(list.Count, points.Length);

        double min = double.MaxValue;
        for (int i = 0; i < points.Length; i++)
        {
            for (int j = i + 1; j < points.Length; j++)
            {
                double dx = points[i].X - points[j].X, dy = points[i].Y - points[j].Y;
                min = Math.Min(min, Math.Sqrt((dx * dx) + (dy * dy)));
            }
        }

        Assert.True(min >= 30, $"Closest pair of nodes is only {min:0.0} units apart.");

        // Clusters must not overlap each other.
        for (int i = 0; i < layout.Clusters.Count; i++)
        {
            for (int j = i + 1; j < layout.Clusters.Count; j++)
            {
                var a = layout.Clusters[i];
                var b = layout.Clusters[j];
                double d = Math.Sqrt(Math.Pow(a.Center.X - b.Center.X, 2) + Math.Pow(a.Center.Y - b.Center.Y, 2));
                Assert.True(d + 0.001 >= a.Radius + b.Radius, "Clusters overlap.");
            }
        }
    }

    [Fact]
    public void Layout_handles_a_thousand_processes_quickly()
    {
        var list = new List<ProcessInfo>();
        for (int i = 1; i <= 1000; i++)
        {
            list.Add(Make.Process(i, i <= 20 ? 0 : (i % 20) + 1, startSeconds: i));
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var layout = RadialClusterLayout.Compute(ProcessGraph.Build(list, ProcessFilter.All, null));
        sw.Stop();

        Assert.Equal(1000, layout.Positions.Count);
        Assert.True(sw.ElapsedMilliseconds < 1000, $"Layout took {sw.ElapsedMilliseconds} ms.");
    }
}
