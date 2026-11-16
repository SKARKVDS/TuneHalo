using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using WF = System.Windows.Forms;

namespace TuneHalo;

public static class Glyph
{
    public const string Play = "", Pause = "", Previous = "", Next = "",
        Heart = "", HeartFill = "", Volume = "", Mute = "", Note = "";
}

/// <summary>Like status of the track currently playing: null = unknown (not connected, podcast, loading…).</summary>
public sealed record LikeInfo(string TrackKey, SpotifyApi.CurrentTrack? Track, bool? Saved);

public partial class App : Application
{
    private Mutex? _mutex;
    private WF.NotifyIcon? _tray;
    private WF.ToolStripMenuItem? _miBubble;
    private BubbleWindow? _bubble;
    private PanelWindow? _panel;
    private SettingsWindow? _settingsWindow;
    private Hotkeys? _hotkeys;
    private MouseWheelHook? _wheel;
    private readonly SemaphoreSlim _wheelGate = new(1, 1);
    private CancellationTokenSource? _likeCts;

    public static new App Current => (App)Application.Current;
    public AppSettings Settings { get; private set; } = new();
    public MediaService Media { get; private set; } = null!;
    public SpotifyApi Api { get; private set; } = null!;
    public VolumeController Volume { get; private set; } = null!;
    public LikeInfo? Like { get; private set; }
    public event Action<LikeInfo?>? LikeChanged;
    /// <summary>Hotkey feedback: (glyph or text, is glyph, accent colour).</summary>
    public event Action<string, bool, Brush?>? Feedback;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _mutex = new Mutex(true, @"Local\TuneHalo.SingleInstance", out bool created);
        if (!created) { Shutdown(); return; }

        Settings = AppSettings.Load();
        Media = new MediaService(Dispatcher);
        Api = new SpotifyApi(Settings);
        Volume = new VolumeController(Api);
        Volume.Warning += ShowError;
        Media.Changed += OnTrackChanged;

        CreateTray();
        _hotkeys = new Hotkeys();
        _hotkeys.Pressed += a => _ = DoAsync(a, fromHotkey: true);
        _wheel = new MouseWheelHook(Dispatcher);
        _wheel.Notch += dir => _ = WheelVolumeAsync(dir);
        ApplySettings();
        if (!Settings.FirstRunDone) { Settings.FirstRunDone = true; Settings.Save(); OpenSettings(); }

