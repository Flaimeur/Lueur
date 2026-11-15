using System;
using System.Collections.Generic;
using System.Linq;

namespace Lueur.Core;

public enum RoutineTrigger { Time, Sunrise, Sunset }

/// <summary>Condition meteo : la routine ne se lance que si le ciel correspond.</summary>
public enum RoutineCondition { Always, Rain, Clear, Cloudy }

/// <summary>
/// Une routine : "a telle heure (ou au lever/coucher du soleil), tels jours, si la meteo est X,
/// mettre ce look partout (chambre + PC) ou tout eteindre".
/// Les routines meteo surveillent le ciel dans une plage horaire (de Time a Until) et se lancent
/// une fois par jour, des que la condition devient vraie.
/// </summary>
public sealed class Routine
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Routine";
    public bool Enabled { get; set; } = true;

    public RoutineTrigger Trigger { get; set; } = RoutineTrigger.Time;
    /// <summary>Heure "HH:mm" (declencheur Time, ou debut de plage pour la meteo).</summary>
    public string Time { get; set; } = "08:00";
    /// <summary>Decalage en minutes par rapport au lever/coucher du soleil (negatif = avant).</summary>
    public int OffsetMinutes { get; set; }
    public List<DayOfWeek> Days { get; set; } = AllDays.ToList();

    public RoutineCondition Condition { get; set; } = RoutineCondition.Always;
    /// <summary>Fin de la plage de surveillance meteo "HH:mm".</summary>
    public string Until { get; set; } = "20:00";

    /// <summary>null = tout eteindre.</summary>
    public LightLook? Look { get; set; }

    /// <summary>Date du dernier lancement automatique (une seule fois par jour).</summary>
    public DateTime? LastRun { get; set; }

    /// <summary>Id de la scene creee sur la DIRIGERA (visible dans l'app IKEA), null si pas encore publiee.</summary>
    public string? IkeaSceneId { get; set; }

    /// <summary>
    /// Routine executee par la DIRIGERA elle-meme (heure fixe, sans condition meteo) : elle marche PC eteint,
    /// et Lueur ne la relance pas. Les autres (soleil, meteo) sont declenchees par Lueur.
    /// </summary>
    public bool RunsOnHub => IkeaSceneId != null && Trigger == RoutineTrigger.Time && Condition == RoutineCondition.Always;

    public static readonly DayOfWeek[] AllDays =
        { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday };
    public static readonly DayOfWeek[] WeekDays = AllDays.Take(5).ToArray();
    public static readonly DayOfWeek[] WeekEnd = AllDays.Skip(5).ToArray();

    public static TimeSpan ParseTime(string s) =>
        TimeSpan.TryParse(s, out var t) ? t : TimeSpan.FromHours(8);

    /// <summary>Heure de declenchement du jour (null si on ne connait pas encore le lever/coucher du soleil).</summary>
    public DateTime? TodayAt(WeatherNow? weather) => Trigger switch
    {
        RoutineTrigger.Time => DateTime.Today + ParseTime(Time),
        RoutineTrigger.Sunrise when weather != null => weather.Sunrise.AddMinutes(OffsetMinutes),
        RoutineTrigger.Sunset when weather != null => weather.Sunset.AddMinutes(OffsetMinutes),
        _ => null,
    };

    public string Summary()
    {
        string when = Trigger switch
        {
            RoutineTrigger.Sunrise => "Lever du soleil" + Offset(),
            RoutineTrigger.Sunset => "Coucher du soleil" + Offset(),
            _ => Condition == RoutineCondition.Always ? Time : $"{Time}–{Until}",
        };
        string cond = Condition switch
        {
            RoutineCondition.Rain => "s'il pleut · ",
            RoutineCondition.Clear => "si grand soleil · ",
            RoutineCondition.Cloudy => "si ciel gris · ",
            _ => "",
        };
        string what = Look == null ? "Tout eteindre" : Look.Describe();
        string ikea = IkeaSceneId == null ? "" : " · dans l'app IKEA";
        return $"{cond}{when} · {DaysLabel()} · {what}{ikea}";
    }

    string Offset() => OffsetMinutes == 0 ? "" : $" {(OffsetMinutes > 0 ? "+" : "−")}{Math.Abs(OffsetMinutes)} min";

    string DaysLabel()
    {
        var set = Days.ToHashSet();
        if (set.SetEquals(AllDays)) return "Tous les jours";
        if (set.SetEquals(WeekDays)) return "Lun–Ven";
        if (set.SetEquals(WeekEnd)) return "Week-end";
        var names = new Dictionary<DayOfWeek, string>
        {
            [DayOfWeek.Monday] = "Lun", [DayOfWeek.Tuesday] = "Mar", [DayOfWeek.Wednesday] = "Mer",
            [DayOfWeek.Thursday] = "Jeu", [DayOfWeek.Friday] = "Ven", [DayOfWeek.Saturday] = "Sam", [DayOfWeek.Sunday] = "Dim",
        };
        return string.Join(" ", AllDays.Where(set.Contains).Select(d => names[d]));
    }

    /// <summary>La journee type : reveil, journee, coucher du soleil, soiree, nuit, + pluie et grand soleil.</summary>
    public static List<Routine> DefaultDay() => new()
    {
        new() { Name = "Reveil", Time = "07:30", Days = WeekDays.ToList(), Look = new LightLook(true, 0, 0, 2700, 0.45) },
        new() { Name = "Journee", Time = "09:00", Look = new LightLook(true, 0, 0, 4000, 1.0) },
        new() { Name = "Coucher du soleil", Trigger = RoutineTrigger.Sunset, OffsetMinutes = -15, Look = new LightLook(true, 0, 0, 2700, 0.75) },
        new() { Name = "Soiree cosy", Time = "21:30", Look = new LightLook(true, 0, 0, 2202, 0.40) },
        new() { Name = "Nuit", Time = "23:45", Look = null },
        new() { Name = "Jour de pluie", Time = "08:00", Until = "18:00", Condition = RoutineCondition.Rain, Look = new LightLook(false, 32, 0.75, 2700, 0.85) },
        new() { Name = "Grand soleil", Time = "08:00", Until = "18:00", Condition = RoutineCondition.Clear, Look = new LightLook(true, 0, 0, 4000, 0.60) },
    };
}
