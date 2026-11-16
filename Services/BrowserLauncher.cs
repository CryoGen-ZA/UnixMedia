using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Threading;
using UnixMedia.Services.Hotkeys;

namespace UnixMedia.Services;

/// <summary>
/// Opens URLs in the operating system's default browser, optionally in fullscreen kiosk mode
/// using a dedicated browser profile so kiosk mode works even if the browser is already open.
/// </summary>
public static class BrowserLauncher
{
    private enum BrowserFamily { Firefox, Chromium }

    /// <summary>Command used to start the browser: executable + fixed leading arguments.</summary>
    private sealed record BrowserInfo(BrowserFamily Family, string FileName, IReadOnlyList<string> PrefixArgs, string ProfileDir);

    private static Process? _kioskProcess;

    /// <summary>Raised (on a background thread) when the kiosk browser closes or Escape sends the user home.</summary>
    public static event EventHandler? KioskExited;

    /// <summary>
    /// Normalises user input into an absolute http(s) URL. Adds "https://" when no scheme is given.
    /// Returns null if the input is not a valid web address.
    /// </summary>
    public static string? Normalize(string? input)
    {
        var text = input?.Trim();
        if (string.IsNullOrEmpty(text)) return null;

        if (!text.Contains("://", StringComparison.Ordinal))
            text = "https://" + text;

        return Uri.TryCreate(text, UriKind.Absolute, out var uri)
               && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
               && !string.IsNullOrEmpty(uri.Host)
            ? uri.AbsoluteUri
            : null;
    }

    public static bool TryOpen(string url, bool kiosk, out string? error)
    {
        error = null;
        var normalized = Normalize(url);
        if (normalized is null)
        {
            error = $"\"{url}\" is not a valid web address.";
            return false;
        }

        if (kiosk)
        {
            BrowserInfo? browser = null;
            try { browser = DetectDefaultBrowser(); }
            catch (Exception ex) { Console.Error.WriteLine($"Default browser detection failed: {ex.Message}"); }

            if (browser is not null && TryOpenKiosk(browser, normalized, out error))
                return true;
            // Unsupported or undetectable browser: fall through to a normal window.
        }

        return TryOpenNormal(normalized, out error);
    }

    // ---------------------------------------------------------------- normal open

    private static bool TryOpenNormal(string url, out string? error)
    {
        error = null;
        try
        {
            ProcessStartInfo psi;
            if (OperatingSystem.IsWindows())
            {
                psi = new ProcessStartInfo(url) { UseShellExecute = true };
            }
            else
            {
                psi = new ProcessStartInfo(OperatingSystem.IsMacOS() ? "open" : "xdg-open") { UseShellExecute = false };
                psi.ArgumentList.Add(url);
            }

            using var _ = Process.Start(psi);
            return true;
        }
        catch (Exception ex)
        {
            error = $"Could not open the browser: {ex.Message}";
            return false;
        }
    }

    // ---------------------------------------------------------------- kiosk open

    private static bool TryOpenKiosk(BrowserInfo browser, string url, out string? error)
    {
        error = null;

        // TV behaviour: the newly chosen app replaces the one currently on screen.
        // (Also required for Firefox, which refuses a second instance on the same profile.)
        CloseKioskBrowser();

        try
        {
            Directory.CreateDirectory(browser.ProfileDir);

            var psi = new ProcessStartInfo(browser.FileName) { UseShellExecute = false };
            foreach (var a in browser.PrefixArgs) psi.ArgumentList.Add(a);

            if (browser.Family == BrowserFamily.Firefox)
            {
                WriteFirefoxPrefs(browser.ProfileDir);
                psi.ArgumentList.Add("--kiosk");
                psi.ArgumentList.Add("--no-remote");
                psi.ArgumentList.Add("--profile");
                psi.ArgumentList.Add(browser.ProfileDir);
            }
            else
            {
                psi.ArgumentList.Add("--kiosk");
                psi.ArgumentList.Add($"--user-data-dir={browser.ProfileDir}");
                psi.ArgumentList.Add("--no-first-run");
                psi.ArgumentList.Add("--no-default-browser-check");
            }
            psi.ArgumentList.Add(url);

            var process = Process.Start(psi);
            if (process is null) return false;

            process.EnableRaisingEvents = true;
            process.Exited += (_, _) =>
            {
                // Only report a browser the user closed, not one we replaced via CloseKioskBrowser.
                if (Interlocked.CompareExchange(ref _kioskProcess, null, process) != process) return;
                process.Dispose();
                EscapeHotkey.SetActive(false, ReturnHome);
                KioskExited?.Invoke(null, EventArgs.Empty);
            };
            _kioskProcess = process;

            // Escape anywhere (the browser has focus, not us) closes the browser and returns home.
            EscapeHotkey.SetActive(true, ReturnHome);
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Kiosk launch failed, falling back to normal window: {ex.Message}");
            return false;
        }
    }