        try { await Media.StartAsync(); }
        catch (Exception ex) { ShowError("Contrôles média Windows indisponibles : " + ex.Message); }
    }

    public void ApplySettings()
    {
        var failed = _hotkeys?.Register(Settings.HotkeysEnabled, Settings.Modifier) ?? new();
        if (failed.Count > 0)
            ShowError("Raccourcis déjà utilisés par une autre application :\n" + string.Join("\n", failed));
        if (_wheel?.Set(Settings.HotkeysEnabled, Settings.Modifier) == false)
            ShowError("Impossible d'activer le raccourci molette (hook souris refusé par Windows).");

        if (Settings.ShowBubble) { _bubble ??= new BubbleWindow(); _bubble.Show(); }
        else { _bubble?.Hide(); _panel?.Hide(); }
    }

    public void SetBubbleVisible(bool visible)
    {
        Settings.ShowBubble = visible;
        Settings.Save();
        ApplySettings();
    }

    // ---- actions -------------------------------------------------------------

    public async Task DoAsync(MediaAction action, bool fromHotkey = false)
    {
        var green = (Brush)FindResource("Green");
        switch (action)
        {
            case MediaAction.PlayPause:
                if (!Media.HasSession) { LaunchSpotify(); return; }
                bool wasPlaying = Media.State.Playing;
                await Media.PlayPauseAsync();
                Feedback?.Invoke(wasPlaying ? Glyph.Pause : Glyph.Play, true, null);
                break;
            case MediaAction.Next:
                await Media.NextAsync();
                Feedback?.Invoke(Glyph.Next, true, null);
                break;
            case MediaAction.Previous:
                await Media.PreviousAsync();
                Feedback?.Invoke(Glyph.Previous, true, null);
                break;
            case MediaAction.VolumeUp:
            case MediaAction.VolumeDown:
                int step = Math.Clamp(Settings.VolumeStep, 1, 25) * (action == MediaAction.VolumeUp ? 1 : -1);
                var v = await Volume.ChangeAsync(step);
                Feedback?.Invoke(v is { } vol ? $"{vol.Percent} %" : "—", false, null);
                break;
            case MediaAction.Mute:
                var m = await Volume.ToggleMuteAsync();
                Feedback?.Invoke(m?.Muted == true ? Glyph.Mute : Glyph.Volume, true, null);
                break;
            case MediaAction.Like:
                await ToggleLikeAsync(green);
                break;
        }
    }


    /// <summary>Modifier + wheel notch. Serialized so fast scrolling never reads a stale volume and loses steps.</summary>
    private async Task WheelVolumeAsync(int direction)
    {
        await _wheelGate.WaitAsync();
        try { await DoAsync(direction > 0 ? MediaAction.VolumeUp : MediaAction.VolumeDown, fromHotkey: true); }
        catch (Exception ex) { ShowError(ex.Message); }
        finally { _wheelGate.Release(); }
    }

    public static void LaunchSpotify()
    {
        try { Process.Start(new ProcessStartInfo("spotify:") { UseShellExecute = true }); }
        catch { /* Spotify not installed */ }
    }

    // ---- like ------------------------------------------------------------------

    private string _likeKey = "";

    private void OnTrackChanged(TrackState s)
    {
        if (s.Key == _likeKey) return;
        _likeKey = s.Key;
        SetLike(null);
        if (!Api.IsConfigured || string.IsNullOrEmpty(s.Title)) return;
        _ = RefreshLikeAsync(s.Key);
    }

    private void SetLike(LikeInfo? l) { Like = l; LikeChanged?.Invoke(l); }

    private async Task RefreshLikeAsync(string key)
    {
        _likeCts?.Cancel();
        var cts = _likeCts = new CancellationTokenSource();
        try
        {
            // Skipping quickly through tracks fires many changes: wait for the track to settle.
            await Task.Delay(1200, cts.Token);
            var t = await Api.GetCurrentTrackAsync();
            if (cts.IsCancellationRequested || key != _likeKey) return;
            if (t == null) { SetLike(new LikeInfo(key, null, null)); return; }
            bool saved = await Api.IsSavedAsync(t);
            if (key == _likeKey) SetLike(new LikeInfo(key, t, saved));
        }
        catch (OperationCanceledException) { }
        catch { /* the heart just stays unknown */ }
    }

    public async Task ToggleLikeAsync(Brush? accent = null)
    {
        accent ??= (Brush)FindResource("Green");
        if (!Api.IsConfigured)
        {
            Feedback?.Invoke(Glyph.Heart, true, null);
            ShowError("Pour liker, connectez votre compte Spotify dans les paramètres.");
            return;
        }
        try
        {
            string key = Media.State.Key;
            // Re-read the track: the like must apply to what is playing *now*.
            var t = await Api.GetCurrentTrackAsync();
            if (t == null) { ShowError("Aucun titre en cours (ou un podcast) : rien à liker."); return; }
            bool saved = Like?.Track?.Uri == t.Uri && Like.Saved is { } s ? s : await Api.IsSavedAsync(t);
            await Api.SetSavedAsync(t, !saved);
            SetLike(new LikeInfo(key, t, !saved));
            Feedback?.Invoke(!saved ? Glyph.HeartFill : Glyph.Heart, true, !saved ? accent : null);
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    /// <summary>Called after a (re)connection from the settings window.</summary>
    public void RefreshLike()
    {
        _likeKey = "";
        OnTrackChanged(Media.State);
    }

    // ---- windows -----------------------------------------------------------------

    public void TogglePanel()
    {
        if (_bubble == null) return;
        _panel ??= new PanelWindow();
        if (_panel.IsVisible) _panel.Hide();
        else _panel.ShowNextTo(_bubble);
    }

    public bool PanelRecentlyClosed => _panel?.RecentlyClosed ?? false;

    public void OpenSettings()
    {
        if (_settingsWindow != null) { _settingsWindow.Activate(); return; }
        _settingsWindow = new SettingsWindow();
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    public void ShowError(string message) => _tray?.ShowBalloonTip(4000, "TuneHalo", message, WF.ToolTipIcon.Warning);

    public void Quit()
    {
        _hotkeys?.Dispose();
        _wheel?.Dispose();
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
        Shutdown();
    }

    // ---- tray --------------------------------------------------------------------

    private void CreateTray()
    {
        var menu = new WF.ContextMenuStrip();
        menu.Items.Add("Lecture / pause", null, (_, _) => _ = DoAsync(MediaAction.PlayPause));
        menu.Items.Add("Suivant", null, (_, _) => _ = DoAsync(MediaAction.Next));
        menu.Items.Add(new WF.ToolStripSeparator());
        _miBubble = new WF.ToolStripMenuItem("Bulle flottante", null, (_, _) => SetBubbleVisible(!Settings.ShowBubble));
        menu.Items.Add(_miBubble);
        menu.Items.Add("Paramètres et raccourcis…", null, (_, _) => OpenSettings());
        menu.Items.Add(new WF.ToolStripSeparator());
        menu.Items.Add("Quitter", null, (_, _) => Quit());
        menu.Opening += (_, _) => _miBubble.Checked = Settings.ShowBubble;

        _tray = new WF.NotifyIcon { Icon = MakeIcon(), Text = "TuneHalo", ContextMenuStrip = menu, Visible = true };
        _tray.MouseClick += (_, me) => { if (me.Button == WF.MouseButtons.Left) _ = DoAsync(MediaAction.PlayPause); };
        Media.Changed += s =>
        {
            string t = !s.SpotifyRunning ? "TuneHalo — Spotify fermé"
                : string.IsNullOrEmpty(s.Title) ? "TuneHalo" : $"{(s.Playing ? "▶" : "⏸")} {s.Title} — {s.Artist}";
            _tray.Text = t.Length > 127 ? t[..127] : t;
        };
    }

    private static System.Drawing.Icon MakeIcon()
    {
        const int px = 64;
        var g = new System.Windows.Controls.Grid { Width = px, Height = px };
        g.Children.Add(new Ellipse { Fill = new SolidColorBrush(Color.FromRgb(0x1D, 0xB9, 0x54)) });
        g.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = Glyph.Note, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 34,
            Foreground = Brushes.Black, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        });
        g.Measure(new Size(px, px));
        g.Arrange(new Rect(0, 0, px, px));
        var rtb = new RenderTargetBitmap(px, px, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(g);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var ms = new MemoryStream();
        enc.Save(ms);
        ms.Position = 0;
        using var bmp = new System.Drawing.Bitmap(ms);
        return System.Drawing.Icon.FromHandle(bmp.GetHicon());
    }
}
