using System;
using System.IO;
using System.Reflection;

namespace UnixMedia.Services;

/// <summary>
/// Installs ~/.local/share/applications/io.github.UnixMedia.desktop on Linux.
/// The XDG portals identify host apps by this desktop file; the GlobalShortcuts portal
/// (used for the Escape-to-home key on Wayland) refuses requests without it.
/// </summary>
public static class LinuxDesktopEntry
{
    public const string AppId = "io.github.UnixMedia";

    public static void Ensure()
    {
        if (!OperatingSystem.IsLinux()) return;

        try
        {
            var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            if (string.IsNullOrEmpty(dataHome))
                dataHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");

            var dir = Path.Combine(dataHome, "applications");
            var file = Path.Combine(dir, AppId + ".desktop");

            var content = $"""
                [Desktop Entry]
                Type=Application
                Name=UnixMedia
                Comment=Smart-TV style launcher for your favourite websites
                Exec={BuildExec()}
                Icon=video-display
                Terminal=false
                Categories=AudioVideo;Video;
                StartupWMClass=UnixMedia

                """;

            if (File.Exists(file) && File.ReadAllText(file) == content) return;

            Directory.CreateDirectory(dir);
            File.WriteAllText(file, content);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Could not install desktop entry: {ex.Message}");
        }
    }

    private static string BuildExec()
    {
        var process = Environment.ProcessPath ?? "UnixMedia";
        // Started as "dotnet UnixMedia.dll" rather than through the native app host.
        if (Path.GetFileNameWithoutExtension(process) == "dotnet")
            return $"{Quote(process)} {Quote(Assembly.GetEntryAssembly()?.Location ?? "UnixMedia.dll")}";
        return Quote(process);
    }

    /// <summary>Quotes an Exec argument per the Desktop Entry spec.</summary>
    private static string Quote(string arg)
    {
        if (arg.IndexOfAny([' ', '\t', '"', '\'', '\\', '>', '<', '~', '|', '&', ';', '$', '*', '?', '#', '(', ')', '`']) < 0)
            return arg.Replace("%", "%%");
        var escaped = arg.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("`", "\\`").Replace("$", "\\$").Replace("%", "%%");
        return $"\"{escaped}\"";
    }
}
