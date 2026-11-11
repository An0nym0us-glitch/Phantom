using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Phantom.App.ViewModels;
using Phantom.Core.Monitoring;

namespace Phantom.App;

/// <summary>
/// Automated UI smoke test, enabled with <c>--smoke-test &lt;output folder&gt;</c>. It drives the real
/// application through every page against live system data, saves a screenshot of each, records
/// data-binding errors, and exits with code 0 on success. Used by CI on Windows, where the full
/// WPF stack is available.
/// </summary>
internal sealed class SmokeTest
{
    private readonly Application _app;
    private readonly Window _window;
    private readonly string _directory;
    private readonly BindingErrorListener _bindingErrors = new();
    private readonly List<double> _selfCpu = [];
    private bool _finished;

    public SmokeTest(Application app, Window window, string directory, MonitoringEngine engine)
    {
        // Record Phantom's own CPU share from every frame so each page's steady-state cost can be reported.
        engine.FrameProduced += frame =>
        {
            if (frame.Snapshot.FindByPid(Environment.ProcessId)?.CpuPercent is { } cpu)
            {
                lock (_selfCpu)
                {
                    _selfCpu.Add(cpu);
                }
            }
        };

        _app = app;
        _window = window;
        _directory = directory;
        Directory.CreateDirectory(directory);
        PresentationTraceSources.Refresh();
        PresentationTraceSources.DataBindingSource.Listeners.Add(_bindingErrors);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
    }

    public async void Start()
    {
        try
        {
            var shell = (ShellViewModel)_window.DataContext;
            var log = new StringBuilder();

            // Let several samples arrive so rate-based metrics and CPU shares are populated.
            await Task.Delay(4500);
            if (shell.Overview.Cpu.Values is not { Length: > 0 })
            {
                throw new InvalidOperationException("No monitoring frames reached the UI.");
            }

            int index = 1;
            foreach (var page in shell.Pages)
            {
                shell.Current = page;
                await Task.Delay(2200); // first-show work (template expansion, JIT) is not steady state
                lock (_selfCpu)
                {
                    _selfCpu.Clear();
                }

                await Task.Delay(3300);
                double cost;
                lock (_selfCpu)
                {
                    cost = _selfCpu.Count > 0 ? _selfCpu.Average() : double.NaN;
                }

                Capture($"{index++:00}-{page.Title.Replace(' ', '-').ToLowerInvariant()}");
                log.AppendLine($"Visited {page.Title,-18} steady-state self CPU {cost:0.0}% of {Environment.ProcessorCount} logical processors");
            }

            // System Map interactions: select, collapse, filter.
            shell.Current = shell.Map;
            await Task.Delay(800);
            var scene = shell.Map.Scene ?? throw new InvalidOperationException("The System Map produced no scene.");
            log.AppendLine($"Map: {scene.Graph.Nodes.Count} nodes, {scene.Graph.Roots.Count} trees");
            var busiest = scene.Graph.Nodes.Where(n => n.ChildCount > 0).MaxBy(n => n.ChildCount)
                ?? throw new InvalidOperationException("No process with children was found.");
            shell.Map.SelectedKey = busiest.Key;
            await Task.Delay(1200);
            Capture($"{index++:00}-map-selected");
            shell.Map.ToggleCollapse(busiest.Key);
            await Task.Delay(1500);
            if (shell.Map.Scene?.Graph.Find(busiest.Key) is not { IsCollapsed: true })
            {
                throw new InvalidOperationException("Collapsing a process group had no effect.");
            }

            Capture($"{index++:00}-map-collapsed");
            shell.Map.ToggleCollapse(busiest.Key);
            shell.Map.FilterIndex = 1;
            shell.Map.SearchText = "e";
            await Task.Delay(1500);
            Capture($"{index++:00}-map-user-filter-search");
            shell.Map.FilterIndex = 0;
            shell.Map.SearchText = "";

            // Inspect this very process end to end.
            shell.InspectProcess(scene.Graph.Nodes.First(n => n.Process.Pid == Environment.ProcessId).Key);
            await Task.Delay(1500);
            if (shell.Processes.Selected?.Pid != Environment.ProcessId || !shell.Processes.Detail.HasProcess)
            {
                throw new InvalidOperationException("Inspecting the current process failed.");
            }

            log.AppendLine($"Inspector: {shell.Processes.Detail.Name} | {shell.Processes.Detail.Path} | {shell.Processes.Detail.Architecture} | {shell.Processes.Detail.Owner}");
            Capture($"{index++:00}-process-inspector");

            // Pause and resume must be reflected in the timeline.
            shell.IsPaused = true;
            await Task.Delay(700);
            shell.IsPaused = false;
            shell.Current = shell.Timeline;
            shell.Timeline.RangeIndex = 0;
            await Task.Delay(1800);
            if (!shell.Timeline.Items.Any(i => i.Description.Contains("paused", StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException("The pause event did not reach the timeline.");
            }

            log.AppendLine($"Timeline: {shell.Timeline.Items.Count} items — {shell.Timeline.Summary}");
            Capture($"{index:00}-timeline-session");

            // Overview was measured first, while startup work was still settling; measure it again now.
            shell.Current = shell.Overview;
            await Task.Delay(2200);
            lock (_selfCpu)
            {
                _selfCpu.Clear();
            }

            await Task.Delay(3300);
            lock (_selfCpu)
            {
                log.AppendLine($"Overview re-measured after warm-up: self CPU {(_selfCpu.Count > 0 ? _selfCpu.Average() : double.NaN):0.0}%");
            }

            log.AppendLine($"Status: {shell.State} | {shell.Summary} | {shell.Cost} | {shell.HistoryStatus}");
            log.AppendLine($"Binding errors: {_bindingErrors.Messages.Count}");
            File.WriteAllText(Path.Combine(_directory, "binding-errors.txt"), string.Join(Environment.NewLine, _bindingErrors.Messages));
            File.WriteAllText(Path.Combine(_directory, "smoke-ok.txt"), log.ToString());
            Finish(0);
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
    }

    public void Fail(Exception ex)
    {
        if (_finished)
        {
            return;
        }

        WriteFailure(_directory, ex);
        try
        {
            File.WriteAllText(Path.Combine(_directory, "binding-errors.txt"), string.Join(Environment.NewLine, _bindingErrors.Messages));
        }
        catch (IOException)
        {
        }

        Finish(1);
    }

    public static void WriteFailure(string directory, Exception ex)
    {
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "smoke-failed.txt"), ex.ToString());
        }
        catch (IOException)
        {
        }
    }

    private void Finish(int exitCode)
    {
        _finished = true;
        _app.Shutdown(exitCode);
    }

    private void Capture(string name)
    {
        if (_window.Content is not FrameworkElement root || root.ActualWidth < 1)
        {
            return;
        }

        int width = (int)root.ActualWidth, height = (int)root.ActualHeight;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(_window.Background, null, new Rect(0, 0, width, height));
            dc.DrawRectangle(new VisualBrush(root) { Stretch = Stretch.None }, null, new Rect(0, 0, width, height));
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(_directory, name + ".png"));
        encoder.Save(stream);
    }

    private sealed class BindingErrorListener : TraceListener
    {
        public List<string> Messages { get; } = [];

        public override void Write(string? message)
        {
        }

        public override void WriteLine(string? message)
        {
            if (!string.IsNullOrWhiteSpace(message) && Messages.Count < 500)
            {
                Messages.Add(message);
            }
        }
    }
}