    /// <summary>Global Escape handler: close the kiosk browser and bring the launcher back.</summary>
    private static void ReturnHome()
    {
        if (_kioskProcess is null) return;
        CloseKioskBrowser();
        KioskExited?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>Closes the kiosk browser started by this app, if it is still running.</summary>
    public static void CloseKioskBrowser()
    {
        var p = Interlocked.Exchange(ref _kioskProcess, null);
        if (p is null) return;
        EscapeHotkey.SetActive(false, ReturnHome);
        try
        {
            if (!p.HasExited)
            {
                p.Kill(entireProcessTree: true);
                p.WaitForExit(3000);
            }
        }
        catch { /* already gone */ }
        finally { p.Dispose(); }
    }

    /// <summary>Quiet first-run / crash-restore prompts in the dedicated Firefox profile.</summary>
    private static void WriteFirefoxPrefs(string profileDir)
    {
        var userJs = Path.Combine(profileDir, "user.js");
        if (File.Exists(userJs)) return;

        var prefs = new[]
        {
            ("browser.shell.checkDefaultBrowser", "false"),
            ("browser.sessionstore.resume_from_crash", "false"),
            ("browser.aboutwelcome.enabled", "false"),
            ("datareporting.policy.dataSubmissionPolicyBypassNotification", "true"),
            ("browser.startup.homepage_override.mstone", "\"ignore\""),
            ("trailhead.firstrun.didSeeAboutWelcomePage", "true"),
        };
        var sb = new StringBuilder("// Written by UnixMedia for its kiosk profile.\n");
        foreach (var (k, v) in prefs) sb.Append($"user_pref(\"{k}\", {v});\n");
        File.WriteAllText(userJs, sb.ToString());
    }

    // ---------------------------------------------------------------- default browser detection

    private static BrowserInfo? DetectDefaultBrowser()
    {
        if (OperatingSystem.IsWindows()) return DetectWindows();
        if (OperatingSystem.IsMacOS()) return DetectMac();
        return DetectLinux();
    }

    private static BrowserFamily? Classify(string identity)
    {
        var s = identity.ToLowerInvariant();
        string[] firefox = ["firefox", "librewolf", "waterfox", "floorp", "org.mozilla"];
        string[] chromium = ["chrome", "chromium", "msedge", "microsoft-edge", "edgemac", "brave", "vivaldi"];
        if (firefox.Any(s.Contains)) return BrowserFamily.Firefox;
        if (chromium.Any(s.Contains)) return BrowserFamily.Chromium;
        return null;
    }

    private static string DefaultProfileDir(BrowserFamily family) =>
        Path.Combine(TileStore.DataDirectory, "browser-profile", family.ToString().ToLowerInvariant());

    // ---- Linux

    private static BrowserInfo? DetectLinux()
    {
        var desktopId = RunAndRead("xdg-settings", "get", "default-web-browser")?.Trim();
        if (string.IsNullOrEmpty(desktopId)) return null;

        var desktopFile = FindDesktopFile(desktopId);
        if (desktopFile is null) return null;

        var exec = ReadDesktopExec(desktopFile);
        if (exec is null) return null;

        var tokens = TokenizeExec(exec);
        if (tokens.Count == 0) return null;

        if (Classify(desktopId + " " + string.Join(' ', tokens)) is not BrowserFamily family) return null;

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var profile = DefaultProfileDir(family);

        // Sandboxed browsers can't see ~/.config/UnixMedia; put the profile somewhere they can.
        if (Path.GetFileName(tokens[0]) == "flatpak")
        {
            var appId = tokens.SkipWhile(t => t != "run").Skip(1).FirstOrDefault(t => !t.StartsWith('-'));
            if (appId is not null)
                profile = Path.Combine(home, ".var", "app", appId, "data", "unixmedia-kiosk-profile");
        }
        else if (tokens[0].StartsWith("/snap/", StringComparison.Ordinal) || desktopFile.Contains("/snapd/", StringComparison.Ordinal))
        {
            var snapName = Path.GetFileName(tokens[0]).Split('.')[0];
            profile = Path.Combine(home, "snap", snapName, "common", "unixmedia-kiosk-profile");
        }

        return new BrowserInfo(family, tokens[0], tokens.Skip(1).ToList(), profile);
    }

    private static string? FindDesktopFile(string desktopId)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (string.IsNullOrEmpty(dataHome)) dataHome = Path.Combine(home, ".local", "share");

        var dataDirs = Environment.GetEnvironmentVariable("XDG_DATA_DIRS");
        if (string.IsNullOrEmpty(dataDirs)) dataDirs = "/usr/local/share:/usr/share";

        var dirs = new List<string> { dataHome };
        dirs.AddRange(dataDirs.Split(':', StringSplitOptions.RemoveEmptyEntries));
        dirs.Add(Path.Combine(home, ".local/share/flatpak/exports/share"));
        dirs.Add("/var/lib/flatpak/exports/share");
        dirs.Add("/var/lib/snapd/desktop");

        return dirs.Select(d => Path.Combine(d, "applications", desktopId)).FirstOrDefault(File.Exists);
    }

