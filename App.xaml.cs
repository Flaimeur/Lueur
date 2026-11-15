using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Lueur.Core;
using Forms = System.Windows.Forms;

namespace Lueur;

/// <summary>
/// Demarrage :
///   Lueur.exe              -> fenetre + icone dans la zone de notification
///   Lueur.exe --minimized  -> seulement l'icone (demarrage avec Windows)
///   Lueur.exe --on | --off | --toggle | --warm | --scene "Nom" | --color #RRGGBB | --level 0-100
///                          -> action, puis quitte (raccourcis, .bat, Moonlight, Lumen...) ; si Lueur est deja
///                             ouvert, c'est lui qui l'execute (transmis par un tube nomme, dans l'ordre)
///   Lueur.exe --screen | --adaptive auto|off... | --show -> ouvre Lueur (reduit) s'il ne tourne pas
/// Une mise a jour telechargee depuis GitHub est installee au lancement normal suivant.
/// </summary>
public partial class App : Application
{
    const string MutexName = "Lueur.Lights.SingleInstance";

    Mutex? _mutex;
    readonly CancellationTokenSource _cts = new();
    LightController? _controller;
    RoutineEngine? _routines;
    AdaptiveEngine? _adaptive;
    MainWindow? _window;
    Forms.NotifyIcon? _tray;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var rawArgs = e.Args;
        var args = rawArgs.Select(a => a.ToLowerInvariant()).ToArray();
        var commands = LueurCommand.Parse(rawArgs);

        // Lancement normal (pas une simple commande) : installe la mise a jour telechargee s'il y en a une.
        if (commands.Count == 0 && Updater.ApplyPendingUpdate(rawArgs))
        {
            Shutdown();
            return;
        }

        _mutex = new Mutex(true, MutexName, out bool firstInstance);
        if (!firstInstance && args.Contains("--after-update"))
        {
            // Relance apres mise a jour : l'ancienne version est en train de se fermer.
            try { firstInstance = _mutex.WaitOne(TimeSpan.FromSeconds(20)); }
            catch (AbandonedMutexException) { firstInstance = true; }
        }
        if (!firstInstance)
        {
            // Deja lance : l'instance ouverte execute les commandes, dans l'ordre (et arrete donc son mode
            // ecran / adaptatif proprement), ou se montre si on a juste relance l'appli.
            CommandChannel.Send(commands.Count == 0 ? new[] { "--show" } : rawArgs);
            Shutdown();
            return;
        }

        if (commands.Count > 0 && !commands.Any(c => c.NeedsRunningApp))
        {
            // Lueur n'est pas ouvert : on fait l'action sans interface puis on quitte.
            _mutex.ReleaseMutex();
            _mutex.Dispose();
            _mutex = null;
            Shutdown(await RunHeadlessAsync(commands));
            return;
        }

        CommandChannel.Listen(received =>
        {
            foreach (var c in LueurCommand.Parse(received))
                Dispatcher.BeginInvoke(() => _window?.RunCommand(c));
        }, _cts.Token);
        var command = commands.FirstOrDefault()?.Verb;

        _controller = new LightController(AppSettings.Load());
        _routines = new RoutineEngine(_controller);
        _adaptive = new AdaptiveEngine(_controller, _routines.Weather);
        _window = new MainWindow(_controller, _routines, _adaptive);
        CreateTray();

        if (!args.Contains("--minimized") && command == null) _window.Show();
        await _controller.InitAsync();
        _routines.Start();
        if (_controller.Settings.AdaptiveOn) _adaptive.Start();
        foreach (var c in commands) _window.RunCommand(c);
        await _window.PublishAllAsync();
        if (!_controller.IsPaired)
        {
            _window.BringToFront();
            _window.ShowPairing();
        }
    }

    /// <summary>Execute les commandes sans ouvrir Lueur (il n'etait pas lance), dans l'ordre.</summary>
    static async Task<int> RunHeadlessAsync(System.Collections.Generic.List<LueurCommand> commands)
    {
        using var controller = new LightController(AppSettings.Load());
        try
        {
            await controller.InitAsync(listenEvents: false);
            var look = controller.Settings.Look;
            foreach (var c in commands)
            {
                switch (c.Verb)
                {
                    case "warm": await controller.ApplyAsync(LightLook.Warm, LookParts.All); break;
                    case "off": await controller.AllOffAsync(); break;
                    case "on": await controller.AllOnAsync(); break;
                    case "toggle": await controller.ToggleAllAsync(); break;
                    case "scene":
                        var scene = controller.Settings.Scenes.FirstOrDefault(s => string.Equals(s.Name, c.Value?.Trim(), StringComparison.OrdinalIgnoreCase));
                        if (scene != null) await controller.ApplyAsync(scene.Look, LookParts.All);
                        break;
                    case "color":
                        if (LueurCommand.TryParseColor(c.Value, out var rgb))
                        {
                            var (h, s, v) = ColorMath.ToHsv(rgb);
                            await controller.ApplyAsync(new LightLook(false, h, s, look.Kelvin, Math.Max(0.05, v)), LookParts.All);
                        }
                        break;
                    case "level":
                        if (int.TryParse(c.Value, out var pct))
                            await controller.ApplyAsync(controller.Settings.Look with { Level = Math.Clamp(pct, 1, 100) / 100.0 }, LookParts.All);
                        break;
                }
            }
            controller.Settings.Save();
            return 0;
        }
        catch (Exception)
        {
            return 1;
        }
    }

    void CreateTray()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Ouvrir Lueur", null, (_, _) => _window?.BringToFront());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Tout allumer", null, async (_, _) => await _controller!.AllOnAsync());
        menu.Items.Add("Tout eteindre", null, async (_, _) => await _controller!.AllOffAsync());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Quitter", null, (_, _) => Quit());

        _tray = new Forms.NotifyIcon
        {
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!),
            Text = "Lueur",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _tray.MouseClick += (_, ev) =>
        {
            if (ev.Button == Forms.MouseButtons.Left) _window?.BringToFront();
        };
    }

    public void Quit()
    {
        _cts.Cancel();
        if (_tray != null) _tray.Visible = false;
        _tray?.Dispose();
        _window?.CloseForReal();
        _adaptive?.Dispose();
        _routines?.Dispose();
        _controller?.Dispose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
