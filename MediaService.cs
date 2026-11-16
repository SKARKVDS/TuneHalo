using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace TuneHalo;

public sealed class TrackState
{
    public bool SpotifyRunning { get; init; }
    public bool Playing { get; init; }
    public string Title { get; init; } = "";
    public string Artist { get; init; } = "";
    public BitmapImage? Cover { get; init; }
    public TimeSpan Position { get; init; }
    public TimeSpan Duration { get; init; }
    /// <summary>When <see cref="Position"/> was measured, to interpolate while playing.</summary>
    public DateTimeOffset PositionAt { get; init; }
    public string Key => Title + "\u0001" + Artist;

    public double ProgressNow()
    {
        if (Duration <= TimeSpan.Zero) return 0;
        var pos = Position;
        if (Playing) pos += DateTimeOffset.Now - PositionAt;
        return Math.Clamp(pos.TotalMilliseconds / Duration.TotalMilliseconds, 0, 1);
    }
}

/// <summary>
/// Reads and drives Spotify through the Windows media transport controls (SMTC):
/// no account or network needed for play/pause/next/previous, title and cover.
/// </summary>
public sealed class MediaService
{
    private readonly Dispatcher _ui;
    private GlobalSystemMediaTransportControlsSessionManager? _mgr;
    private GlobalSystemMediaTransportControlsSession? _session;
    private string _coverKey = "";
    private BitmapImage? _cover;
    private bool _updating, _updateAgain;

    public TrackState State { get; private set; } = new();
    public event Action<TrackState>? Changed;

    public MediaService(Dispatcher ui) { _ui = ui; }

    public async Task StartAsync()
    {
        _mgr = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        _mgr.SessionsChanged += (_, _) => Post();
        Post();
    }

    private static bool IsSpotify(GlobalSystemMediaTransportControlsSession s) =>
        s.SourceAppUserModelId?.Contains("spotify", StringComparison.OrdinalIgnoreCase) == true;

    private void AttachSession()
    {
        GlobalSystemMediaTransportControlsSession? found = null;
        if (_mgr != null)
            foreach (var s in _mgr.GetSessions())
                if (IsSpotify(s)) { found = s; break; }

        if (ReferenceEquals(found, _session)) return;
        if (_session != null)
        {
            _session.MediaPropertiesChanged -= OnSessionEvent;
            _session.PlaybackInfoChanged -= OnSessionEvent;
            _session.TimelinePropertiesChanged -= OnSessionEvent;
        }
        _session = found;
        if (_session != null)
        {
            _session.MediaPropertiesChanged += OnSessionEvent;
            _session.PlaybackInfoChanged += OnSessionEvent;
            _session.TimelinePropertiesChanged += OnSessionEvent;
        }
    }

    private void OnSessionEvent(GlobalSystemMediaTransportControlsSession s, object e) => Post();

    /// <summary>SMTC events arrive on worker threads and in bursts: coalesce them on the UI thread.</summary>
    private void Post() => _ui.BeginInvoke(async () => await UpdateAsync());

    public async Task UpdateAsync()
    {
        if (_updating) { _updateAgain = true; return; }
        _updating = true;
        try
        {
            do
            {
                _updateAgain = false;
                AttachSession();
                State = await ReadAsync();
                Changed?.Invoke(State);
            } while (_updateAgain);
        }
        finally { _updating = false; }
    }

    private async Task<TrackState> ReadAsync()
    {
        var s = _session;
        if (s == null) return new TrackState { SpotifyRunning = System.Diagnostics.Process.GetProcessesByName("Spotify").Length > 0 };
        try
        {
            var props = await s.TryGetMediaPropertiesAsync();
            var info = s.GetPlaybackInfo();
            var tl = s.GetTimelineProperties();
            string title = props?.Title ?? "", artist = props?.Artist ?? "";

            string key = title + "\u0001" + artist;
            if (key != _coverKey)
            {
                _coverKey = key;
                _cover = props?.Thumbnail != null ? await LoadCoverAsync(props.Thumbnail) : null;
            }

            return new TrackState
            {
                SpotifyRunning = true,
                Playing = info?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                Title = title,
                Artist = artist,
                Cover = _cover,
                Position = tl.Position,
                Duration = tl.EndTime - tl.StartTime,
                PositionAt = tl.LastUpdatedTime == default ? DateTimeOffset.Now : tl.LastUpdatedTime,
            };
        }
        catch
        {
            return new TrackState { SpotifyRunning = true };
        }
    }

    private static async Task<BitmapImage?> LoadCoverAsync(IRandomAccessStreamReference thumb)
    {
        try
        {
            using var ras = await thumb.OpenReadAsync();
            using var src = ras.AsStreamForRead();
            var ms = new MemoryStream();
            await src.CopyToAsync(ms);
            ms.Position = 0;
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    // ---- commands ----------------------------------------------------------

    public bool HasSession => _session != null;

    public async Task<bool> PlayPauseAsync()
    {
        if (_session == null) return false;
        return await _session.TryTogglePlayPauseAsync();
    }

    public async Task<bool> PauseAsync()
    {
        if (_session == null) return false;
        return await _session.TryPauseAsync();
    }

    public async Task<bool> NextAsync()
    {
        if (_session == null) return false;
        return await _session.TrySkipNextAsync();
    }

    public async Task<bool> PreviousAsync()
    {
        if (_session == null) return false;
        return await _session.TrySkipPreviousAsync();
    }
}
