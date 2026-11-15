using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Lueur.Core;

namespace Lueur;

/// <summary>Carte "Routines" et son editeur.</summary>
public partial class MainWindow
{
    readonly RoutineEngine _routines;
    Routine? _editing;

    static readonly (DayOfWeek Day, string Label)[] DayLabels =
    {
        (DayOfWeek.Monday, "Lun"), (DayOfWeek.Tuesday, "Mar"), (DayOfWeek.Wednesday, "Mer"), (DayOfWeek.Thursday, "Jeu"),
        (DayOfWeek.Friday, "Ven"), (DayOfWeek.Saturday, "Sam"), (DayOfWeek.Sunday, "Dim"),
    };

    void InitRoutines()
    {
        foreach (var (day, label) in DayLabels)
            DayChips.Children.Add(new ToggleButton { Style = (Style)FindResource("DayChip"), Content = label, Tag = day });

        _routines.Ran += r => Dispatcher.BeginInvoke(() =>
        {
            ShowToast($"Routine « {r.Name} » lancee.");
            if (r.Look != null) LoadLook(r.Look); else RefreshPreview(lit: false);
            RefreshRoutines();
        });

        RefreshRoutines();
        _ = RefreshWeatherAsync();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(10) };
        timer.Tick += async (_, _) => await RefreshWeatherAsync();
        timer.Start();
    }

    async System.Threading.Tasks.Task RefreshWeatherAsync()
    {
        var w = await _routines.Weather.GetAsync();
        WeatherLabel.Text = w == null
            ? (_routines.Weather.HasLocation ? $"{_ctl.Settings.City} · meteo indisponible" : "Choisis ta ville dans les reglages (roue dentee) pour la meteo et le soleil")
            : $"{_ctl.Settings.City} · {w.Describe()} · lever {w.Sunrise:HH:mm} · coucher {w.Sunset:HH:mm}";
    }

    void RefreshRoutines()
    {
        var list = _ctl.Settings.Routines;
        RoutineList.ItemsSource = list.Select(r => new RoutineRow(r)).ToList();
        RoutinesEmpty.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        DefaultRoutinesButton.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    async void DefaultRoutines_Click(object sender, RoutedEventArgs e)
    {
        _ctl.Settings.Routines.AddRange(Routine.DefaultDay());
        _ctl.Settings.Save();
        RefreshRoutines();
        ShowToast("Journee type creee, envoi dans l'app IKEA...");
        await PublishAllAsync();
    }

    /// <summary>Envoie dans l'app IKEA les routines qui n'y sont pas encore.</summary>
    public async System.Threading.Tasks.Task PublishAllAsync()
    {
        int missing = _ctl.Settings.Routines.Count(r => r.IkeaSceneId == null);
        if (missing == 0 || _ctl.Ikea == null) return;
        var error = await _routines.PublishMissingAsync();
        RefreshRoutines();
        ShowToast(error ?? $"{missing} routine{(missing > 1 ? "s" : "")} ajoutee{(missing > 1 ? "s" : "")} dans l'app IKEA.");
    }

    async System.Threading.Tasks.Task PublishAsync(Routine r)
    {
        try { await _routines.PublishAsync(r); }
        catch (Exception ex) { ShowToast($"« {r.Name} » non publiee dans l'app IKEA : {ex.Message}"); }
        RefreshRoutines();
    }

    async void RoutineSwitch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { Tag: RoutineRow row } box)
        {
            row.Routine.Enabled = box.IsChecked == true;
            _ctl.Settings.Save();
            // L'horaire de la scene IKEA est active/desactive avec la routine.
            if (row.Routine.IkeaSceneId != null) await PublishAsync(row.Routine);
        }
    }

    async void RunRoutine_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: RoutineRow row })
            await _routines.RunAsync(row.Routine);
    }

    async void DeleteRoutine_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: RoutineRow row }) return;
        var answer = MessageBox.Show(this, $"Supprimer la routine « {row.Name} » (aussi dans l'app IKEA) ?", "Lueur", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;
        try { await _routines.UnpublishAsync(row.Routine); }
        catch (Exception ex) { ShowToast("Scene IKEA non supprimee : " + ex.Message); }
        _ctl.Settings.Routines.Remove(row.Routine);
        _ctl.Settings.Save();
        RefreshRoutines();
    }

    // ------------------------------------------------------------------ Editeur

    void AddRoutine_Click(object sender, RoutedEventArgs e) => OpenEditor(null);

    void EditRoutine_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: RoutineRow row }) OpenEditor(row.Routine);
    }

    void OpenEditor(Routine? routine)
    {
        _editing = routine;
        var r = routine ?? new Routine { Name = "", Look = CurrentLook };
        RoutineEditorTitle.Text = routine == null ? "Nouvelle routine" : "Modifier la routine";
        RoutineName.Text = r.Name;
        (r.Trigger switch { RoutineTrigger.Sunrise => TrigSunrise, RoutineTrigger.Sunset => TrigSunset, _ => TrigTime }).IsChecked = true;
        RoutineTime.Text = r.Time;
        RoutineOffset.Text = r.OffsetMinutes.ToString();
        RoutineUntil.Text = r.Until;
        foreach (ToggleButton chip in DayChips.Children) chip.IsChecked = r.Days.Contains((DayOfWeek)chip.Tag);
        (r.Condition switch
        {
            RoutineCondition.Rain => CondRain,
            RoutineCondition.Clear => CondClear,
            RoutineCondition.Cloudy => CondCloudy,
            _ => CondAlways,
        }).IsChecked = true;
        // En modification, "couleur actuelle" garde le look enregistre tant qu'on ne touche a rien.
        ActLookLabel.Text = routine?.Look != null ? routine.Look.Describe() : "Couleur actuelle · " + CurrentLook.Describe();
        (r.Look == null ? ActOff : ActLook).IsChecked = true;
        RoutineError.Visibility = Visibility.Collapsed;
        RoutineOverlay.Visibility = Visibility.Visible;
        RoutineForm_Changed(this, new RoutedEventArgs());
    }

    void RoutineForm_Changed(object sender, RoutedEventArgs e)
    {
        // Appele aussi pendant InitializeComponent, avant que tous les champs existent.
        if (TimeField == null || OffsetField == null || UntilField == null || TimeFieldLabel == null) return;
        bool sun = TrigSunrise.IsChecked == true || TrigSunset.IsChecked == true;
        bool weather = CondAlways.IsChecked != true;
        TimeField.Visibility = sun ? Visibility.Collapsed : Visibility.Visible;
        OffsetField.Visibility = sun ? Visibility.Visible : Visibility.Collapsed;
        UntilField.Visibility = weather && !sun ? Visibility.Visible : Visibility.Collapsed;
        TimeFieldLabel.Text = weather ? "Surveiller a partir de (HH:mm)" : "Heure (HH:mm)";
    }

    void RoutineCancel_Click(object sender, RoutedEventArgs e) => RoutineOverlay.Visibility = Visibility.Collapsed;

    async void RoutineSave_Click(object sender, RoutedEventArgs e)
    {
        string name = RoutineName.Text.Trim();
        var days = DayChips.Children.OfType<ToggleButton>().Where(c => c.IsChecked == true).Select(c => (DayOfWeek)c.Tag).ToList();
        if (name.Length == 0) { RoutineFail("Donne un nom a la routine."); return; }
        if (days.Count == 0) { RoutineFail("Choisis au moins un jour."); return; }
        if (!TimeSpan.TryParse(RoutineTime.Text, out _) || !TimeSpan.TryParse(RoutineUntil.Text, out _))
        { RoutineFail("Heure invalide : utilise le format HH:mm, par exemple 07:30."); return; }
        if (!int.TryParse(RoutineOffset.Text, out int offset)) { RoutineFail("Le decalage doit etre un nombre de minutes."); return; }

        var r = _editing ?? new Routine();
        r.Name = name;
        r.Trigger = TrigSunrise.IsChecked == true ? RoutineTrigger.Sunrise : TrigSunset.IsChecked == true ? RoutineTrigger.Sunset : RoutineTrigger.Time;
        r.Time = Routine.ParseTime(RoutineTime.Text).ToString(@"hh\:mm");
        r.Until = Routine.ParseTime(RoutineUntil.Text).ToString(@"hh\:mm");
        r.OffsetMinutes = offset;
        r.Days = days;
        r.Condition = CondRain.IsChecked == true ? RoutineCondition.Rain
            : CondClear.IsChecked == true ? RoutineCondition.Clear
            : CondCloudy.IsChecked == true ? RoutineCondition.Cloudy
            : RoutineCondition.Always;
        if (ActOff.IsChecked == true) r.Look = null;
        else if (_editing?.Look == null) r.Look = CurrentLook;
        r.LastRun = null;

        if (_editing == null) _ctl.Settings.Routines.Add(r);
        _ctl.Settings.Save();
        RoutineOverlay.Visibility = Visibility.Collapsed;
        RefreshRoutines();
        await PublishAsync(r);
    }

    void RoutineFail(string message)
    {
        RoutineError.Text = message;
        RoutineError.Visibility = Visibility.Visible;
    }
}

/// <summary>Ligne de la liste des routines.</summary>
public sealed record RoutineRow(Routine Routine)
{
    public string Name => Routine.Name;
    public string Summary => Routine.Summary();
    public bool Enabled => Routine.Enabled;
}
