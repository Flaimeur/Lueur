using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Lueur.Core;

namespace Lueur;

/// <summary>Reglages (ville, mises a jour, Lumen), saisie de texte, fichier d'etat pour Lumen.</summary>
public partial class MainWindow
{
    readonly Updater _updater = new();
    DispatcherTimer? _stateTimer;
    (bool Lit, LightLook Look)? _pendingState;
    Action<string>? _promptDone;

    void InitSettings()
    {
        VersionLabel.Text = $"Version {Updater.CurrentVersion} · github.com/{Updater.Repository}";
        AutoUpdateSwitch.IsChecked = _ctl.Settings.AutoUpdate;
        RefreshCityLabel();

        // Premiere verification 1 min apres le lancement (le PC finit de demarrer), puis toutes les 6 h.
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        timer.Tick += async (_, _) =>
        {
            timer.Interval = TimeSpan.FromHours(6);
            if (_ctl.Settings.AutoUpdate && _updater.Ready == null) await CheckForUpdateAsync(download: true, quiet: true);
        };
        timer.Start();
    }

    void Settings_Click(object sender, RoutedEventArgs e)
    {
        CityBox.Text = "";
        CityResults.ItemsSource = null;
        UpdateStatus.Text = _updater.Ready != null ? $"Version {_updater.Ready} prete : installee au prochain demarrage." : "";
        SettingsOverlay.Visibility = Visibility.Visible;
    }

    void SettingsClose_Click(object sender, RoutedEventArgs e) => SettingsOverlay.Visibility = Visibility.Collapsed;
    void SettingsBackdrop_Click(object sender, MouseButtonEventArgs e) => SettingsOverlay.Visibility = Visibility.Collapsed;

    // ------------------------------------------------------------------ Ville

    void RefreshCityLabel() => CityCurrent.Text = string.IsNullOrEmpty(_ctl.Settings.City)
        ? "Aucune ville : la meteo et le lever/coucher du soleil sont desactives."
        : $"Ville actuelle : {_ctl.Settings.City}";

