using System;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Lueur.Core;

namespace Lueur;

public partial class MainWindow : Window
{
    readonly LightController _ctl;
    readonly Coalescer _sender = new();
    LookParts _pendingParts;
    bool _updatingUi;
    bool _reallyClose;
    CancellationTokenSource? _pairCts;

    static readonly (double Hue, double Sat, bool White, int Kelvin)[] SwatchList =
    {
        (0, 1, false, 0), (24, 1, false, 0), (48, 1, false, 0), (120, 1, false, 0), (175, 1, false, 0),
        (205, 1, false, 0), (238, 1, false, 0), (268, 1, false, 0), (300, 1, false, 0), (330, 0.9, false, 0),
        (0, 0, true, 2202), (0, 0, true, 3000), (0, 0, true, 4000),
    };

    public MainWindow(LightController controller, RoutineEngine routines, AdaptiveEngine adaptive)
    {
        _ctl = controller;
        _routines = routines;
        _adaptive = adaptive;
        InitializeComponent();

        BuildSwatches();
        BuildScenes();
        InitRoutines();
        InitAdaptive();
        InitSettings();
        RoomList.ItemsSource = _ctl.Lights;
        _ctl.Lights.CollectionChanged += (_, _) => RefreshStatus();
        _ctl.ConnectionChanged += RefreshStatus;
        _ctl.Notice += msg => Dispatcher.BeginInvoke(() => ShowToast(msg));
        _ctl.RoomChangedExternally += look => Dispatcher.BeginInvoke(() => OnRoomChangedExternally(look));

        _ctl.ScreenColor += OnScreenColor;
        _ctl.ScreenModeChanged += () =>
        {
            ScreenSwitch.IsChecked = _ctl.IsScreenModeOn;
            ScreenLiveBadge.Visibility = _ctl.IsScreenModeOn ? Visibility.Visible : Visibility.Collapsed;
            if (!_ctl.IsScreenModeOn) RefreshPreview(lit: true);
        };

        SyncSwitch.IsChecked = _ctl.Settings.SyncPcWithRoom;
        GHubSwitch.IsChecked = _ctl.Settings.CloseGHub;
        StartupSwitch.IsChecked = StartupRegistration.IsEnabled;
        LoadLook(_ctl.Settings.Look);

        SourceInitialized += (_, _) => { WindowEffects.Apply(this); Hotkeys.Register(this, OnHotkey); };
    }

    // ------------------------------------------------------------------ Look courant <-> UI

    LightLook CurrentLook => new(
        ModeWhite.IsChecked == true,
        HueSlider.Value,
        SatSlider.Value / 100,
        (int)Math.Round(TempSlider.Value / 50) * 50,
        LevelSlider.Value / 100);

    void LoadLook(LightLook look)
    {
        _updatingUi = true;
        (look.IsWhite ? ModeWhite : ModeColor).IsChecked = true;
        HueSlider.Value = look.Hue;
        SatSlider.Value = look.Saturation * 100;
        TempSlider.Value = Math.Clamp(look.Kelvin, 2200, 4000);
        LevelSlider.Value = Math.Clamp(look.Level * 100, 1, 100);
        _updatingUi = false;
        RefreshPreview(lit: true);
    }

    void RefreshPreview(bool lit)
    {
        var look = CurrentLook;
        ColorPanel.Visibility = look.IsWhite ? Visibility.Collapsed : Visibility.Visible;
        WhitePanel.Visibility = look.IsWhite ? Visibility.Visible : Visibility.Collapsed;
        KelvinLabel.Text = $"{look.Kelvin} K";
        LevelLabel.Text = $"{(int)Math.Round(look.Level * 100)} %";

        var baseColor = look.BaseRgb.ToMedia();
        var pureHue = ColorMath.FromHsv(look.Hue, 1, 1).ToMedia();
        SatEnd.Color = pureHue;
        LevelEnd.Color = baseColor;
        var knob = new SolidColorBrush(baseColor);
        HueSlider.Tag = new SolidColorBrush(pureHue);
        SatSlider.Tag = knob;
        TempSlider.Tag = knob;
        LevelSlider.Tag = knob;

        // L'accent de toute l'interface prend la couleur choisie.
        Application.Current.Resources["AccentBrush"] = new SolidColorBrush(Darken(baseColor, look.IsWhite ? 0.55 : 0.85));

        LookLabel.Text = lit ? look.Describe() : "Eteint";
        AnimateOrb(baseColor, lit ? 0.35 + look.Level * 0.65 : 0.12);
        PublishState(lit, look);
    }

