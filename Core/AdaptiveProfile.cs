using System;
using System.Collections.Generic;
using System.Linq;

namespace Lueur.Core;

/// <summary>Un point de la journee : a cette heure, la lumiere ressemble a ca.</summary>
public sealed record AdaptiveKey(TimeSpan At, LightLook Look);

/// <summary>
/// Profil adaptatif en couleurs : une suite de points dans la journee. Entre deux points la lumiere
/// glisse en continu (teinte par le chemin le plus court, luminosite lineaire), et la fin de la
/// journee rejoint le premier point du lendemain.
/// </summary>
public sealed class AdaptiveProfile
{
    public required string Key { get; init; }
    public required string Name { get; init; }
    public required string Mood { get; init; }
    public required AdaptiveKey[] Keys { get; init; }

    public LightLook At(TimeSpan time)
    {
        var keys = Keys;
        int next = Array.FindIndex(keys, k => k.At > time);
        var a = next <= 0 ? keys[^1] : keys[next - 1];
        var b = next < 0 ? keys[0] : keys[next];

        double span = (b.At - a.At).TotalMinutes;
        double pos = (time - a.At).TotalMinutes;
        if (span <= 0) span += 24 * 60;
        if (pos < 0) pos += 24 * 60;
        double f = Smooth(Math.Clamp(pos / span, 0, 1));
        return Blend(a.Look, b.Look, f);
    }

    /// <summary>Ease in-out : les transitions demarrent et finissent en douceur.</summary>
    static double Smooth(double t) => t * t * (3 - 2 * t);

    static LightLook Blend(LightLook a, LightLook b, double f)
    {
        double level = a.Level + (b.Level - a.Level) * f;
        if (a.IsWhite && b.IsWhite)
            return new LightLook(true, 0, 0, (int)Math.Round(a.Kelvin + (b.Kelvin - a.Kelvin) * f), level);

        // Un blanc est converti en sa teinte/saturation equivalente pour pouvoir glisser vers une couleur.
        var (ha, sa) = HueSat(a);
        var (hb, sb) = HueSat(b);
        double dh = ((hb - ha + 540) % 360) - 180;
        return new LightLook(false, (ha + dh * f + 360) % 360, sa + (sb - sa) * f, a.IsWhite ? a.Kelvin : b.Kelvin, level);
    }

    static (double H, double S) HueSat(LightLook l)
    {
        if (!l.IsWhite) return (l.Hue, l.Saturation);
        var (h, s, _) = ColorMath.ToHsv(ColorMath.FromKelvin(l.Kelvin));
        return (h, s);
    }

    static LightLook W(int kelvin, double level) => new(true, 0, 0, kelvin, level);
    static LightLook C(double hue, double sat, double level) => new(false, hue, sat, 2700, level);
    static AdaptiveKey K(int h, int m, LightLook look) => new(new TimeSpan(h, m, 0), look);

    public static readonly AdaptiveProfile Sunny = new()
    {
        Key = "sun",
        Name = "Soleil",
        Mood = "Aube peche, plein jour lumineux, heure doree, coucher corail",
        Keys = new[]
        {
            K(0, 0, C(8, 0.9, 0.05)),
            K(5, 30, C(8, 0.9, 0.05)),
            K(6, 30, C(22, 0.55, 0.30)),
            K(8, 30, W(4000, 1.0)),
            K(13, 0, W(4000, 0.9)),
            K(17, 0, C(36, 0.55, 0.85)),
            K(19, 30, C(14, 0.75, 0.60)),
            K(21, 30, W(2202, 0.40)),
            K(23, 0, C(8, 0.9, 0.08)),
        },
    };

    public static readonly AdaptiveProfile Rainy = new()
    {
        Key = "rain",
        Name = "Pluie",
        Mood = "Cocon ambre et dore pour compenser le ciel gris, coin du feu le soir",
        Keys = new[]
        {
            K(0, 0, C(6, 0.9, 0.05)),
            K(5, 30, C(6, 0.9, 0.05)),
            K(7, 0, C(30, 0.65, 0.45)),
            K(9, 0, W(2700, 0.95)),
            K(12, 0, C(40, 0.45, 1.0)),
            K(16, 0, C(28, 0.70, 0.85)),
            K(19, 0, C(18, 0.80, 0.55)),
            K(21, 30, C(12, 0.85, 0.30)),
            K(23, 0, C(5, 0.9, 0.06)),
        },
    };

    public static readonly AdaptiveProfile Cloudy = new()
    {
        Key = "cloud",
        Name = "Gris",
        Mood = "Blanc net le matin, touche de soleil l'apres-midi, soiree tamisee",
        Keys = new[]
        {
            K(0, 0, C(8, 0.9, 0.05)),
            K(5, 30, C(8, 0.9, 0.05)),
            K(7, 0, W(3000, 0.50)),
            K(9, 0, W(4000, 1.0)),
            K(14, 0, C(45, 0.35, 0.95)),
            K(18, 0, C(30, 0.55, 0.75)),
            K(21, 0, W(2202, 0.40)),
            K(23, 0, C(8, 0.9, 0.08)),
        },
    };

    public static readonly AdaptiveProfile[] All = { Sunny, Rainy, Cloudy };

    /// <summary>Profil correspondant a la meteo actuelle (neige et inconnu -> gris).</summary>
    public static AdaptiveProfile ForSky(Sky sky) => sky switch
    {
        Sky.Clear => Sunny,
        Sky.Rain => Rainy,
        _ => Cloudy,
    };

    public static AdaptiveProfile? ByKey(string key) => All.FirstOrDefault(p => p.Key == key);
}
