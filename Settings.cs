using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace TuneHalo;

/// <summary>Modifier combination that must be held with the numpad keys.</summary>
public enum HotkeyModifier { CtrlAlt, CtrlShift, AltShift, CtrlWin }

public sealed class AppSettings
{
    public double? BubbleLeft { get; set; }
    public double? BubbleTop { get; set; }
    public bool ShowBubble { get; set; } = true;
    public bool FirstRunDone { get; set; }
    public bool HotkeysEnabled { get; set; } = true;
    public HotkeyModifier Modifier { get; set; } = HotkeyModifier.CtrlAlt;
    /// <summary>Volume step in percent for the +/- hotkeys and the mouse wheel.</summary>
    public int VolumeStep { get; set; } = 5;

    // ---- Spotify Web API (only needed for "like") ----
    public string? SpotifyClientId { get; set; }
    /// <summary>Refresh token, encrypted with Windows DPAPI for the current user.</summary>
    public string? RefreshTokenProtected { get; set; }

    [JsonIgnore]
    public string? RefreshToken
    {
        get
        {
            if (string.IsNullOrEmpty(RefreshTokenProtected)) return null;
            try
            {
                var raw = ProtectedData.Unprotect(Convert.FromBase64String(RefreshTokenProtected), Entropy, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(raw);
            }
            catch { return null; }
        }
        set => RefreshTokenProtected = string.IsNullOrWhiteSpace(value)
            ? null
            : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), Entropy, DataProtectionScope.CurrentUser));
    }

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("TuneHalo.refresh.v1");
    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TuneHalo");
    private static readonly string FilePath = Path.Combine(Dir, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch { /* corrupted file: defaults */ }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* non-fatal */ }
    }
}

public static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "TuneHalo";

    public static bool IsEnabled
    {
        get { using var k = Registry.CurrentUser.OpenSubKey(RunKey); return k?.GetValue(Name) != null; }
    }

    public static void Set(bool enabled)
    {
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled && Environment.ProcessPath is { } exe) k.SetValue(Name, $"\"{exe}\"");
        else k.DeleteValue(Name, false);
    }
}
