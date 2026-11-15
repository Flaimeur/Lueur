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

    /// <summary>Degrade de la tuile : la couleur du look, du plus clair au plus sombre.</summary>
    public (Rgb From, Rgb To) Gradient()
    {
        var c = Look.BaseRgb;
        var light = new Rgb((byte)Math.Min(255, c.R * 0.9 + 25), (byte)Math.Min(255, c.G * 0.9 + 25), (byte)Math.Min(255, c.B * 0.9 + 25));
        return (light, c.Scale(0.35 + Look.Level * 0.3));
    }
}
