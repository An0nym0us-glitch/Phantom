using Phantom.Core.Models;
using Phantom.Core.Processes;

namespace Phantom.Tests;

public class ProcessHierarchyTests
{
    [Fact]
    public void Builds_parent_child_relationships()
    {
        var h = ProcessHierarchy.Build(
        [
            Make.Process(100, 1, startSeconds: 0),
            Make.Process(200, 100, startSeconds: 10),
            Make.Process(300, 100, startSeconds: 5),
            Make.Process(400, 200, startSeconds: 20),
        ]);

        Assert.Equal([100], h.Roots.Select(p => p.Pid));
        Assert.Equal([300, 200], h.GetChildren(100).Select(p => p.Pid)); // ordered by start time
        Assert.Equal(200, h.GetParent(400)!.Pid);
        Assert.Equal([200, 100], h.GetAncestors(400).Select(p => p.Pid));
        Assert.Equal([300, 200, 400], h.GetDescendants(100).Select(p => p.Pid));
        Assert.Equal(2, h.GetDepth(400));
    }

    [Fact]
    public void Missing_parent_makes_the_process_a_root()
    {
        var h = ProcessHierarchy.Build([Make.Process(10, 9999)]);
        Assert.Single(h.Roots);
        Assert.Null(h.GetParent(10));
    }

    [Fact]
    public void Recycled_parent_pid_is_not_treated_as_a_parent()
    {
        // PID 50 was reused by a process created AFTER its supposed child.
        var h = ProcessHierarchy.Build(
        [
            Make.Process(50, 1, startSeconds: 100),
            Make.Process(60, 50, startSeconds: 10),
        ]);

        Assert.Null(h.GetParent(60));
        Assert.Equal(2, h.Roots.Count);
        Assert.Empty(h.GetChildren(50));
    }

    [Fact]
    public void Self_parented_idle_process_is_a_root_and_parents_the_system_process()
    {
        var h = ProcessHierarchy.Build(
        [
            Make.Process(0, 0, "System Idle Process", noStart: true),
            Make.Process(4, 0, "System", startSeconds: 0),
        ]);

        Assert.Equal([0], h.Roots.Select(p => p.Pid));
        Assert.Equal(0, h.GetParent(4)!.Pid);
    }

    [Fact]
    public void Cycles_between_processes_without_start_times_are_broken()
    {
        var h = ProcessHierarchy.Build(
        [
            Make.Process(1, 2, noStart: true),
            Make.Process(2, 1, noStart: true),
        ]);

        Assert.NotEmpty(h.Roots);
        Assert.True(h.GetAncestors(1).Count <= 2);
        Assert.True(h.GetDescendants(h.Roots[0].Pid).Count <= 2);
    }

    [Fact]
    public void Empty_input_yields_an_empty_hierarchy()
    {
        Assert.Empty(ProcessHierarchy.Empty.Roots);
        Assert.Null(ProcessHierarchy.Empty.Find(1));
        Assert.Empty(ProcessHierarchy.Empty.GetChildren(1));
        Assert.Empty(ProcessHierarchy.Empty.GetDescendants(1));
    }

    [Fact]
    public void Process_key_distinguishes_reused_pids()
    {
        var a = Make.Process(7, 1, startSeconds: 1);
        var b = Make.Process(7, 1, startSeconds: 2);
        Assert.NotEqual(a.Key, b.Key);
        Assert.Equal(a.Key, new ProcessKey(7, a.StartTimeUtc!.Value.Ticks));
    }
}