    /// <summary>Reads Exec= from the [Desktop Entry] group (not from Desktop Action groups).</summary>
    private static string? ReadDesktopExec(string path)
    {
        var inMain = false;
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            if (line.StartsWith('['))
            {
                inMain = line == "[Desktop Entry]";
                continue;
            }
            if (inMain && line.StartsWith("Exec=", StringComparison.Ordinal))
                return line["Exec=".Length..];
        }
        return null;
    }

    /// <summary>Splits a desktop-entry Exec value into arguments and drops field codes like %u.</summary>
    private static List<string> TokenizeExec(string exec)
    {
        var tokens = new List<string>();
        var sb = new StringBuilder();
        bool inQuotes = false, has = false;

        for (var i = 0; i < exec.Length; i++)
        {
            var c = exec[i];
            if (inQuotes && c == '\\' && i + 1 < exec.Length) { sb.Append(exec[++i]); continue; }
            if (c == '"') { inQuotes = !inQuotes; has = true; continue; }
            if (!inQuotes && char.IsWhiteSpace(c))
            {
                if (has) { tokens.Add(sb.ToString()); sb.Clear(); has = false; }
                continue;
            }
            sb.Append(c);
            has = true;
        }
        if (has) tokens.Add(sb.ToString());

        return tokens
            .Where(t => !(t.Length == 2 && t[0] == '%' && t[1] != '%'))   // %u %U %f %F %i %c %k ...
            .Where(t => !t.StartsWith("@@", StringComparison.Ordinal))      // flatpak file-forwarding markers
            .Where(t => t != "--file-forwarding")
            .Select(t => t.Replace("%%", "%"))
            .ToList();
    }

    // ---- Windows

    [SupportedOSPlatform("windows")]
    private static BrowserInfo? DetectWindows()
    {
        using var choice = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\https\UserChoice");
        if (choice?.GetValue("ProgId") is not string progId) return null;

        using var cmdKey = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey($@"{progId}\shell\open\command");
        if (cmdKey?.GetValue(null) is not string command) return null;

        // e.g.  "C:\Program Files\Mozilla Firefox\firefox.exe" -osint -url "%1"
        command = command.Trim();
        string exe;
        if (command.StartsWith('"'))
        {
            var end = command.IndexOf('"', 1);
            if (end < 0) return null;
            exe = command[1..end];
        }
        else
        {
            var idx = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return null;
            exe = command[..(idx + 4)];
        }

        if (!File.Exists(exe) || Classify(progId + " " + Path.GetFileName(exe)) is not BrowserFamily family) return null;
        return new BrowserInfo(family, exe, [], DefaultProfileDir(family));
    }

    // ---- macOS

    private static BrowserInfo? DetectMac()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var plist = Path.Combine(home, "Library/Preferences/com.apple.LaunchServices/com.apple.launchservices.secure.plist");
        if (!File.Exists(plist)) return null;   // Safari is the default; it has no kiosk flag.

        var json = RunAndRead("plutil", "-convert", "json", "-o", "-", plist);
        if (json is null) return null;

        string? bundleId = null;
        using (var doc = JsonDocument.Parse(json))
        {
            if (!doc.RootElement.TryGetProperty("LSHandlers", out var handlers)) return null;
            foreach (var h in handlers.EnumerateArray())
            {
                if (h.TryGetProperty("LSHandlerURLScheme", out var scheme) && scheme.GetString() == "https" &&
                    h.TryGetProperty("LSHandlerRoleAll", out var role))
                {
                    bundleId = role.GetString();
                    break;
                }
            }
        }
        if (bundleId is null || Classify(bundleId) is not BrowserFamily family) return null;

        // Find the .app bundle and its executable so we can start (and later close) the process directly.
        var appPath = RunAndRead("mdfind", $"kMDItemCFBundleIdentifier == '{bundleId}'")?
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(p => p.EndsWith(".app", StringComparison.Ordinal));
        if (appPath is null) return null;

        var exeName = RunAndRead("defaults", "read", Path.Combine(appPath, "Contents", "Info"), "CFBundleExecutable")?.Trim();
        if (string.IsNullOrEmpty(exeName)) return null;

        var exe = Path.Combine(appPath, "Contents", "MacOS", exeName);
        return File.Exists(exe) ? new BrowserInfo(family, exe, [], DefaultProfileDir(family)) : null;
    }

    // ---- helpers

    private static string? RunAndRead(string file, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(file)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            using var p = Process.Start(psi);
            if (p is null) return null;
            var output = p.StandardOutput.ReadToEndAsync();
            _ = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(TimeSpan.FromSeconds(5))) { try { p.Kill(); } catch { } return null; }
            return p.ExitCode == 0 ? output.Result : null;
        }
        catch
        {
            return null;
        }
    }
}
