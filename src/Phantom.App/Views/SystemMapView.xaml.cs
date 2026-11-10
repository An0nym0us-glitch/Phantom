using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Phantom.App.ViewModels;

namespace Phantom.App.Views;

/// <summary>
/// Code-behind is limited to view concerns: forwarding toolbar clicks to the canvas's
/// camera and reflecting canvas state (zoom level, match count) back into the toolbar.
/// </summary>
public partial class SystemMapView : UserControl
{
    public SystemMapView()
    {
        InitializeComponent();
        Map.ZoomChanged += zoom => ZoomText.Text = $"{zoom * 100:0}%";
        Map.NodeActivated += key => (DataContext as SystemMapViewModel)?.ToggleCollapse(key);
        Map.LayoutUpdated += (_, _) => UpdateMatchText();
    }

    private void UpdateMatchText()
    {
        string text = string.IsNullOrWhiteSpace(SearchBox.Text)
            ? ""
            : Map.MatchCount == 0 ? "No matches" : $"{Map.MatchCount} match{(Map.MatchCount == 1 ? "" : "es")}";
        if (MatchText.Text != text)
        {
            MatchText.Text = text;
        }
    }

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Map.FocusNextMatch();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            SearchBox.Text = "";
            e.Handled = true;
        }
    }

    private void OnZoomIn(object sender, RoutedEventArgs e) => Map.ZoomBy(1.25);

    private void OnZoomOut(object sender, RoutedEventArgs e) => Map.ZoomBy(0.8);

    private void OnFit(object sender, RoutedEventArgs e) => Map.FitToView();

    private void OnResetLayout(object sender, RoutedEventArgs e) => Map.ResetLayout();

    private void OnFocusSelected(object sender, RoutedEventArgs e)
    {
        if (Map.SelectedKey is { } key)
        {
            Map.FocusOn(key);
        }
    }
}
