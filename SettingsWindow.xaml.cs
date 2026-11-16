using System;
using System.Diagnostics;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TuneHalo;

public partial class SettingsWindow : Window
{
    private CancellationTokenSource? _loginCts;
    private AppSettings S => App.Current.Settings;

    private static string ActionLabel(MediaAction a) => a switch
    {
        MediaAction.PlayPause => "Lecture / pause",
        MediaAction.Previous => "Titre précédent",
        MediaAction.Next => "Titre suivant",
        MediaAction.VolumeUp => "Volume Spotify +",
        MediaAction.VolumeDown => "Volume Spotify −",
        MediaAction.Like => "Liker / retirer le like",
        MediaAction.Mute => "Couper / remettre le son",
        _ => a.ToString(),
    };

    public SettingsWindow()
    {
        InitializeComponent();

        foreach (HotkeyModifier m in Enum.GetValues<HotkeyModifier>())
            ModifierCombo.Items.Add(new ComboBoxItem { Content = Hotkeys.ModifierLabel(m), Tag = m });
        ModifierCombo.SelectedIndex = (int)S.Modifier;
        HotkeysCheck.IsChecked = S.HotkeysEnabled;
        BubbleCheck.IsChecked = S.ShowBubble;
        AutoStartCheck.IsChecked = AutoStart.IsEnabled;
        RedirectBox.Text = SpotifyApi.RedirectUri;
        ClientIdBox.Text = S.SpotifyClientId ?? "";
        RenderLayout();
        RenderStatus();

        HotkeysCheck.Click += (_, _) => { S.HotkeysEnabled = HotkeysCheck.IsChecked == true; SaveAndApply(); };
        ModifierCombo.SelectionChanged += (_, _) =>
        {
            if (ModifierCombo.SelectedItem is ComboBoxItem { Tag: HotkeyModifier m }) { S.Modifier = m; SaveAndApply(); RenderLayout(); }
        };
        BubbleCheck.Click += (_, _) => App.Current.SetBubbleVisible(BubbleCheck.IsChecked == true);
        AutoStartCheck.Click += (_, _) =>
        {
            try { AutoStart.Set(AutoStartCheck.IsChecked == true); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "TuneHalo"); }
        };
        DashboardLink.RequestNavigate += (_, e) => Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        CopyButton.Click += (_, _) => { try { Clipboard.SetText(SpotifyApi.RedirectUri); CopyButton.Content = "Copié ✓"; } catch { } };
        ConnectButton.Click += async (_, _) =>
        {
            S.SpotifyClientId = ClientIdBox.Text.Trim();
            S.Save();
            _loginCts?.Cancel();
            _loginCts = new CancellationTokenSource();
            ConnectButton.IsEnabled = false;
            StatusText.Text = "Autorisez TuneHalo dans le navigateur qui vient de s'ouvrir…";
            string? err = await App.Current.Api.LoginAsync(_loginCts.Token);
            ConnectButton.IsEnabled = true;
            RenderStatus(err);
            if (err == null) App.Current.RefreshLike();
        };
        DisconnectButton.Click += (_, _) => { App.Current.Api.Logout(); RenderStatus(); App.Current.RefreshLike(); };
        CloseButton.Click += (_, _) => Close();
        Closed += (_, _) =>
        {
            _loginCts?.Cancel();
            var id = ClientIdBox.Text.Trim();
            if (id != (S.SpotifyClientId ?? "")) { S.SpotifyClientId = id; S.Save(); }
        };
    }

    private void SaveAndApply()
    {
        S.Save();
        App.Current.ApplySettings();
    }

    private void RenderLayout()
    {
        LayoutList.Items.Clear();
        string mod = Hotkeys.ModifierLabel(S.Modifier);
        var rows = new System.Collections.Generic.List<(string Keys, string Label)>();
        foreach (var k in Hotkeys.Layout) rows.Add(($"{mod} + Pavé {k.Key}", ActionLabel(k.Action)));
        rows.Add(($"{mod} + Molette", "Volume Spotify (partout)"));
        foreach (var r in rows)
        {
            var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            var keys = new TextBlock { Text = r.Keys, Foreground = Brushes.White, FontFamily = new FontFamily("Consolas"), FontSize = 12 };
            var label = new TextBlock { Text = r.Label, Foreground = new SolidColorBrush(Color.FromRgb(0xB3, 0xB3, 0xB3)) };
            Grid.SetColumn(label, 1);
            row.Children.Add(keys);
            row.Children.Add(label);
            LayoutList.Items.Add(row);
        }
    }

    private void RenderStatus(string? error = null)
    {
        bool ok = App.Current.Api.IsConfigured;
        DisconnectButton.IsEnabled = ok;
        ConnectButton.Content = ok ? "Reconnecter" : "Connecter Spotify";
        StatusText.Foreground = error != null ? new SolidColorBrush(Color.FromRgb(0xE5, 0x53, 0x4B))
            : ok ? (Brush)FindResource("Green") : new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A));
        StatusText.Text = error ?? (ok ? "Connecté : like et volume du lecteur Spotify actifs." : "Non connecté : pas de like, volume via le mixeur Windows.");
        if (ok && error == null) _ = CheckPlayerScopeAsync();
    }

    private async System.Threading.Tasks.Task CheckPlayerScopeAsync()
    {
        if (await App.Current.Api.CanControlPlayerAsync()) return;
        if (!App.Current.Api.IsConfigured) return;
        StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xA3, 0x3D));
        StatusText.Text = "Connecté pour le like, mais pas pour le volume : cliquez sur « Reconnecter » pour autoriser le contrôle du lecteur.";
    }
}
