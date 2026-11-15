using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Lueur.Core;

/// <summary>
/// Verifie les routines toutes les 30 secondes et lance celles dont c'est l'heure.
/// Une routine horaire se lance si on est dans les 10 minutes qui suivent son heure (rattrape un PC
/// sorti de veille) ; une routine meteo se lance des que le ciel correspond, entre son heure et Until.
/// </summary>
public sealed class RoutineEngine : IDisposable
{
    readonly LightController _ctl;
    readonly CancellationTokenSource _cts = new();
    static readonly TimeSpan Grace = TimeSpan.FromMinutes(10);

    public WeatherService Weather { get; } = new();
    /// <summary>Une routine vient de se lancer (nom), pour la notification.</summary>
    public event Action<Routine>? Ran;

    public RoutineEngine(LightController controller)
    {
        _ctl = controller;
        Weather.SetLocation(controller.Settings.Latitude, controller.Settings.Longitude);
    }

    public void Start() => _ = LoopAsync(_cts.Token);

    async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await TickAsync(DateTime.Now, ct); }
            catch (Exception) when (!ct.IsCancellationRequested) { }
            try { await Task.Delay(TimeSpan.FromSeconds(30), ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    async Task TickAsync(DateTime now, CancellationToken ct)
    {
        var due = _ctl.Settings.Routines
            .Where(r => r.Enabled && !r.RunsOnHub && r.Days.Contains(now.DayOfWeek) && r.LastRun?.Date != now.Date)
            .ToList();
        if (due.Count == 0) return;

        var weather = await Weather.GetAsync(ct);
        foreach (var r in due)
        {
            var at = r.TodayAt(weather);
            if (at == null || now < at) continue;

            if (r.Condition == RoutineCondition.Always)
            {
                if (now - at > Grace) continue;
            }
            else
            {
                if (now.TimeOfDay > Routine.ParseTime(r.Until)) continue;
                if (weather == null || !Matches(r.Condition, weather.Sky)) continue;
            }

            r.LastRun = now;
            _ctl.Settings.Save();
            await RunAsync(r);
        }
    }

    static bool Matches(RoutineCondition c, Sky sky) => c switch
    {
        RoutineCondition.Rain => sky is Sky.Rain or Sky.Snow,
        RoutineCondition.Clear => sky == Sky.Clear,
        RoutineCondition.Cloudy => sky == Sky.Cloudy,
        _ => true,
    };

    /// <summary>Lance une routine tout de suite (aussi utilise par le bouton "tester").</summary>
    public async Task RunAsync(Routine r)
    {
        if (r.Look == null) await _ctl.AllOffAsync();
        else await _ctl.ApplyAsync(r.Look, LookParts.All);
        Ran?.Invoke(r);
    }

    // ------------------------------------------------------------------ Publication dans l'app IKEA

    /// <summary>
    /// Cree (ou recree) la scene IKEA de la routine. Heure fixe sans meteo : la scene porte l'horaire et
    /// la DIRIGERA l'execute seule, PC eteint. Soleil/meteo : la scene n'a que le declencheur "app"
    /// (lancable depuis l'app IKEA ou Siri) et c'est Lueur qui la declenche au bon moment.
    /// </summary>
    public async Task PublishAsync(Routine r)
    {
        var ikea = _ctl.Ikea;
        if (ikea == null || _ctl.Lights.Count == 0) return;
        await UnpublishAsync(r);

        var triggers = new List<object> { new { type = "app", disabled = false } };
        if (r.Trigger == RoutineTrigger.Time && r.Condition == RoutineCondition.Always)
        {
            triggers.Add(new
            {
                type = "time",
                disabled = !r.Enabled,
                trigger = new { days = r.Days.Select(d => d.ToString()[..3]).ToArray(), time = r.Time },
            });
        }

        var actions = _ctl.Lights.Select(l => (object)new
        {
            type = "device",
            id = l.Id,
            enabled = true,
            attributes = SceneAttributes(l, r.Look),
        }).ToList();

        try
        {
            r.IkeaSceneId = await ikea.CreateSceneAsync(r.Name, IconFor(r), triggers, actions);
        }
        catch (HttpRequestException ex) when (ex.Message.Contains("icon"))
        {
            r.IkeaSceneId = await ikea.CreateSceneAsync(r.Name, "scenes_sun_horizon", triggers, actions);
        }
        _ctl.Settings.Save();
    }

    /// <summary>Publie les routines qui ne sont pas encore dans l'app IKEA. Renvoie l'erreur eventuelle.</summary>
    public async Task<string?> PublishMissingAsync()
    {
        foreach (var r in _ctl.Settings.Routines.Where(r => r.IkeaSceneId == null).ToList())
        {
            try { await PublishAsync(r); }
            catch (Exception ex) { return $"« {r.Name} » non publiee dans l'app IKEA : {ex.Message}"; }
        }
        return null;
    }

    public async Task UnpublishAsync(Routine r)
    {
        if (r.IkeaSceneId == null || _ctl.Ikea == null) return;
        await _ctl.Ikea.DeleteSceneAsync(r.IkeaSceneId);
        r.IkeaSceneId = null;
        _ctl.Settings.Save();
    }

    static Dictionary<string, object> SceneAttributes(IkeaLight l, LightLook? look)
    {
        if (look == null) return new() { ["isOn"] = false };
        var a = new Dictionary<string, object> { ["isOn"] = true };
        if (l.CanDim) a["lightLevel"] = Math.Clamp((int)Math.Round(look.Level * 100), 1, 100);
        if (!look.IsWhite && l.CanColor)
        {
            a["colorHue"] = Math.Round(look.Hue);
            a["colorSaturation"] = Math.Round(look.Saturation, 2);
        }
        else if (l.CanTemperature)
        {
            a["colorTemperature"] = Math.Clamp(look.IsWhite ? look.Kelvin : 2700, l.KelvinWarm, l.KelvinCool);
        }
        return a;
    }

    static string IconFor(Routine r) => r switch
    {
        { Look: null } => "scenes_moon",
        { Condition: RoutineCondition.Rain } => "scenes_hot_drink",
        { Condition: RoutineCondition.Clear } => "scenes_leaf",
        { Trigger: RoutineTrigger.Sunrise } => "scenes_wake_up",
        { Trigger: RoutineTrigger.Sunset } => "scenes_sun_horizon",
        _ when Routine.ParseTime(r.Time).Hours < 10 => "scenes_wake_up",
        _ => "scenes_heart",
    };

    public void Dispose() => _cts.Cancel();
}
