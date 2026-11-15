using System;

namespace Lueur.Core;

/// <summary>Ce que l'utilisateur a choisi : une couleur (teinte/saturation) ou un blanc (Kelvin), plus une luminosite.</summary>
public sealed record LightLook(bool IsWhite, double Hue, double Saturation, int Kelvin, double Level)
{
    public static readonly LightLook Default = new(false, 265, 0.85, 2700, 0.8);
    /// <summary>Lumière de soirée : blanc très chaud, faible (commande --warm).</summary>
    public static readonly LightLook Warm = new(true, 30, 0.6, 2200, 0.3);

    /// <summary>Couleur a pleine intensite, sans la luminosite (pour l'UI).</summary>
    public Rgb BaseRgb => IsWhite ? ColorMath.FromKelvin(Kelvin) : ColorMath.FromHsv(Hue, Saturation, 1);

    /// <summary>Couleur finale envoyee aux LEDs du PC.</summary>
    public Rgb ToRgb() => BaseRgb.Scale(Level);

    public string Describe()
    {
        int pct = (int)Math.Round(Level * 100);
        if (IsWhite)
            return $"Blanc {Kelvin} K · {pct} %";
        return $"{HueName(Hue, Saturation)} · {pct} %";
    }

    static string HueName(double h, double s)
    {
        if (s < 0.15) return "Blanc";
        h = ((h % 360) + 360) % 360;
        return h switch
        {
            < 15 => "Rouge",
            < 40 => "Orange",
            < 65 => "Jaune",
            < 160 => "Vert",
            < 200 => "Cyan",
            < 250 => "Bleu",
            < 290 => "Violet",
            < 335 => "Rose",
            _ => "Rouge",
        };
    }
}

/// <summary>Quelles parties d'un look envoyer aux lampes (evite de tout renvoyer quand on bouge un seul curseur).</summary>
[Flags]
public enum LookParts
{
    None = 0,
    Power = 1,
    Color = 2,
    Level = 4,
    All = Power | Color | Level,
}
