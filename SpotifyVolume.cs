using System;
using System.Collections.Generic;
using System.Diagnostics;
using NAudio.CoreAudioApi;

namespace TuneHalo;

/// <summary>
/// Controls Spotify's own volume in the Windows mixer (not the system volume),
/// so a Teams call or a video keeps its level.
/// </summary>
public static class SpotifyVolume
{
    private static HashSet<int> SpotifyPids()
    {
        var set = new HashSet<int>();
        foreach (var p in Process.GetProcessesByName("Spotify")) { set.Add(p.Id); p.Dispose(); }
        return set;
    }

    /// <summary>Runs <paramref name="action"/> on every audio session owned by Spotify, on every active output device.</summary>
    private static int ForEachSession(Action<SimpleAudioVolume> action)
    {
        var pids = SpotifyPids();
        if (pids.Count == 0) return 0;
        int found = 0;
        using var en = new MMDeviceEnumerator();
        foreach (var dev in en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            using (dev)
            {
                var mgr = dev.AudioSessionManager;
                mgr.RefreshSessions();
                var sessions = mgr.Sessions;
                for (int i = 0; i < sessions.Count; i++)
                {
                    using var s = sessions[i];
                    if (!pids.Contains((int)s.GetProcessID)) continue;
                    action(s.SimpleAudioVolume);
                    found++;
                }
            }
        }
        return found;
    }

    /// <summary>Current volume 0..1, or null when Spotify has no audio session yet.</summary>
    public static (float Volume, bool Muted)? Get()
    {
        try
        {
            (float, bool)? r = null;
            ForEachSession(v => r ??= (v.Volume, v.Mute));
            return r;
        }
        catch { return null; }
    }

    public static float? Change(int deltaPercent)
    {
        var cur = Get();
        if (cur is not { } c) return null;
        float target = Math.Clamp(MathF.Round((c.Volume * 100 + deltaPercent) / 1f) / 100f, 0f, 1f);
        return Set(target);
    }

    public static float? Set(float volume)
    {
        try
        {
            volume = Math.Clamp(volume, 0f, 1f);
            int n = ForEachSession(v => { v.Volume = volume; if (volume > 0) v.Mute = false; });
            return n > 0 ? volume : null;
        }
        catch { return null; }
    }

    public static bool? ToggleMute()
    {
        var cur = Get();
        if (cur is not { } c) return null;
        bool target = !c.Muted;
        try { ForEachSession(v => v.Mute = target); return target; }
        catch { return null; }
    }
}
