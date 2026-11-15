using System;
using System.Collections.Generic;

namespace Lueur.Core;

/// <summary>Une ambiance de Lueur : un nom, un sous-titre et un look. La tuile prend la couleur du look.</summary>
public sealed record SceneDef(string Name, string Sub, LightLook Look)
{
    /// <summary>Ambiances proposees au premier lancement (chacun peut ensuite les modifier ou en ajouter).</summary>
    public static List<SceneDef> Defaults() => new()
    {
        new("Gaming", "Violet neon", new LightLook(false, 268, 1.0, 2700, 0.85)),
        new("Cosy", "Blanc chaud tamise", new LightLook(true, 30, 0.6, 2202, 0.40)),
        new("Focus", "Blanc froid 100 %", new LightLook(true, 210, 0.1, 4000, 1.00)),
        new("Cinema", "Ambre tres doux", new LightLook(false, 22, 0.95, 2700, 0.15)),
        new("Ocean", "Bleu profond", new LightLook(false, 198, 0.9, 2700, 0.70)),
        new("Nuit", "Rouge veilleuse 5 %", new LightLook(false, 4, 1.0, 2700, 0.05)),
    };

    /// <summary>
    /// Degrade de la tuile : la couleur du look, assombrie pour que le texte blanc reste lisible meme sur
    /// un blanc froid (le haut de la tuile ne depasse jamais ~70 % de luminosite).
    /// </summary>
    public (Rgb From, Rgb To) Gradient()
    {
        var c = Look.BaseRgb;
        double peak = Math.Max(c.R, Math.Max(c.G, c.B)) / 255.0;
        double luminance = (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;
        double top = luminance > 0.6 ? 0.55 : 0.8; // couleurs tres claires (blancs) : plus sombres
        return (c.Scale(top / Math.Max(peak, 0.01) * peak), c.Scale(0.25 + Look.Level * 0.2));
    }
}