    void CityBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) CitySearch_Click(sender, e);
    }

    async void CitySearch_Click(object sender, RoutedEventArgs e)
    {
        CitySearchButton.IsEnabled = false;
        try
        {
            var found = await WeatherService.FindCityAsync(CityBox.Text);
            if (found.Count == 0)
            {
                CityCurrent.Text = "Aucune ville trouvee. Essaie un autre nom (ex. « Lyon », « Bruxelles »).";
                CityResults.ItemsSource = null;
                return;
            }
            CityResults.ItemsSource = found.Select(f =>
            {
                var b = new Button { Style = (Style)FindResource("GhostButton"), Height = 34, FontSize = 12, Margin = new Thickness(0, 4, 0, 0), Content = f.Label, HorizontalContentAlignment = HorizontalAlignment.Left };
                b.Click += async (_, _) => await ChooseCityAsync(f.Label, f.Lat, f.Lon);
                return b;
            }).ToList();
        }
        catch (Exception ex)
        {
            CityCurrent.Text = "Recherche impossible : " + ex.Message;
        }
        finally { CitySearchButton.IsEnabled = true; }
    }

    async Task ChooseCityAsync(string label, double lat, double lon)
    {
        _ctl.Settings.City = label;
        _ctl.Settings.Latitude = lat;
        _ctl.Settings.Longitude = lon;
        _ctl.Settings.Save();
        _routines.Weather.SetLocation(lat, lon);
        CityResults.ItemsSource = null;
        RefreshCityLabel();
        await RefreshWeatherAsync();
        _ = _adaptive.ResolveAsync().ContinueWith(_ => Dispatcher.BeginInvoke(RefreshAdaptive));
    }

    // ------------------------------------------------------------------ Mises a jour

    void AutoUpdate_Click(object sender, RoutedEventArgs e)
    {
        _ctl.Settings.AutoUpdate = AutoUpdateSwitch.IsChecked == true;
        _ctl.Settings.Save();
    }

    async void CheckUpdate_Click(object sender, RoutedEventArgs e) => await CheckForUpdateAsync(download: true, quiet: false);

    async Task CheckForUpdateAsync(bool download, bool quiet)
    {
        CheckUpdateButton.IsEnabled = false;
        if (!quiet) UpdateStatus.Text = "Recherche d'une nouvelle version...";
        try
        {
            var found = await _updater.CheckAsync(download);
            if (_updater.Ready != null)
            {
                UpdateStatus.Text = $"Version {_updater.Ready} prete : installee au prochain demarrage.";
                UpdateBannerText.Text = $"Lueur {_updater.Ready} est prete.";
                UpdateBanner.Visibility = Visibility.Visible;
            }
            else if (!quiet)
            {
                UpdateStatus.Text = _updater.LastError ?? (found == null ? $"Lueur est a jour ({Updater.CurrentVersion})." : $"Version {found} disponible.");
            }
        }
        finally { CheckUpdateButton.IsEnabled = true; }
    }

    void UpdateRestart_Click(object sender, RoutedEventArgs e)
    {
        Updater.RestartNow(new[] { "--minimized" });
        ((App)Application.Current).Quit();
    }

    void UpdateBannerClose_Click(object sender, RoutedEventArgs e) => UpdateBanner.Visibility = Visibility.Collapsed;

    void OpenDataFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppSettings.Folder);
        Process.Start(new ProcessStartInfo("explorer.exe", AppSettings.Folder) { UseShellExecute = true });
    }

    // ------------------------------------------------------------------ Saisie de texte

    /// <summary>Petite fenetre de saisie ; done recoit le texte (non vide) quand on valide.</summary>
    void AskText(string title, string label, string initial, Action<string> done)
    {
        PromptTitle.Text = title;
        PromptLabel.Text = label;
        PromptBox.Text = initial;
        _promptDone = done;
        PromptOverlay.Visibility = Visibility.Visible;
        PromptBox.Focus();
        PromptBox.SelectAll();
    }

    void PromptOk_Click(object sender, RoutedEventArgs e)
    {
        var text = PromptBox.Text.Trim();
        if (text.Length == 0) return;
        PromptOverlay.Visibility = Visibility.Collapsed;
        var done = _promptDone;
        _promptDone = null;
        done?.Invoke(text);
    }

    void PromptCancel_Click(object sender, RoutedEventArgs e)
    {
        _promptDone = null;
        PromptOverlay.Visibility = Visibility.Collapsed;
    }

    void PromptBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) PromptOk_Click(sender, e);
        else if (e.Key == Key.Escape) PromptCancel_Click(sender, e);
    }

    // ------------------------------------------------------------------ Etat pour Lumen

    /// <summary>
    /// Ecrit %APPDATA%\Lueur\state.json (au plus 2 fois par seconde) : l'etat en direct de Lueur pour Lumen
    /// et toute autre appli. Format documente dans LUMEN.md.
    /// </summary>
    void PublishState(bool lit, LightLook look)
    {
        _pendingState = (lit, look);
        if (_stateTimer == null)
        {
            _stateTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _stateTimer.Tick += (_, _) =>
            {
                _stateTimer.Stop();
                if (_pendingState is { } s) WriteState(s.Lit, s.Look);
            };
        }
        _stateTimer.Stop();
        _stateTimer.Start();
    }

    void WriteState(bool lit, LightLook look)
    {
        var rgb = look.BaseRgb;
        string mode = _ctl.IsScreenModeOn ? "screen" : _adaptive.IsOn ? "adaptive" : "manual";
        var state = new
        {
            app = "Lueur",
            version = Updater.CurrentVersion.ToString(),
            exe = Environment.ProcessPath,
            running = true,
            on = lit,
            color = $"#{rgb.R:X2}{rgb.G:X2}{rgb.B:X2}",
            level = (int)Math.Round(look.Level * 100),
            description = lit ? look.Describe() : "Eteint",
            mode,
            adaptiveProfile = _adaptive.IsOn ? _adaptive.Current.Key : null,
            roomLights = _ctl.Lights.Count,
            pcDevices = _ctl.Pc.Devices.Count(d => !_ctl.Pc.Excluded.Contains(d.Name)),
            scenes = _ctl.Settings.Scenes.Select(s => s.Name).ToArray(),
            commands = new[] { "--on", "--off", "--toggle", "--warm", "--screen", "--show", "--scene <nom>", "--color #RRGGBB", "--level 0-100", "--adaptive on|off|auto|sun|rain|cloud" },
            updatedAt = DateTimeOffset.Now,
        };
        try
        {
            Directory.CreateDirectory(AppSettings.Folder);
            var path = Path.Combine(AppSettings.Folder, "state.json");
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(path + ".tmp", path, overwrite: true);
        }
        catch (Exception) { }
    }

    /// <summary>A la fermeture : Lumen doit voir que Lueur ne tourne plus.</summary>
    void WriteStoppedState()
    {
        try
        {
            var path = Path.Combine(AppSettings.Folder, "state.json");
            if (!File.Exists(path)) return;
            var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path));
            if (node == null) return;
            node["running"] = false;
            node["updatedAt"] = DateTimeOffset.Now;
            File.WriteAllText(path, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception) { }
    }
}
