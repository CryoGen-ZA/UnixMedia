using System;
using System.Diagnostics;

namespace UnixMedia.Services;

public enum PowerAction { Shutdown, Restart, Exit }

/// <summary>Shuts down or restarts the computer using the operating system's own commands.</summary>
public static class PowerService
{
    public static bool TryShutdown(out string? error) => Run(restart: false, out error);
    public static bool TryRestart(out string? error) => Run(restart: true, out error);

    private static bool Run(bool restart, out string? error)
    {
        error = null;
        ProcessStartInfo psi;

        if (OperatingSystem.IsWindows())
        {
            psi = new ProcessStartInfo("shutdown");
            psi.ArgumentList.Add(restart ? "/r" : "/s");
            psi.ArgumentList.Add("/t");
            psi.ArgumentList.Add("0");
        }
        else if (OperatingSystem.IsMacOS())
        {
            psi = new ProcessStartInfo("osascript");
            psi.ArgumentList.Add("-e");
            psi.ArgumentList.Add($"tell application \"System Events\" to {(restart ? "restart" : "shut down")}");
        }
        else
        {
            psi = new ProcessStartInfo("systemctl");
            psi.ArgumentList.Add(restart ? "reboot" : "poweroff");
        }

        psi.UseShellExecute = false;
        psi.RedirectStandardError = true;
        psi.CreateNoWindow = true;

        try
        {
            using var p = Process.Start(psi);
            if (p is null) { error = "Could not start the power command."; return false; }

            // The command usually returns right away; if it is still running the OS is busy shutting down.
            if (p.WaitForExit(TimeSpan.FromSeconds(5)) && p.ExitCode != 0)
            {
                var stderr = p.StandardError.ReadToEnd().Trim();
                error = $"{(restart ? "Restart" : "Shut down")} failed: {(stderr.Length > 0 ? stderr : $"exit code {p.ExitCode}")}";
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            error = $"{(restart ? "Restart" : "Shut down")} failed: {ex.Message}";
            return false;
        }
    }
}