    void AnimateOrb(Color color, double intensity)
    {
        var duration = TimeSpan.FromMilliseconds(260);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        void To(Animatable target, DependencyProperty prop, Color c) =>
            target.BeginAnimation(prop, new ColorAnimation(c, duration) { EasingFunction = ease });

        To(GlowBrush, SolidColorBrush.ColorProperty, color);
        To(OrbMid, GradientStop.ColorProperty, color);
        To(OrbEdge, GradientStop.ColorProperty, Darken(color, 0.35));
        To(OrbHi, GradientStop.ColorProperty, Mix(color, Colors.White, 0.8));
        To(AmbientStop, GradientStop.ColorProperty, color);
        OrbGlow.BeginAnimation(OpacityProperty, new DoubleAnimation(intensity, duration) { EasingFunction = ease });
        OrbHost.BeginAnimation(OpacityProperty, new DoubleAnimation(intensity < 0.2 ? 0.45 : 1, duration));
    }

    static Color Darken(Color c, double f) => Color.FromRgb((byte)(c.R * f), (byte)(c.G * f), (byte)(c.B * f));
    static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    // ------------------------------------------------------------------ Envoi aux lampes

    /// <summary>Les curseurs bougent vite : on n'envoie que le dernier etat, une requete a la fois.</summary>
    void Send(LookParts parts)
    {
        _pendingParts |= parts;
        _sender.Post(async () =>
        {
            var p = _pendingParts;
            _pendingParts = LookParts.None;
            await _ctl.ApplyAsync(CurrentLook, p);
        });
    }

