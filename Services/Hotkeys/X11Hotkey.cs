using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace UnixMedia.Services.Hotkeys;

/// <summary>Linux/X11: grabs Escape on the root window using a private Xlib connection.</summary>
internal sealed class X11Hotkey : IGlobalHotkey
{
    private const string LibX11 = "libX11.so.6";
    private const ulong XK_Escape = 0xff1b;
    private const int KeyPress = 2, GrabModeAsync = 1;
    private const uint LockMask = 1 << 1, Mod2Mask = 1 << 4; // CapsLock, NumLock
    private static readonly uint[] ModifierVariants = [0, LockMask, Mod2Mask, LockMask | Mod2Mask];

    private Thread? _thread;
    private volatile bool _running;

    public Task StartAsync(Action pressed)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _running = true;
        _thread = new Thread(() =>
        {
            var display = XOpenDisplay(IntPtr.Zero);
            if (display == IntPtr.Zero)
            {
                started.TrySetException(new InvalidOperationException("Could not open the X display."));
                return;
            }

            var root = XDefaultRootWindow(display);
            var keycode = XKeysymToKeycode(display, XK_Escape);
            foreach (var mods in ModifierVariants)
                XGrabKey(display, keycode, mods, root, 1, GrabModeAsync, GrabModeAsync);
            XFlush(display);
            started.TrySetResult();

            var ev = Marshal.AllocHGlobal(256); // XEvent is a 192-byte union on 64-bit
            try
            {
                while (_running)
                {
                    while (_running && XPending(display) > 0)
                    {
                        XNextEvent(display, ev);
                        if (Marshal.ReadInt32(ev) == KeyPress) pressed();
                    }
                    Thread.Sleep(30);
                }
            }
            finally
            {
                foreach (var mods in ModifierVariants)
                    XUngrabKey(display, keycode, mods, root);
                XCloseDisplay(display);
                Marshal.FreeHGlobal(ev);
            }
        }) { IsBackground = true, Name = "EscapeHotkey" };
        _thread.Start();
        return started.Task;
    }

    public Task StopAsync()
    {
        _running = false;
        _thread?.Join(2000);
        _thread = null;
        return Task.CompletedTask;
    }

    [DllImport(LibX11)] private static extern IntPtr XOpenDisplay(IntPtr name);
    [DllImport(LibX11)] private static extern int XCloseDisplay(IntPtr display);
    [DllImport(LibX11)] private static extern IntPtr XDefaultRootWindow(IntPtr display);
    [DllImport(LibX11)] private static extern byte XKeysymToKeycode(IntPtr display, ulong keysym);
    [DllImport(LibX11)] private static extern int XGrabKey(IntPtr display, int keycode, uint modifiers, IntPtr window, int ownerEvents, int pointerMode, int keyboardMode);
    [DllImport(LibX11)] private static extern int XUngrabKey(IntPtr display, int keycode, uint modifiers, IntPtr window);
    [DllImport(LibX11)] private static extern int XPending(IntPtr display);
    [DllImport(LibX11)] private static extern int XNextEvent(IntPtr display, IntPtr ev);
    [DllImport(LibX11)] private static extern int XFlush(IntPtr display);
}
