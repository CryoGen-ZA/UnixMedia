namespace UnixMedia.Services;

/// <summary>User preferences persisted to settings.json.</summary>
public sealed class AppSettings
{
    /// <summary>Open tiles in the browser's fullscreen kiosk mode (when the default browser supports it).</summary>
    public bool KioskMode { get; set; } = true;

    /// <summary>Play navigation / selection sounds.</summary>
    public bool Sounds { get; set; } = true;

    /// <summary>Play the animated intro before the home screen.</summary>
    public bool Intro { get; set; } = true;
}