    void Color_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingUi || !IsLoaded) return;
        RefreshPreview(lit: true);
        Send(LookParts.Power | LookParts.Color);
    }

    void Level_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingUi || !IsLoaded) return;
        RefreshPreview(lit: true);
        Send(LookParts.Power | LookParts.Level);
    }

    void Mode_Checked(object sender, RoutedEventArgs e)
    {
        if (_updatingUi || !IsLoaded) return;
        RefreshPreview(lit: true);
        Send(LookParts.All);
    }

    async void AllOn_Click(object sender, RoutedEventArgs e)
    {
        RefreshPreview(lit: true);
        await _ctl.ApplyAsync(CurrentLook, LookParts.All);
    }

    async void AllOff_Click(object sender, RoutedEventArgs e)
    {
        RefreshPreview(lit: false);
        await _ctl.AllOffAsync();
    }

    void Orb_Click(object sender, MouseButtonEventArgs e)
    {
        if (_ctl.AnyRoomLightOn || _ctl.Pc.IsLit) AllOff_Click(sender, e);
        else AllOn_Click(sender, e);
    }

    async void LightSwitch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { Tag: IkeaLight light } box)
        {
            bool on = box.IsChecked == true;
            box.IsChecked = light.IsOn; // l'etat reel revient par le modele
            await _ctl.SetLightOnAsync(light, on);
        }
    }

    void OnRoomChangedExternally(LightLook? look)
    {
        if (look == null)
        {
            RefreshPreview(lit: false);
            return;
        }
        LoadLook(look);
    }

    // ------------------------------------------------------------------ Couleurs et ambiances

    void BuildSwatches()
    {
        foreach (var s in SwatchList)
        {
            var look = new LightLook(s.White, s.Hue, s.Sat, s.White ? s.Kelvin : 2700, 0);
            var b = new Button
            {
                Style = (Style)FindResource("Swatch"),
                Background = new SolidColorBrush(look.BaseRgb.ToMedia()),
                Margin = new Thickness(0, 0, 4, 0),
                ToolTip = s.White ? $"Blanc {s.Kelvin} K" : null,
            };
            b.Click += (_, _) =>
            {
                LoadLook(CurrentLook with { IsWhite = s.White, Hue = s.Hue, Saturation = s.Sat, Kelvin = s.White ? s.Kelvin : (int)TempSlider.Value });
                Send(LookParts.All);
            };
            Swatches.Children.Add(b);
        }
    }

    /// <summary>
    /// Tuiles des ambiances (celles des reglages, modifiables) + une tuile « + » pour enregistrer la couleur
    /// actuelle. Clic droit sur une ambiance : la remplacer par la couleur actuelle, ou la supprimer.
    /// </summary>
    void BuildScenes()
    {
        Scenes.Children.Clear();
        foreach (var scene in _ctl.Settings.Scenes)
        {
            var content = new StackPanel();
            content.Children.Add(new TextBlock { Text = scene.Name, FontSize = 15, FontWeight = FontWeights.SemiBold, FontFamily = (FontFamily)FindResource("DisplayFont"), TextTrimming = TextTrimming.CharacterEllipsis });
            content.Children.Add(new TextBlock { Text = scene.Sub, FontSize = 11, Opacity = 0.8, Margin = new Thickness(0, 1, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });
            var (from, to) = scene.Gradient();
            var tile = new Button
            {
                Style = (Style)FindResource("SceneTile"),
                Content = content,
                Background = new LinearGradientBrush(from.ToMedia(), to.ToMedia(), 35),
                ToolTip = "Clic droit : remplacer ou supprimer",
            };
            tile.Click += async (_, _) =>
            {
                LoadLook(scene.Look);
                await _ctl.ApplyAsync(scene.Look, LookParts.All);
            };
            tile.ContextMenu = SceneMenu(scene);
            Scenes.Children.Add(tile);
        }

        var add = new Button
        {
            Style = (Style)FindResource("SceneTile"),
            Background = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)),
            Content = new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = "+  Ajouter", FontSize = 15, FontWeight = FontWeights.SemiBold, FontFamily = (FontFamily)FindResource("DisplayFont") },
                    new TextBlock { Text = "Enregistre la couleur actuelle", FontSize = 11, Opacity = 0.7, Margin = new Thickness(0, 1, 0, 0) },
                },
            },
        };
        add.Click += (_, _) => AskText("Nouvelle ambiance", "Nom de l'ambiance", "", name =>
        {
            var look = CurrentLook;
            _ctl.Settings.Scenes.RemoveAll(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
            _ctl.Settings.Scenes.Add(new SceneDef(name, look.Describe(), look));
            _ctl.Settings.Save();
            BuildScenes();
            ShowToast($"Ambiance « {name} » enregistrée.");
        });
        Scenes.Children.Add(add);
    }

    ContextMenu SceneMenu(SceneDef scene)
    {
        var menu = new ContextMenu();
        var replace = new MenuItem { Header = "Remplacer par la couleur actuelle" };
        replace.Click += (_, _) =>
        {
            var i = _ctl.Settings.Scenes.IndexOf(scene);
            if (i < 0) return;
            var look = CurrentLook;
            _ctl.Settings.Scenes[i] = scene with { Look = look, Sub = look.Describe() };
            _ctl.Settings.Save();
            BuildScenes();
        };
        var rename = new MenuItem { Header = "Renommer" };
        rename.Click += (_, _) => AskText("Renommer l'ambiance", "Nouveau nom", scene.Name, name =>
        {
            var i = _ctl.Settings.Scenes.IndexOf(scene);
            if (i < 0) return;
            _ctl.Settings.Scenes[i] = scene with { Name = name };
            _ctl.Settings.Save();
            BuildScenes();
        });
        var delete = new MenuItem { Header = "Supprimer" };
        delete.Click += (_, _) =>
        {
            _ctl.Settings.Scenes.Remove(scene);
            _ctl.Settings.Save();
            BuildScenes();
        };
        menu.Items.Add(replace);
        menu.Items.Add(rename);
        menu.Items.Add(new Separator());
        menu.Items.Add(delete);
        return menu;
    }

    // ------------------------------------------------------------------ Etat des connexions

    void RefreshStatus()
    {
        bool paired = _ctl.IsPaired;
        var green = (SolidColorBrush)new BrushConverter().ConvertFrom("#34D399")!;
        var orange = (SolidColorBrush)new BrushConverter().ConvertFrom("#FBBF24")!;
        var grey = (SolidColorBrush)new BrushConverter().ConvertFrom("#6C6C7A")!;

        int lights = _ctl.Lights.Count;
        IkeaDot.Fill = !paired ? grey : _ctl.IkeaOnline ? (_ctl.IkeaLive ? green : orange) : orange;
        IkeaStatus.Text = !paired ? "DIRIGERA · non appairee"
            : _ctl.IkeaOnline ? $"DIRIGERA · {lights} lampe{(lights > 1 ? "s" : "")}"
            : "DIRIGERA · injoignable";
        RoomEmpty.Visibility = lights == 0 ? Visibility.Visible : Visibility.Collapsed;
        RoomEmpty.Text = !paired ? "Pas encore connecte a la DIRIGERA."
            : _ctl.IkeaOnline ? "Aucune lampe trouvee sur la DIRIGERA." : "La DIRIGERA ne repond pas.";
        RepairButton.Visibility = paired && _ctl.IkeaOnline ? Visibility.Collapsed : Visibility.Visible;

        var devices = _ctl.Pc.Devices;
        bool pcOk = _ctl.Pc.IsConnected;
        PcDot.Fill = pcOk ? green : orange;
        PcStatus.Text = pcOk ? $"OpenRGB · {devices.Count} appareil{(devices.Count > 1 ? "s" : "")}" : "OpenRGB · hors ligne";
        PcList.ItemsSource = pcOk
            ? devices.Select(d => new PcDeviceRow(d, !_ctl.Pc.Excluded.Contains(d.Name))).ToList()
            : null;
        PcEmpty.Visibility = pcOk && devices.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        PcEmpty.Text = !pcOk ? (_ctl.Pc.LastError ?? "OpenRGB n'est pas lance.")
            : "OpenRGB ne voit aucun peripherique. Lance-le une fois en administrateur.";
    }

    void PcSwitch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { Tag: PcDeviceRow row } box)
            _ctl.SetPcDeviceIncluded(row.Name, box.IsChecked == true);
    }

    async void ReconnectPc_Click(object sender, RoutedEventArgs e)
    {
        PcEmpty.Text = "Connexion a OpenRGB...";
        await _ctl.ReconnectPcAsync();
        if (_ctl.Pc.IsConnected) _ctl.Pc.SetAll(_ctl.AnyRoomLightOn ? CurrentLook.ToRgb() : Rgb.Black);
    }

    // ------------------------------------------------------------------ Appairage

    public void ShowPairing()
    {
        PairingOverlay.Visibility = Visibility.Visible;
        PairError.Visibility = Visibility.Collapsed;
        PairHint.Visibility = Visibility.Collapsed;
        HostBox.Text = _ctl.Settings.DirigeraHost ?? "";
        if (string.IsNullOrWhiteSpace(HostBox.Text)) Discover_Click(this, new RoutedEventArgs());
    }

    void ShowPairing_Click(object sender, RoutedEventArgs e) => ShowPairing();

    void PairLater_Click(object sender, RoutedEventArgs e)
    {
        _pairCts?.Cancel();
        PairingOverlay.Visibility = Visibility.Collapsed;
    }

    async void Discover_Click(object sender, RoutedEventArgs e)
    {
        DiscoverButton.IsEnabled = false;
        DiscoverButton.Content = "Recherche...";
        PairError.Visibility = Visibility.Collapsed;
        try
        {
            var ip = await DirigeraClient.DiscoverAsync(CancellationToken.None);
            if (ip != null) HostBox.Text = ip;
            else ShowPairError("Aucune DIRIGERA trouvee sur le reseau. Verifie qu'elle est branchee, ou saisis son IP.");
        }
        finally
        {
            DiscoverButton.IsEnabled = true;
            DiscoverButton.Content = "Rechercher";
        }
    }

    async void Pair_Click(object sender, RoutedEventArgs e)
    {
        var host = HostBox.Text.Trim();
        if (host.Length == 0) { ShowPairError("Indique l'adresse IP de la DIRIGERA."); return; }

        PairButton.IsEnabled = false;
        PairError.Visibility = Visibility.Collapsed;
        _pairCts = new CancellationTokenSource();
        var client = new DirigeraClient(host, null, null);
        try
        {
            PairHint.Visibility = Visibility.Visible;
            var progress = new Progress<int>(s => PairCountdown.Text = s.ToString());
            var token = await client.PairAsync(progress, _pairCts.Token);
            PairingOverlay.Visibility = Visibility.Collapsed;
            await _ctl.CompletePairingAsync(client, token);
            ShowToast("DIRIGERA connectee. Bienvenue dans Lueur.");
        }
        catch (OperationCanceledException)
        {
            client.Dispose();
        }
        catch (Exception ex)
        {
            client.Dispose();
            ShowPairError(ex is TimeoutException ? ex.Message : "Echec : " + ex.Message);
        }
        finally
        {
            PairButton.IsEnabled = true;
            PairHint.Visibility = Visibility.Collapsed;
        }
    }

    void ShowPairError(string message)
    {
        PairError.Text = message;
        PairError.Visibility = Visibility.Visible;
    }

    // ------------------------------------------------------------------ Mode ecran

    void Screen_Click(object sender, RoutedEventArgs e)
    {
        if (ScreenSwitch.IsChecked == true)
        {
            _ctl.ScreenModeIncludesRoom = ScreenRoomSwitch.IsChecked == true;
            _ctl.StartScreenMode();
            ScreenLiveBadge.Visibility = Visibility.Visible;
            LookSub.Text = "Le setup suit l'ecran";
        }
        else
        {
            _ctl.StopScreenMode();
        }
    }

    void ScreenRoom_Click(object sender, RoutedEventArgs e) =>
        _ctl.ScreenModeIncludesRoom = ScreenRoomSwitch.IsChecked == true;

    void OnScreenColor(Rgb color)
    {
        if (!_ctl.IsScreenModeOn) return;
        var (h, s, v) = ColorMath.ToHsv(color);
        var full = ColorMath.FromHsv(h, s, 1).ToMedia();
        LookLabel.Text = $"Ecran · {(int)Math.Round(v * 100)} %";
        Application.Current.Resources["AccentBrush"] = new SolidColorBrush(Darken(full, 0.85));
        AnimateOrb(full, 0.35 + v * 0.65);
    }

    // ------------------------------------------------------------------ Reglages

    void GHub_Click(object sender, RoutedEventArgs e)
    {
        _ctl.Settings.CloseGHub = GHubSwitch.IsChecked == true;
        _ctl.Settings.Save();
    }

    void Sync_Click(object sender, RoutedEventArgs e)
    {
        _ctl.Settings.SyncPcWithRoom = SyncSwitch.IsChecked == true;
        _ctl.Settings.Save();
    }

    void Startup_Click(object sender, RoutedEventArgs e)
    {
        StartupRegistration.Set(StartupSwitch.IsChecked == true);
    }

    void Shortcuts_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            DesktopShortcuts.Create();
            ShowToast("Raccourcis « Lueur - Tout allumer » et « Lueur - Tout eteindre » crees sur le Bureau.");
        }
        catch (Exception ex)
        {
            ShowToast("Impossible de creer les raccourcis : " + ex.Message);
        }
    }

    public void RunCommand(string verb) => RunCommand(new LueurCommand(verb, null));

    /// <summary>Commande venue d'une autre instance (Moonlight, Lumen, raccourci, .bat) : voir LueurCommand.</summary>
    // Les commandes passent une par une, dans l'ordre d'arrivee : un "--off" suivi d'un "--on" 200 ms plus
    // tard (Moonlight) ne doivent pas s'executer en meme temps, sinon le premier peut finir apres le second.
    public async void RunCommand(LueurCommand command)
    {
        await _commandGate.WaitAsync();
        try
        {
            switch (command.Verb)
            {
                case "show":
                    BringToFront();
                    break;
                case "scene":
                    var scene = _ctl.Settings.Scenes.FirstOrDefault(s => string.Equals(s.Name, command.Value?.Trim(), StringComparison.OrdinalIgnoreCase));
                    if (scene == null) { ShowToast($"Ambiance « {command.Value} » introuvable."); break; }
                    LoadLook(scene.Look);
                    await _ctl.ApplyAsync(scene.Look, LookParts.All);
                    break;
                case "color":
                    if (!LueurCommand.TryParseColor(command.Value, out var rgb)) { ShowToast($"Couleur « {command.Value} » invalide (format #RRGGBB)."); break; }
                    var (h, s, v) = ColorMath.ToHsv(rgb);
                    var colorLook = new LightLook(false, h, s, CurrentLook.Kelvin, Math.Max(0.05, v));
                    LoadLook(colorLook);
                    await _ctl.ApplyAsync(colorLook, LookParts.All);
                    break;
                case "level":
                    if (!int.TryParse(command.Value, out var pct)) break;
                    var levelLook = CurrentLook with { Level = Math.Clamp(pct, 1, 100) / 100.0 };
                    LoadLook(levelLook);
                    await _ctl.ApplyAsync(levelLook, LookParts.All);
                    break;
                case "adaptive":
                    var mode = command.Value?.Trim().ToLowerInvariant();
                    if (mode == "off") _adaptive.Stop();
                    else
                    {
                        if (mode is "auto" or "sun" or "rain" or "cloud") _adaptive.SetProfile(mode);
                        _adaptive.Start();
                    }
                    break;
                case "on":
                    RefreshPreview(lit: true);
                    await _ctl.AllOnAsync();
                    break;
                case "off":
                    RefreshPreview(lit: false);
                    await _ctl.AllOffAsync();
                    break;
                case "toggle":
                    bool lit = _ctl.AnyRoomLightOn || _ctl.Pc.IsLit;
                    RefreshPreview(lit: !lit);
                    await (lit ? _ctl.AllOffAsync() : _ctl.AllOnAsync());
                    break;
                case "screen":
                    ScreenSwitch.IsChecked = true;
                    Screen_Click(this, new RoutedEventArgs());
                    break;
                case "warm":
                    // Lumière de soirée (demandée par Lumen) : blanc chaud, faible.
                    LoadLook(LightLook.Warm);
                    await _ctl.ApplyAsync(LightLook.Warm, LookParts.All);
                    break;
            }
        }
        finally { _commandGate.Release(); }
    }

    readonly SemaphoreSlim _commandGate = new(1, 1);

    void OnHotkey(int id)
    {
        if (id == Hotkeys.AllOnId) AllOn_Click(this, new RoutedEventArgs());
        else if (id == Hotkeys.AllOffId) AllOff_Click(this, new RoutedEventArgs());
    }

    // ------------------------------------------------------------------ Fenetre

    public void ShowToast(string message)
    {
        ToastText.Text = message;
        var fade = new DoubleAnimationUsingKeyFrames();
        fade.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(180))));
        fade.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(4))));
        fade.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(4.4))));
        Toast.BeginAnimation(OpacityProperty, fade);
    }

    public void BringToFront()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    public void CloseForReal()
    {
        _reallyClose = true;
        WriteStoppedState();
        Close();
    }

    void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(CancelEventArgs e)
    {
        _ctl.Settings.Look = CurrentLook;
        _ctl.Settings.Save();
        if (!_reallyClose)
        {
            // La croix cache la fenetre : Lueur reste dans la zone de notification (raccourcis clavier actifs).
            e.Cancel = true;
            Hide();
            return;
        }
        Hotkeys.Unregister(this);
        base.OnClosing(e);
    }
}

