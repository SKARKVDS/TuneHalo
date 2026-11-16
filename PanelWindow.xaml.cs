using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace TuneHalo;

/// <summary>Control panel that opens next to the bubble and hides as soon as it loses focus.</summary>
public partial class PanelWindow : Window
{
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private TrackState _state = new();
    private DateTimeOffset _closedAt = DateTimeOffset.MinValue;
    private bool _settingVolume;

    public bool RecentlyClosed => DateTimeOffset.Now - _closedAt < TimeSpan.FromMilliseconds(400);

    public PanelWindow()
    {
        InitializeComponent();
        PrevButton.Click += (_, _) => _ = App.Current.DoAsync(MediaAction.Previous);
        NextButton.Click += (_, _) => _ = App.Current.DoAsync(MediaAction.Next);
        PlayButton.Click += (_, _) => _ = App.Current.DoAsync(MediaAction.PlayPause);
        LikeButton.Click += (_, _) => _ = App.Current.DoAsync(MediaAction.Like);
        MuteButton.Click += (_, _) => _ = App.Current.DoAsync(MediaAction.Mute);
        VolumeSlider.ValueChanged += (_, e) =>
        {
            if (_settingVolume) return;
            _ = App.Current.Volume.SetAsync((int)Math.Round(e.NewValue));
        };
        PreviewMouseWheel += (_, e) => { _ = App.Current.DoAsync(e.Delta > 0 ? MediaAction.VolumeUp : MediaAction.VolumeDown); e.Handled = true; };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Hide();
            else if (e.Key == Key.Space) _ = App.Current.DoAsync(MediaAction.PlayPause);
        };
        Deactivated += (_, _) => Hide();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) _tick.Start();
            else { _tick.Stop(); _closedAt = DateTimeOffset.Now; }
        };
        SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(this).Handle;
            NativeMethods.SetWindowLong(h, -20, NativeMethods.GetWindowLong(h, -20) | 0x80); // hide from Alt+Tab
        };

        App.Current.Media.Changed += Render;
        App.Current.LikeChanged += RenderLike;
        App.Current.Volume.Changed += RenderVolume;
        _tick.Tick += (_, _) => DrawProgress();
        Render(App.Current.Media.State);
        RenderLike(App.Current.Like);
    }

    public void ShowNextTo(Window bubble)
    {
        ReadVolume();
        Opacity = 0;
        Show();
        UpdateLayout();

        // Work area of the bubble's monitor, in DIPs.
        var dpi = VisualTreeHelper.GetDpi(bubble);
        var scr = System.Windows.Forms.Screen.FromHandle(new WindowInteropHelper(bubble).Handle).WorkingArea;
        double waL = scr.Left / dpi.DpiScaleX, waT = scr.Top / dpi.DpiScaleY,
               waR = scr.Right / dpi.DpiScaleX, waB = scr.Bottom / dpi.DpiScaleY;

        double bubbleCx = bubble.Left + bubble.Width / 2, bubbleCy = bubble.Top + bubble.Height / 2;
        bool openLeft = bubbleCx > (waL + waR) / 2;
        Left = openLeft ? bubble.Left - ActualWidth + 6 : bubble.Left + bubble.Width - 6;
        Top = bubbleCy - ActualHeight / 2;
        Left = Math.Clamp(Left, waL, Math.Max(waL, waR - ActualWidth));
        Top = Math.Clamp(Top, waT, Math.Max(waT, waB - ActualHeight));

        Activate();
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120)));
    }

    private void Render(TrackState s)
    {
        _state = s;
        if (!s.SpotifyRunning)
        {
            TitleText.Text = "Spotify n'est pas lancé";
            ArtistText.Text = "Lecture pour l'ouvrir";
        }
        else
        {
            TitleText.Text = string.IsNullOrEmpty(s.Title) ? "Spotify" : s.Title;
            ArtistText.Text = s.Artist;
        }
        Cover.Background = s.Cover != null ? new ImageBrush(s.Cover) { Stretch = Stretch.UniformToFill } : null;
        PlayButton.Content = s.Playing ? Glyph.Pause : Glyph.Play;
        DrawProgress();
    }

    private void RenderLike(LikeInfo? l)
    {
        bool liked = l?.Saved == true;
        LikeButton.Content = liked ? Glyph.HeartFill : Glyph.Heart;
        LikeButton.Foreground = liked ? (Brush)FindResource("Green") : new SolidColorBrush(Color.FromRgb(0xE6, 0xE6, 0xE6));
        LikeButton.ToolTip = !App.Current.Api.IsConfigured ? "Liker (connectez Spotify dans les paramètres)"
            : liked ? "Retirer des titres likés" : "Ajouter aux titres likés";
    }

    private async void ReadVolume()
    {
        var v = await App.Current.Volume.GetAsync(fresh: true);
        if (v != null) RenderVolume(v);
        else { VolumeSlider.IsEnabled = false; VolumeText.Text = ""; }
    }

    private void RenderVolume(VolumeInfo v)
    {
        VolumeSlider.IsEnabled = true;
        if (!VolumeSlider.IsMouseCaptureWithin) // don't fight the user's drag
        {
            _settingVolume = true;
            VolumeSlider.Value = v.Percent;
            _settingVolume = false;
        }
        VolumeText.Text = $"{v.Percent} %";
        MuteButton.Content = v.Muted ? Glyph.Mute : Glyph.Volume;
        MuteButton.ToolTip = v.ViaApi ? "Couper le son de Spotify" : "Couper le son de Spotify (mixeur Windows)";
    }

    private void DrawProgress()
    {
        double f = _state.ProgressNow();
        var track = (FrameworkElement)ProgressFill.Parent;
        ProgressFill.Width = Math.Max(0, track.ActualWidth * f);
        PosText.Text = Fmt(TimeSpan.FromMilliseconds(_state.Duration.TotalMilliseconds * f));
        DurText.Text = Fmt(_state.Duration);
    }

    private static string Fmt(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : $"{(int)t.TotalMinutes}:{t.Seconds:00}";
}

internal static class NativeMethods
{
    [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int i);
    [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr h, int i, int v);
}
