using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Tmds.DBus.Protocol;

namespace UnixMedia.Services.Hotkeys;

/// <summary>
/// Linux/Wayland: org.freedesktop.portal.GlobalShortcuts (KDE Plasma, GNOME 48+, Hyprland, …).
/// A portal session is opened while the kiosk browser runs and closed afterwards, so Escape
/// is only captured system-wide while the browser is on screen. The first time, the desktop
/// may ask the user to confirm the shortcut.
/// </summary>
internal sealed class PortalHotkey : IGlobalHotkey
{
    private const string AppId = LinuxDesktopEntry.AppId;
    private const string ShortcutId = "close-browser";
    private const string Dest = "org.freedesktop.portal.Desktop";
    private const string PortalPath = "/org/freedesktop/portal/desktop";
    private const string ShortcutsIface = "org.freedesktop.portal.GlobalShortcuts";

    private DBusConnection? _conn;
    private string? _session;
    private IDisposable? _activatedWatch;

    public async Task StartAsync(Action pressed)
    {
        var conn = await GetConnectionAsync().ConfigureAwait(false);

        // 1. Session
        var sessionToken = "unixmedia_s" + Guid.NewGuid().ToString("N")[..8];
        var (code, results) = await RequestAsync(conn, "CreateSession", "a{sv}", TimeSpan.FromSeconds(10), (ref MessageWriter w, string token) =>
            w.WriteDictionary(new Dictionary<string, VariantValue>
            {
                ["handle_token"] = VariantValue.String(token),
                ["session_handle_token"] = VariantValue.String(sessionToken),
            })).ConfigureAwait(false);
        if (code != 0 || !results.TryGetValue("session_handle", out var handle))
            throw new InvalidOperationException($"GlobalShortcuts CreateSession failed (response {code}).");
        var session = ReadPath(handle);
        _session = session;

        // 2. Listen for activations of our shortcut in this session.
        _activatedWatch = await conn.AddMatchAsync(
            new MatchRule { Type = MessageType.Signal, Interface = ShortcutsIface, Member = "Activated", Path = PortalPath },
            (Message m, object? _) =>
            {
                var r = m.GetBodyReader();
                return (Session: r.ReadObjectPathAsString(), Id: r.ReadString());
            },
            (Notification<(string Session, string Id)> n) =>
            {
                if (n.HasValue && n.Value.Session == session && n.Value.Id == ShortcutId) pressed();
            },
            emitOnCapturedContext: false, flags: ObserverFlags.None, state: null).ConfigureAwait(false);

        // 3. Bind Escape (may show a one-time confirmation dialog, so allow time for the user).
        (code, _) = await RequestAsync(conn, "BindShortcuts", "oa(sa{sv})sa{sv}", TimeSpan.FromMinutes(2), (ref MessageWriter w, string token) =>
        {
            w.WriteObjectPath(session);
            var arr = w.WriteArrayStart(DBusType.Struct);
            w.WriteStructureStart();
            w.WriteString(ShortcutId);
            w.WriteDictionary(new Dictionary<string, VariantValue>
            {
                ["description"] = VariantValue.String("Close the browser and return to UnixMedia"),
                ["preferred_trigger"] = VariantValue.String("Escape"),
            });
            w.WriteArrayEnd(arr);
            w.WriteString(""); // parent window
            w.WriteDictionary(new Dictionary<string, VariantValue> { ["handle_token"] = VariantValue.String(token) });
        }).ConfigureAwait(false);

        if (code != 0)
        {
            await StopAsync().ConfigureAwait(false);
            throw new InvalidOperationException($"GlobalShortcuts BindShortcuts was declined or failed (response {code}).");
        }
    }

    public async Task StopAsync()
    {
        _activatedWatch?.Dispose();
        _activatedWatch = null;

        var session = _session;
        _session = null;
        if (session is null || _conn is null) return;

        try
        {
            await _conn.CallMethodAsync(BuildCall(_conn, session, "org.freedesktop.portal.Session", "Close", null, null))
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Closing GlobalShortcuts session failed: {ex.Message}");
        }
    }

