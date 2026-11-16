using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace TuneHalo;

/// <summary>
/// Global "modifier + mouse wheel" (Ctrl+Alt by default) → Spotify volume, anywhere on screen.
/// Uses a low-level mouse hook; the matching wheel events are swallowed so the window under
/// the cursor does not also zoom/scroll. Everything else passes through untouched.
/// </summary>
public sealed class MouseWheelHook : IDisposable
{
    private const int WH_MOUSE_LL = 14, WM_MOUSEWHEEL = 0x020A, WHEEL_DELTA = 120;
    private const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_LWIN = 0x5B, VK_RWIN = 0x5C;

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT { public int X, Y; public uint MouseData, Flags, Time; public IntPtr ExtraInfo; }

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int id, HookProc fn, IntPtr hMod, uint threadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vk);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);

    private readonly HookProc _proc; // kept alive: the GC must not collect the delegate while hooked
    private readonly Dispatcher _dispatcher;
    private IntPtr _hook;
    private HotkeyModifier _modifier;
    private int _accum; // high-resolution wheels send fractions of 120

    /// <summary>Raised on the UI thread with +1 / -1 per wheel notch.</summary>
    public event Action<int>? Notch;

    public MouseWheelHook(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _proc = Callback;
    }

    public bool Set(bool enabled, HotkeyModifier modifier)
    {
        _modifier = modifier;
        if (!enabled) { Unhook(); return true; }
        if (_hook != IntPtr.Zero) return true;
        using var mod = Process.GetCurrentProcess().MainModule;
        _hook = SetWindowsHookEx(WH_MOUSE_LL, _proc, GetModuleHandle(mod?.ModuleName), 0);
        return _hook != IntPtr.Zero;
    }

    private static bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    private bool ModifierHeld()
    {
        bool ctrl = Down(VK_CONTROL), alt = Down(VK_MENU), shift = Down(VK_SHIFT), win = Down(VK_LWIN) || Down(VK_RWIN);
        return _modifier switch
        {
            HotkeyModifier.CtrlShift => ctrl && shift && !alt && !win,
            HotkeyModifier.AltShift => alt && shift && !ctrl && !win,
            HotkeyModifier.CtrlWin => ctrl && win && !alt && !shift,
            _ => ctrl && alt && !shift && !win,
        };
    }

    private IntPtr Callback(int code, IntPtr wParam, IntPtr lParam)
    {
        // Must return fast (Windows drops slow LL hooks): only read state and post to the UI thread.
        if (code >= 0 && wParam.ToInt32() == WM_MOUSEWHEEL && ModifierHeld())
        {
            var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            _accum += (short)(data.MouseData >> 16);
            int notches = _accum / WHEEL_DELTA;
            _accum -= notches * WHEEL_DELTA;
            for (int i = 0; i < Math.Abs(notches); i++)
                _dispatcher.BeginInvoke(() => Notch?.Invoke(Math.Sign(notches)));
            return new IntPtr(1); // swallow: no zoom/scroll in the window under the cursor
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    private void Unhook()
    {
        if (_hook != IntPtr.Zero) { UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; }
        _accum = 0;
    }

    public void Dispose() => Unhook();
}
