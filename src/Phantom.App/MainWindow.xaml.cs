using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Phantom.App;

public partial class MainWindow : Window
{
    private const int DwmwaUseImmersiveDarkMode = 20;

    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => UseDarkTitleBar();
    }

    /// <summary>Asks the desktop compositor for a dark caption so the title bar matches the theme (Windows 10 2004+).</summary>
    private void UseDarkTitleBar()
    {
        try
        {
            int enabled = 1;
            DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, DwmwaUseImmersiveDarkMode, ref enabled, sizeof(int));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // Purely cosmetic; older systems keep the default caption.
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
