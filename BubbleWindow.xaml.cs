using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace TuneHalo;

public partial class BubbleWindow : Window
{
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private TrackState _state = new();

    public BubbleWindow()
    {
        InitializeComponent();
        ContextMenu = BuildMenu();
        SourceInitialized += (_, _) => MakeToolWindow();
        Loaded += OnLoaded;
        Root.MouseLeftButtonDown += OnMouseDown;
        MouseDown += (_, e) => { if (e.ChangedButton == MouseButton.Middle) _ = App.Current.DoAsync(MediaAction.PlayPause); };
        MouseWheel += (_, e) => _ = App.Current.DoAsync(e.Delta > 0 ? MediaAction.VolumeUp : MediaAction.VolumeDown);
        MouseEnter += (_, _) => Zoom(1.08);
        MouseLeave += (_, _) => Zoom(1.0);

        App.Current.Media.Changed += Render;
        App.Current.LikeChanged += l => LikeBadge.Visibility = l?.Saved == true ? Visibility.Visible : Visibility.Collapsed;
        App.Current.Feedback += ShowOsd;
        _tick.Tick += (_, _) => DrawProgress();
        _tick.Start();
    }

    private ContextMenu BuildMenu()
    {
        var m = new ContextMenu();
        void Add(string label, Action a) { var i = new MenuItem { Header = label }; i.Click += (_, _) => a(); m.Items.Add(i); }
        Add("Lecture / pause", () => _ = App.Current.DoAsync(MediaAction.PlayPause));
        Add("Liker / retirer le like", () => _ = App.Current.DoAsync(MediaAction.Like));
        Add("Ouvrir Spotify", App.LaunchSpotify);
        m.Items.Add(new Separator());
        Add("Paramètres et raccourcis…", App.Current.OpenSettings);
        Add("Masquer la bulle", () => App.Current.SetBubbleVisible(false));
        Add("Quitter", App.Current.Quit);
        return m;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        var s = App.Current.Settings;
        if (s.BubbleLeft is { } l && s.BubbleTop is { } t && IsOnScreen(l, t)) { Left = l; Top = t; }
        else
        {
            var wa = SystemParameters.WorkArea;
            Left = wa.Right - Width - 16;
            Top = wa.Top + wa.Height * 0.6;
        }
        Render(App.Current.Media.State);
    }

    private static bool IsOnScreen(double x, double y)
    {
        foreach (var sc in System.Windows.Forms.Screen.AllScreens)
            if (sc.WorkingArea.Contains((int)x + 20, (int)y + 20)) return true;
        return false;
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        bool panelJustClosed = App.Current.PanelRecentlyClosed;
        double x = Left, y = Top;
        try { DragMove(); } catch (InvalidOperationException) { return; }
        if (Math.Abs(Left - x) < 3 && Math.Abs(Top - y) < 3)
        {
            // A click on the bubble deactivates the open panel (which hides it): don't reopen it straight away.
            if (!panelJustClosed) App.Current.TogglePanel();
        }
        else
        {
            App.Current.Settings.BubbleLeft = Left;
            App.Current.Settings.BubbleTop = Top;
            App.Current.Settings.Save();
        }
    }

    private void Zoom(double to)
    {
        var a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(140)) { EasingFunction = new QuadraticEase() };
        Scale.BeginAnimation(ScaleTransform.ScaleXProperty, a);
        Scale.BeginAnimation(ScaleTransform.ScaleYProperty, a);
    }

    private void Render(TrackState s)
    {
        _state = s;
        if (s.Cover != null)
        {
            Cover.Fill = new ImageBrush(s.Cover) { Stretch = Stretch.UniformToFill };
            NoteIcon.Visibility = Visibility.Collapsed;
        }
        else
        {
            Cover.Fill = null;
            NoteIcon.Visibility = Visibility.Visible;
        }
        PausedLayer.Visibility = s.SpotifyRunning && !s.Playing && !string.IsNullOrEmpty(s.Title) ? Visibility.Visible : Visibility.Collapsed;
        ToolTip = !s.SpotifyRunning ? "Spotify n'est pas lancé — clic milieu pour le lancer"
            : string.IsNullOrEmpty(s.Title) ? "Spotify" : $"{s.Title}\n{s.Artist}";
        DrawProgress();
    }

    private void DrawProgress()
    {
        double f = _state.ProgressNow();
        if (f <= 0.001) { Progress.Data = null; return; }
        const double cx = 36, cy = 36, r = 27.5;
        if (f >= 0.999) { Progress.Data = new EllipseGeometry(new Point(cx, cy), r, r); return; }
        double a = f * 2 * Math.PI;
        var start = new Point(cx, cy - r);
        var end = new Point(cx + r * Math.Sin(a), cy - r * Math.Cos(a));
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(start, false, false);
            ctx.ArcTo(end, new Size(r, r), 0, f > 0.5, SweepDirection.Clockwise, true, false);
        }
        g.Freeze();
        Progress.Data = g;
    }

    private void ShowOsd(string text, bool isGlyph, Brush? accent)
    {
        OsdText.Text = text;
        OsdText.FontFamily = isGlyph ? (FontFamily)FindResource("Icons") : new FontFamily("Segoe UI");
        OsdText.FontSize = isGlyph ? 20 : 13;
        OsdText.Foreground = accent ?? Brushes.White;
        var anim = new DoubleAnimationUsingKeyFrames();
        anim.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(100))));
        anim.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1000))));
        anim.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1400))));
        Osd.BeginAnimation(OpacityProperty, anim);
    }

    // ---- hide from Alt+Tab ----
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr h, int i, int v);

    private void MakeToolWindow()
    {
        var h = new WindowInteropHelper(this).Handle;
        SetWindowLong(h, -20, GetWindowLong(h, -20) | 0x80);
    }
}
