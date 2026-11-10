using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace Phantom.App.Services;

public interface IDialogService
{
    bool Confirm(string title, string message);
    void Inform(string title, string message);
    /// <summary>Asks where to save a file. Returns null if the user cancels.</summary>
    string? PickSaveFile(string defaultName, string filter);
    void OpenFolder(string path);
}

public sealed class DialogService : IDialogService
{
    public bool Confirm(string title, string message)
        => MessageBox.Show(Application.Current?.MainWindow!, message, title, MessageBoxButton.OKCancel, MessageBoxImage.Warning,
            MessageBoxResult.Cancel) == MessageBoxResult.OK;

    public void Inform(string title, string message)
        => MessageBox.Show(Application.Current?.MainWindow!, message, title, MessageBoxButton.OK, MessageBoxImage.Information);

    public string? PickSaveFile(string defaultName, string filter)
    {
        var dialog = new SaveFileDialog
        {
            FileName = defaultName,
            Filter = filter,
            OverwritePrompt = true,
            AddExtension = true,
        };
        return dialog.ShowDialog(Application.Current?.MainWindow) == true ? dialog.FileName : null;
    }

    public void OpenFolder(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // Opening a folder is a convenience; failing to do so is not worth interrupting the user.
        }
    }
}

public static class Theme
{
    /// <summary>Looks up a theme brush by resource key, with a neutral fallback outside a running application.</summary>
    public static System.Windows.Media.Brush Brush(string key)
        => Application.Current?.TryFindResource(key) as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.Gray;
}
