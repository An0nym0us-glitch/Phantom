namespace Phantom.Infrastructure;

/// <summary>Locations of everything Phantom writes. All of it stays under the user's local profile.</summary>
public sealed class AppPaths
{
    public AppPaths(string? root = null)
    {
        Root = root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Phantom");
    }

    public string Root { get; }
    public string SettingsFile => Path.Combine(Root, "settings.json");
    public string DatabaseFile => Path.Combine(Root, "history.db");
    public string LogDirectory => Path.Combine(Root, "logs");
}
