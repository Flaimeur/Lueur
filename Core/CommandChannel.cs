using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Lueur.Core;

/// <summary>
/// Une commande de pilotage, venue de la ligne de commande, de Lumen, de Moonlight, d'un raccourci...
///   --on | --off | --toggle | --warm | --screen | --show
///   --scene "Nom"            ambiance enregistree dans Lueur
///   --color #RRGGBB          couleur (chambre + PC)
///   --level 0-100            luminosite
///   --adaptive on|off|auto|sun|rain|cloud
/// </summary>
public sealed record LueurCommand(string Verb, string? Value)
{
    public static readonly string[] Simple = { "on", "off", "toggle", "warm", "screen", "show" };
    public static readonly string[] WithValue = { "scene", "color", "level", "adaptive" };

    /// <summary>Commandes qui ont besoin de Lueur ouvert (pas faisables "sans interface").</summary>
    public bool NeedsRunningApp => Verb is "screen" or "show" or "adaptive";

    /// <summary>Lit les commandes dans les arguments, dans l'ordre. Les autres arguments (--minimized...) sont ignores.</summary>
    public static List<LueurCommand> Parse(IReadOnlyList<string> args)
    {
        var list = new List<LueurCommand>();
        for (int i = 0; i < args.Count; i++)
        {
            var a = args[i].Trim();
            if (!a.StartsWith("--")) continue;
            var verb = a[2..].ToLowerInvariant();
            if (Simple.Contains(verb)) list.Add(new(verb, null));
            else if (WithValue.Contains(verb) && i + 1 < args.Count) list.Add(new(verb, args[++i]));
        }
        return list;
    }

    public static bool TryParseColor(string? s, out Rgb color)
    {
        color = Rgb.Black;
        s = s?.Trim().TrimStart('#');
        if (s is not { Length: 6 } || !int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)) return false;
        color = new Rgb((byte)(v >> 16), (byte)(v >> 8), (byte)v);
        return true;
    }
}

/// <summary>
/// Transmet les arguments d'une deuxieme instance de Lueur a l'instance ouverte, par un tube nomme.
/// Contrairement aux evenements Windows, l'ordre est garanti et les commandes peuvent porter une valeur.
/// </summary>
public static class CommandChannel
{
    const string PipeName = "Lueur.Lights.Commands";

    /// <summary>Envoie les arguments a l'instance ouverte. Renvoie false si personne n'ecoute.</summary>
    public static bool Send(IReadOnlyList<string> args)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            pipe.Connect(2000);
            using var w = new StreamWriter(pipe, new UTF8Encoding(false));
            w.Write(string.Join("\n", args));
            w.Flush();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Ecoute en continu ; chaque message recu (liste d'arguments) est passe a onArgs, dans l'ordre.</summary>
    public static void Listen(Action<string[]> onArgs, CancellationToken ct)
    {
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    // Tube reserve a l'utilisateur courant (par defaut) : une autre session Windows ne peut pas piloter Lueur.
                    using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync(ct);
                    using var r = new StreamReader(server, Encoding.UTF8);
                    var text = await r.ReadToEndAsync(ct);
                    onArgs(text.Split('\n', StringSplitOptions.RemoveEmptyEntries));
                }
                catch (OperationCanceledException) { return; }
                catch (Exception) { await Task.Delay(200, CancellationToken.None); }
            }
        }, ct);
    }
}
