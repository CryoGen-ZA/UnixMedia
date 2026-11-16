using System;
using System.Threading;
using System.Threading.Tasks;

namespace UnixMedia.Services.Hotkeys;

/// <summary>A system-wide Escape hotkey that fires even while another app (the browser) has focus.</summary>
internal interface IGlobalHotkey
{
    /// <summary>Starts listening; <paramref name="pressed"/> may be invoked on any thread.</summary>
    Task StartAsync(Action pressed);
    Task StopAsync();
}

/// <summary>
/// Turns the global Escape hotkey on/off. Requests are serialised so rapid on/off/on
/// (e.g. one kiosk browser replacing another) always ends in the last requested state.
/// </summary>
public static class EscapeHotkey
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static IGlobalHotkey? _backend;
    private static bool _backendCreated;
    private static bool _desired;
    private static bool _active;
    private static Action? _onPressed;

    public static void SetActive(bool active, Action onPressed)
    {
        _desired = active;
        _onPressed = onPressed;
        _ = ReconcileAsync();
    }

    private static async Task ReconcileAsync()
    {
        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_backendCreated)
            {
                _backendCreated = true;
                _backend = CreateBackend();
            }
            if (_backend is null) return;

            while (_active != _desired)
            {
                var want = _desired;
                try
                {
                    if (want) await _backend.StartAsync(() => _onPressed?.Invoke()).ConfigureAwait(false);
                    else await _backend.StopAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Global Escape hotkey {(want ? "start" : "stop")} failed: {ex.Message}");
                }
                _active = want; // don't retry endlessly on failure
            }
        }
        finally
        {
            Gate.Release();
        }
    }

    private static IGlobalHotkey? CreateBackend()
    {
        if (OperatingSystem.IsWindows()) return new WindowsHotkey();
        if (OperatingSystem.IsMacOS()) return new MacHotkey();
        if (OperatingSystem.IsLinux())
        {
            var wayland = Environment.GetEnvironmentVariable("XDG_SESSION_TYPE") == "wayland"
                          || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));
            return wayland ? new PortalHotkey() : new X11Hotkey();
        }
        return null;
    }
}