    private async Task<DBusConnection> GetConnectionAsync()
    {
        if (_conn is not null) return _conn;

        var address = Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS");
        if (string.IsNullOrEmpty(address))
            address = $"unix:path=/run/user/{GetUid()}/bus";

        // Our own connection: the host app-id registration must be its first portal call.
        var conn = new DBusConnection(address);
        await conn.ConnectAsync().ConfigureAwait(false);

        try
        {
            await conn.CallMethodAsync(BuildCall(conn, PortalPath, "org.freedesktop.host.portal.Registry", "Register", "sa{sv}", (ref MessageWriter w) =>
            {
                w.WriteString(AppId);
                w.WriteDictionary(new Dictionary<string, VariantValue>());
            })).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Older portals lack the registry; shortcuts still work, just without a nice app name.
            Console.Error.WriteLine($"Portal app registration skipped: {ex.Message}");
        }

        _conn = conn;
        return conn;
    }

    /// <summary>Calls a portal method that answers through an org.freedesktop.portal.Request Response signal.</summary>
    private static async Task<(uint Code, Dictionary<string, VariantValue> Results)> RequestAsync(
        DBusConnection conn, string member, string signature, TimeSpan timeout, RequestArgWriter writeArgs)
    {
        var token = "unixmedia_r" + Guid.NewGuid().ToString("N")[..8];
        var sender = conn.UniqueName!.TrimStart(':').Replace('.', '_');
        var requestPath = $"/org/freedesktop/portal/desktop/request/{sender}/{token}";

        var tcs = new TaskCompletionSource<(uint, Dictionary<string, VariantValue>)>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Subscribe before calling so the response can't be missed.
        using var watch = await conn.AddMatchAsync(
            new MatchRule { Type = MessageType.Signal, Interface = "org.freedesktop.portal.Request", Member = "Response", Path = requestPath },
            (Message m, object? _) =>
            {
                var r = m.GetBodyReader();
                return (r.ReadUInt32(), r.ReadDictionaryOfStringToVariantValue());
            },
            (Notification<(uint, Dictionary<string, VariantValue>)> n) =>
            {
                if (n.HasValue) tcs.TrySetResult(n.Value);
                else if (n.Exception is not null) tcs.TrySetException(n.Exception);
            },
            emitOnCapturedContext: false, flags: ObserverFlags.None, state: null).ConfigureAwait(false);

        await conn.CallMethodAsync(BuildCall(conn, PortalPath, ShortcutsIface, member, signature, (ref MessageWriter w) => writeArgs(ref w, token)))
            .ConfigureAwait(false);

        var done = await Task.WhenAny(tcs.Task, Task.Delay(timeout)).ConfigureAwait(false);
        if (done != tcs.Task) throw new TimeoutException($"GlobalShortcuts {member} timed out.");
        return await tcs.Task.ConfigureAwait(false);
    }

    private delegate void ArgWriter(ref MessageWriter writer);
    private delegate void RequestArgWriter(ref MessageWriter writer, string token);

    /// <summary>Builds a method-call message synchronously (MessageWriter can't live across an await).</summary>
    private static MessageBuffer BuildCall(DBusConnection conn, string path, string iface, string member, string? signature, ArgWriter? args)
    {
        var w = conn.GetMessageWriter();
        try
        {
            w.WriteMethodCallHeader(Dest, path, iface, member, signature, MessageFlags.None);
            args?.Invoke(ref w);
            return w.CreateMessage();
        }
        finally
        {
            w.Dispose();
        }
    }

    private static string ReadPath(VariantValue v)
    {
        try { return v.GetObjectPathAsString(); }
        catch { return v.GetString(); } // older portals return the handle as a string
    }

    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "getuid")]
    private static extern uint GetUid();
}
