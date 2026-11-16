using System;
using System.Threading.Tasks;

namespace TuneHalo;

/// <param name="ViaApi">True when this is Spotify's own volume (the slider inside Spotify), false for the Windows mixer.</param>
public sealed record VolumeInfo(int Percent, bool Muted, bool ViaApi);

/// <summary>
/// Spotify volume. When the account is connected, drives the player's own volume through the Web API
/// (the slider inside Spotify moves with it). Otherwise, or if the API refuses, falls back to
/// Spotify's session in the Windows mixer.
/// </summary>
public sealed class VolumeController
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(5);
    private readonly SpotifyApi _api;
    private int? _cached;
    private DateTimeOffset _cachedAt;
    private int _beforeMute = 50;
    private int? _pending;
    private bool _sending;

    public event Action<VolumeInfo>? Changed;
    public event Action<string>? Warning;

    public VolumeController(SpotifyApi api) { _api = api; }

    private async Task<bool> UseApiAsync()
    {
        try { return await _api.CanControlPlayerAsync(); }
        catch { return false; }
    }

    public async Task<VolumeInfo?> GetAsync(bool fresh = false)
    {
        if (await UseApiAsync())
        {
            if (!fresh && _cached is { } c && DateTimeOffset.Now - _cachedAt < CacheTtl) return new(c, c == 0, true);
            try
            {
                if (await _api.GetPlayerVolumeAsync() is { } v)
                {
                    _cached = v;
                    _cachedAt = DateTimeOffset.Now;
                    return new(v, v == 0, true);
                }
            }
            catch { /* no active device, network…: use the mixer */ }
        }
        var m = SpotifyVolume.Get();
        return m is { } mm ? new((int)Math.Round(mm.Volume * 100), mm.Muted, false) : null;
    }

    public async Task<VolumeInfo?> ChangeAsync(int delta)
    {
        var cur = await GetAsync();
        if (cur == null) return null;
        return await SetAsync(cur.Percent + delta, cur.ViaApi);
    }

    public async Task<VolumeInfo?> SetAsync(int percent, bool? viaApi = null)
    {
        percent = Math.Clamp(percent, 0, 100);
        bool api = viaApi ?? await UseApiAsync();
        VolumeInfo info;
        if (api)
        {
            // Answer immediately and send in the background: key repeats and slider drags are coalesced.
            _cached = percent;
            _cachedAt = DateTimeOffset.Now;
            _pending = percent;
            _ = FlushAsync();
            info = new(percent, percent == 0, true);
        }
        else
        {
            if (SpotifyVolume.Set(percent / 100f) == null) return null;
            info = new(percent, false, false);
        }
        Changed?.Invoke(info);
        return info;
    }

    private async Task FlushAsync()
    {
        if (_sending) return;
        _sending = true;
        try
        {
            await Task.Delay(120);
            while (_pending is { } p)
            {
                _pending = null;
                try { await _api.SetPlayerVolumeAsync(p); }
                catch (Exception ex)
                {
                    // e.g. a device that refuses remote volume: apply it in the mixer instead.
                    _cached = null;
                    SpotifyVolume.Set(p / 100f);
                    Warning?.Invoke(ex.Message + " — volume appliqué dans le mixeur Windows.");
                }
            }
        }
        finally { _sending = false; }
    }

    public async Task<VolumeInfo?> ToggleMuteAsync()
    {
        var cur = await GetAsync();
        if (cur == null) return null;
        if (cur.ViaApi)
        {
            if (cur.Percent > 0) { _beforeMute = cur.Percent; return await SetAsync(0, true); }
            return await SetAsync(_beforeMute > 0 ? _beforeMute : 50, true);
        }
        if (SpotifyVolume.ToggleMute() is not { } muted) return null;
        var info = cur with { Muted = muted };
        Changed?.Invoke(info);
        return info;
    }
}
