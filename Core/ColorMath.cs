using System;

namespace Lueur.Core;

/// <summary>Couleur 8 bits par canal, celle qu'on envoie a OpenRGB.</summary>
public readonly record struct Rgb(byte R, byte G, byte B)
{
    public static readonly Rgb Black = new(0, 0, 0);

    /// <summary>Multiplie la couleur par un facteur 0..1 (luminosite).</summary>
    public Rgb Scale(double factor)
    {
        factor = Math.Clamp(factor, 0, 1);
        return new Rgb((byte)Math.Round(R * factor), (byte)Math.Round(G * factor), (byte)Math.Round(B * factor));
    }

    public System.Windows.Media.Color ToMedia() => System.Windows.Media.Color.FromRgb(R, G, B);
}

public static class ColorMath
{
    /// <summary>HSV -> RGB. h en degres (0..360), s et v entre 0 et 1.</summary>
    public static Rgb FromHsv(double h, double s, double v)
    {
        h = ((h % 360) + 360) % 360;
        s = Math.Clamp(s, 0, 1);
        v = Math.Clamp(v, 0, 1);

        double c = v * s;
        double x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        double m = v - c;
        (double r, double g, double b) = (int)(h / 60) switch
        {
            0 => (c, x, 0d),
            1 => (x, c, 0d),
            2 => (0d, c, x),
            3 => (0d, x, c),
            4 => (x, 0d, c),
            _ => (c, 0d, x),
        };
        return new Rgb(ToByte(r + m), ToByte(g + m), ToByte(b + m));
    }

    /// <summary>
    /// Temperature de blanc (Kelvin) -> RGB approximatif (algorithme de Tanner Helland).
    /// Sert a reproduire sur le PC le "blanc chaud / blanc froid" des ampoules IKEA.
    /// </summary>
    public static Rgb FromKelvin(double kelvin)
    {
        double t = Math.Clamp(kelvin, 1000, 40000) / 100;
        double r, g, b;

        if (t <= 66)
        {
            r = 255;
            g = 99.4708025861 * Math.Log(t) - 161.1195681661;
        }
        else
        {
            r = 329.698727446 * Math.Pow(t - 60, -0.1332047592);
            g = 288.1221695283 * Math.Pow(t - 60, -0.0755148492);
        }

        if (t >= 66) b = 255;
        else if (t <= 19) b = 0;
        else b = 138.5177312231 * Math.Log(t - 10) - 305.0447927307;

        return new Rgb(Clamp255(r), Clamp255(g), Clamp255(b));
    }

    /// <summary>RGB -> (teinte 0..360, saturation 0..1, valeur 0..1).</summary>
    public static (double H, double S, double V) ToHsv(Rgb c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        double h = d == 0 ? 0
            : max == r ? 60 * (((g - b) / d) % 6)
            : max == g ? 60 * ((b - r) / d + 2)
            : 60 * ((r - g) / d + 4);
        return ((h + 360) % 360, max == 0 ? 0 : d / max, max);
    }

    static byte ToByte(double v) => (byte)Math.Round(Math.Clamp(v, 0, 1) * 255);
    static byte Clamp255(double v) => (byte)Math.Round(Math.Clamp(v, 0, 255));
}
