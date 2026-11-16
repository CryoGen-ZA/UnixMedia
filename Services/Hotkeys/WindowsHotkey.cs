using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;

namespace UnixMedia.Services.Hotkeys;

/// <summary>Windows: RegisterHotKey on a dedicated thread with its own message loop.</summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsHotkey : IGlobalHotkey
{
    private const int HotkeyId = 0x554D; // "UM"
    private const uint MOD_NOREPEAT = 0x4000, VK_ESCAPE = 0x1B, WM_HOTKEY = 0x0312, WM_QUIT = 0x0012;

    private Thread? _thread;
    private uint _threadId;

    public Task StartAsync(Action pressed)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _thread = new Thread(() =>
        {
            _threadId = GetCurrentThreadId();
            if (!RegisterHotKey(IntPtr.Zero, HotkeyId, MOD_NOREPEAT, VK_ESCAPE))
            {
                started.TrySetException(new InvalidOperationException($"RegisterHotKey failed (error {Marshal.GetLastWin32Error()})."));
                return;
            }
            started.TrySetResult();

            while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                if (msg.message == WM_HOTKEY && (int)msg.wParam == HotkeyId)
                    pressed();
            }
            UnregisterHotKey(IntPtr.Zero, HotkeyId);
        }) { IsBackground = true, Name = "EscapeHotkey" };
        _thread.Start();
        return started.Task;
    }

    public Task StopAsync()
    {
        if (_thread is { IsAlive: true })
        {
            PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            _thread.Join(2000);
        }
        _thread = null;
        return Task.CompletedTask;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX, ptY;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG msg, IntPtr hWnd, uint min, uint max);

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
