using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Lueur.Core;

namespace Lueur;

/// <summary>Carte "Eclairage adaptatif" : interrupteur, choix du profil et frise de la journee.</summary>
public partial class MainWindow
{
    readonly AdaptiveEngine _adaptive;
    bool _adaptiveUiLoading;

    void InitAdaptive()
    {
        _adaptiveUiLoading = true;
        (_ctl.Settings.AdaptiveProfile switch
        {
            "sun" => ProfSun,
            "rain" => ProfRain,
            "cloud" => ProfCloud,
            _ => ProfAuto,
        }).IsChecked = true;
        _adaptiveUiLoading = false;

        _adaptive.Changed += () => Dispatcher.BeginInvoke(RefreshAdaptive);
        _ = _adaptive.ResolveAsync().ContinueWith(_ => Dispatcher.BeginInvoke(RefreshAdaptive));

        // Le repere "maintenant" avance meme quand l'adaptatif est coupe.
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        timer.Tick += (_, _) => PlaceNowMarker();
        timer.Start();
    }

    void RefreshAdaptive()
    {
        bool on = _adaptive.IsOn;
        AdaptiveSwitch.IsChecked = on;
        AdaptiveBadge.Visibility = on ? Visibility.Visible : Visibility.Collapsed;

        var p = _adaptive.Current;
        string source = _ctl.Settings.AdaptiveProfile == "auto"
            ? $"Aujourd'hui : {p.Name} ({_routines.Weather.Last?.Describe().ToLowerInvariant() ?? "meteo inconnue"}). "
            : $"{p.Name}. ";
        AdaptiveMood.Text = source + p.Mood + ".";

        TimelineBar.Background = BuildTimeline(p);
        PlaceNowMarker();

        if (on && _adaptive.CurrentLook is { } look)
        {
            LoadLook(look);
            LookSub.Text = $"Adaptatif · {p.Name}";
        }
    }

    /// <summary>Degrade des 24 h du profil (un point toutes les 30 min), assez lumineux pour rester lisible la nuit.</summary>
    static Brush BuildTimeline(AdaptiveProfile p)
    {
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        for (int i = 0; i <= 48; i++)
        {
            var look = p.At(TimeSpan.FromMinutes(Math.Min(i * 30, 24 * 60 - 1)));
            var c = look.BaseRgb.Scale(0.25 + look.Level * 0.75).ToMedia();
            brush.GradientStops.Add(new GradientStop(c, i / 48.0));
        }
        brush.Freeze();
        return brush;
    }

    void PlaceNowMarker()
    {
        double frac = DateTime.Now.TimeOfDay.TotalMinutes / (24 * 60);
        NowMarker.Margin = new Thickness(Math.Max(0, Timeline.ActualWidth * frac - 1.5), -4, 0, -4);
    }

    void Timeline_SizeChanged(object sender, SizeChangedEventArgs e) => PlaceNowMarker();

    void Adaptive_Click(object sender, RoutedEventArgs e)
    {
        if (AdaptiveSwitch.IsChecked == true)
        {
            _adaptive.Start();
            if (!_ctl.AnyRoomLightOn) ShowToast("Adaptatif actif : il prendra le relais des que tu allumes (il ne rallume jamais seul).");
        }
        else
        {
            _adaptive.Stop();
            LookSub.Text = "Chambre + PC synchronises";
        }
    }

    void Profile_Checked(object sender, RoutedEventArgs e)
    {
        if (_adaptiveUiLoading || _adaptive == null || sender is not RadioButton { Tag: string key }) return;
        _adaptive.SetProfile(key);
    }
}
