using System;

namespace UnixMedia.Models;

/// <summary>A launcher tile shown on the home screen.</summary>
public sealed class Tile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";

    /// <summary>Absolute path to an icon image stored in the app's icon folder, or null for a text tile.</summary>
    public string? IconPath { get; set; }

    /// <summary>Tile background colour as a hex string, e.g. "#0078D4".</summary>
    public string Color { get; set; } = "#334155";

    public Tile Clone() => (Tile)MemberwiseClone();
}
