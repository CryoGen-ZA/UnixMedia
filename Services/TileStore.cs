using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using UnixMedia.Models;

namespace UnixMedia.Services;

/// <summary>Loads and saves tiles as JSON in the per-user application data folder.</summary>
public static class TileStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string DataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UnixMedia");

    public static string IconDirectory { get; } = Path.Combine(DataDirectory, "icons");

    private static string TilesFile => Path.Combine(DataDirectory, "tiles.json");

    public static List<Tile> Load()
    {
        try
        {
            if (File.Exists(TilesFile))
            {
                var tiles = JsonSerializer.Deserialize<List<Tile>>(File.ReadAllText(TilesFile), JsonOptions);
                if (tiles is not null)
                    return tiles;
            }
        }
        catch (Exception ex)
        {
            // Keep the broken file for inspection instead of silently overwriting it.
            Console.Error.WriteLine($"Failed to read {TilesFile}: {ex.Message}");
            try { File.Copy(TilesFile, TilesFile + ".bak", overwrite: true); } catch { /* best effort */ }
        }

        return DefaultTiles();
    }

    public static void Save(IEnumerable<Tile> tiles) => WriteAtomic(TilesFile, JsonSerializer.Serialize(tiles, JsonOptions));

    private static string SettingsFile => Path.Combine(DataDirectory, "settings.json");

    public static AppSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsFile))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsFile), JsonOptions) ?? new();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to read {SettingsFile}: {ex.Message}");
        }
        return new AppSettings();
    }

    public static void SaveSettings(AppSettings settings) =>
        WriteAtomic(SettingsFile, JsonSerializer.Serialize(settings, JsonOptions));

    private static void WriteAtomic(string path, string content)
    {
        Directory.CreateDirectory(DataDirectory);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content);
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Copies a user-chosen image into the app's icon folder and returns the new path.</summary>
    public static string ImportIcon(string sourcePath)
    {
        Directory.CreateDirectory(IconDirectory);
        var dest = Path.Combine(IconDirectory, Guid.NewGuid().ToString("N") + Path.GetExtension(sourcePath).ToLowerInvariant());
        File.Copy(sourcePath, dest);
        return dest;
    }

    public static bool IsManagedIcon(string? path) =>
        path is not null &&
        Path.GetFullPath(path).StartsWith(Path.GetFullPath(IconDirectory) + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    /// <summary>Deletes an icon only if it lives in the app's own icon folder.</summary>
    public static void DeleteIcon(string? path)
    {
        if (!IsManagedIcon(path)) return;
        try { File.Delete(path!); } catch { /* best effort */ }
    }

    private static List<Tile> DefaultTiles() =>
    [
        new() { Name = "YouTube", Url = "https://www.youtube.com", Color = "#CC0000" },
        new() { Name = "Netflix", Url = "https://www.netflix.com", Color = "#B20710" },
        new() { Name = "Spotify", Url = "https://open.spotify.com", Color = "#1DB954" },
        new() { Name = "Twitch", Url = "https://www.twitch.tv", Color = "#9146FF" },
    ];
}
