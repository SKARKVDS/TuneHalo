using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace TuneHalo;

public enum MediaAction { PlayPause, Previous, Next, VolumeUp, VolumeDown, Like, Mute }

/// <summary>
/// Global hotkeys: modifier (Ctrl+Alt by default) + numpad key, so normal typing never triggers them.
/// NumLock must be on (with NumLock off the numpad sends Insert/End/arrows instead).
/// </summary>
public sealed class Hotkeys : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8, MOD_NOREPEAT = 0x4000;

    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    /// <summary>Numpad key → action. VK_NUMPAD0 = 0x60 … VK_NUMPAD9 = 0x69.</summary>
    public static readonly (uint Vk, string Key, MediaAction Action, bool Repeat)[] Layout =
    {
        (0x60, "0", MediaAction.PlayPause, false),
        (0x64, "4", MediaAction.Previous, false),
        (0x66, "6", MediaAction.Next, false),
        (0x68, "8", MediaAction.VolumeUp, true),
        (0x62, "2", MediaAction.VolumeDown, true),
        (0x65, "5", MediaAction.Like, false),
        (0x61, "1", MediaAction.Mute, false),
    };

    private readonly HwndSource _src;
    private readonly List<int> _ids = new();
    public event Action<MediaAction>? Pressed;

    public Hotkeys()
    {
        // Message-only window (HWND_MESSAGE parent) to receive WM_HOTKEY.
        _src = new HwndSource(new HwndSourceParameters("TuneHalo.Hotkeys") { ParentWindow = new IntPtr(-3), Width = 0, Height = 0 });
        _src.AddHook(WndProc);
    }

    public static string ModifierLabel(HotkeyModifier m) => m switch
    {
        HotkeyModifier.CtrlAlt => "Ctrl + Alt",
        HotkeyModifier.CtrlShift => "Ctrl + Maj",
        HotkeyModifier.AltShift => "Alt + Maj",
        HotkeyModifier.CtrlWin => "Ctrl + Win",
        _ => m.ToString(),
    };

    private static uint ModFlags(HotkeyModifier m) => m switch
    {
        HotkeyModifier.CtrlShift => MOD_CONTROL | MOD_SHIFT,
        HotkeyModifier.AltShift => MOD_ALT | MOD_SHIFT,
        HotkeyModifier.CtrlWin => MOD_CONTROL | MOD_WIN,
        _ => MOD_CONTROL | MOD_ALT,
    };

    /// <summary>(Re)registers every hotkey. Returns the keys that another app already owns.</summary>
    public List<string> Register(bool enabled, HotkeyModifier modifier)
    {
        Unregister();
        var failed = new List<string>();
        if (!enabled) return failed;
        uint mods = ModFlags(modifier);
        for (int i = 0; i < Layout.Length; i++)
        {
            var k = Layout[i];
            int id = 0x7100 + i;
            if (RegisterHotKey(_src.Handle, id, mods | (k.Repeat ? 0 : MOD_NOREPEAT), k.Vk)) _ids.Add(id);
            else failed.Add($"{ModifierLabel(modifier)} + Pavé {k.Key}");
        }
        return failed;
    }

    private void Unregister()
    {
        foreach (var id in _ids) UnregisterHotKey(_src.Handle, id);
        _ids.Clear();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wp, IntPtr lp, ref bool handled)
    {
        if (msg == WM_HOTKEY)
        {
            int idx = wp.ToInt32() - 0x7100;
            if (idx >= 0 && idx < Layout.Length) { Pressed?.Invoke(Layout[idx].Action); handled = true; }
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        Unregister();
        _src.Dispose();
    }
}
