using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace UnixMedia.Services.Hotkeys;

/// <summary>
/// macOS: Carbon RegisterEventHotKey. Works in Cocoa apps without Accessibility permission;
/// must be called on the main thread, and events arrive through the app's run loop.
/// </summary>
internal sealed class MacHotkey : IGlobalHotkey
{
    private const string Carbon = "/System/Library/Frameworks/Carbon.framework/Carbon";
    private const uint kEventClassKeyboard = 0x6B657962; // 'keyb'
    private const uint kEventHotKeyPressed = 5;
    private const uint kVK_Escape = 53;
    private const uint Signature = 0x554D4544; // 'UMED'

    private delegate int EventHandlerProc(IntPtr callRef, IntPtr eventRef, IntPtr userData);

    private EventHandlerProc? _proc; // kept alive while registered
    private IntPtr _handlerRef, _hotKeyRef;
    private Action? _pressed;

    public Task StartAsync(Action pressed) => Dispatcher.UIThread.InvokeAsync(() =>
    {
        _pressed = pressed;
        _proc = (_, _, _) => { _pressed?.Invoke(); return 0; };

        var spec = new EventTypeSpec { eventClass = kEventClassKeyboard, eventKind = kEventHotKeyPressed };
        var target = GetApplicationEventTarget();
        var err = InstallEventHandler(target, Marshal.GetFunctionPointerForDelegate(_proc), 1, ref spec, IntPtr.Zero, out _handlerRef);
        if (err != 0) throw new InvalidOperationException($"InstallEventHandler failed ({err}).");

        var id = new EventHotKeyID { signature = Signature, id = 1 };
        err = RegisterEventHotKey(kVK_Escape, 0, id, target, 0, out _hotKeyRef);
        if (err != 0) throw new InvalidOperationException($"RegisterEventHotKey failed ({err}).");
    }).GetTask();

    public Task StopAsync() => Dispatcher.UIThread.InvokeAsync(() =>
    {
        if (_hotKeyRef != IntPtr.Zero) { UnregisterEventHotKey(_hotKeyRef); _hotKeyRef = IntPtr.Zero; }
        if (_handlerRef != IntPtr.Zero) { RemoveEventHandler(_handlerRef); _handlerRef = IntPtr.Zero; }
        _proc = null;
        _pressed = null;
    }).GetTask();

    [StructLayout(LayoutKind.Sequential)]
    private struct EventTypeSpec { public uint eventClass; public uint eventKind; }

    [StructLayout(LayoutKind.Sequential)]
    private struct EventHotKeyID { public uint signature; public uint id; }

    [DllImport(Carbon)] private static extern IntPtr GetApplicationEventTarget();
    [DllImport(Carbon)] private static extern int InstallEventHandler(IntPtr target, IntPtr handler, uint numTypes, ref EventTypeSpec list, IntPtr userData, out IntPtr handlerRef);
    [DllImport(Carbon)] private static extern int RemoveEventHandler(IntPtr handlerRef);
    [DllImport(Carbon)] private static extern int RegisterEventHotKey(uint keyCode, uint modifiers, EventHotKeyID id, IntPtr target, uint options, out IntPtr hotKeyRef);
    [DllImport(Carbon)] private static extern int UnregisterEventHotKey(IntPtr hotKeyRef);
}