/// <summary>Ligne de la liste "PC".</summary>
public sealed record PcDeviceRow(PcDevice Device, bool Included)
{
    public string Name => Device.Name;
    public string Subtitle => $"{Device.Kind} · {Device.LedCount} LED{(Device.LedCount > 1 ? "s" : "")}";
}

/// <summary>Execute seulement la derniere tache postee, une a la fois (pour les curseurs).</summary>
sealed class Coalescer
{
    Func<Task>? _pending;
    bool _running;

    public async void Post(Func<Task> work)
    {
        _pending = work;
        if (_running) return;
        _running = true;
        try
        {
            while (_pending != null)
            {
                var next = _pending;
                _pending = null;
                try { await next(); } catch (Exception) { }
                await Task.Delay(40);
            }
        }
        finally { _running = false; }
    }
}

/// <summary>Fond Mica + barre de titre sombre + coins arrondis de Windows 11.</summary>
static class WindowEffects
{
    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    const int DWMWA_SYSTEMBACKDROP_TYPE = 38;

    public static void Apply(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (HwndSource.FromHwnd(hwnd) is { } src) src.CompositionTarget.BackgroundColor = Colors.Transparent;
        int dark = 1, round = 2, mica = 2;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
        DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref mica, sizeof(int));
    }
}
