using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OpenRGB.NET;

namespace Lueur.Core;

/// <summary>Un peripherique RGB du PC vu par OpenRGB (RAM, carte mere, ventilos, souris...).</summary>
public sealed record PcDevice(int Index, string Name, string Kind, int LedCount);

/// <summary>
/// Pilote toutes les LEDs du PC via le serveur SDK d'OpenRGB (127.0.0.1:6742).
/// Si OpenRGB ne tourne pas, on le lance en mode serveur, minimise.
/// </summary>
public sealed class OpenRgbService : IDisposable
{
    readonly object _lock = new();
    OpenRgbClient? _client;
    Device[] _devices = Array.Empty<Device>();

    public HashSet<string> Excluded { get; set; } = new();
    public IReadOnlyList<PcDevice> Devices { get; private set; } = Array.Empty<PcDevice>();
    public bool IsConnected { get { lock (_lock) return _client?.Connected == true; } }
    public Rgb LastColor { get; private set; } = Rgb.Black;
    public bool IsLit => LastColor != Rgb.Black;
    public string? LastError { get; private set; }

    public async Task<bool> ConnectAsync(string? exePath, bool launchIfNeeded, CancellationToken ct)
    {
        if (await Task.Run(TryConnect, ct)) return true;
        if (!launchIfNeeded) return false;

        var exe = FindExe(exePath);
        if (exe == null)
        {
            LastError = "OpenRGB introuvable. Installe-le (winget install CalcProgrammer1.OpenRGB).";
            return false;
        }

        if (HasOpenRgbService)
        {
            // Le service Windows "OpenRGB" (installe avec OpenRGB) le demarre avec les droits systeme des
            // l'allumage. En lancer un second ferait se battre deux OpenRGB pour la RAM et la carte mere
            // (et le second afficherait l'avertissement SMBus) : on attend simplement le service.
            LastError = "Le service OpenRGB ne repond pas (services.msc > OpenRGB > Demarrer).";
        }
        else if (Process.GetProcessesByName("OpenRGB").Length == 0 && !StartViaAdminTask())
        {
            Process.Start(new ProcessStartInfo(exe, "--server --startminimized")
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(exe)!,
            });
        }
        else
        {
            LastError = "OpenRGB tourne mais son serveur SDK est coupe (onglet SDK Server > Start Server).";
        }

        // La detection des peripheriques (SMBus pour la RAM) peut prendre quelques secondes.
        for (int i = 0; i < 20 && !ct.IsCancellationRequested; i++)
        {
            await Task.Delay(1000, ct);
            if (await Task.Run(TryConnect, ct)) return true;
        }
        return false;
    }

    static bool HasOpenRgbService
    {
        get
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\OpenRGB");
            return key != null;
        }
    }

    /// <summary>
    /// Lance OpenRGB par la tache planifiee "Lueur - OpenRGB" (scripts\openrgb-admin.ps1) : elle le demarre
    /// en administrateur sans demande d'autorisation, condition pour piloter la RAM. Lance sans ces droits,
    /// OpenRGB affiche l'avertissement SMBus/PawnIO et ne voit pas les barrettes.
    /// </summary>
    static bool StartViaAdminTask()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("schtasks.exe", "/run /tn \"Lueur - OpenRGB\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            p!.WaitForExit(5000);
            return p.ExitCode == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Demande a OpenRGB de redetecter tous ses peripheriques (paquet SDK 140), attend que la liste soit
    /// stable, puis se reconnecte. Necessaire quand un autre logiciel (G HUB) a lache un appareil : OpenRGB
    /// garde sinon une connexion morte vers lui et ses ecritures n'ont plus d'effet (cas du Yeti X).
    /// La detection complete prend une vingtaine de secondes.
    /// </summary>
    public async Task<bool> RescanAsync(CancellationToken ct)
    {
        try { await Task.Run(() => SendPacket(140), ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LastError = ex.Message;
            return false;
        }

        uint last = uint.MaxValue;
        int stable = 0;
        var deadline = DateTime.UtcNow.AddSeconds(45);
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            await Task.Delay(2500, ct);
            uint count;
            try { count = await Task.Run(() => SendPacket(0), ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { continue; }
            stable = count > 0 && count == last ? stable + 1 : 0;
            last = count;
            if (stable >= 3) break; // meme nombre d'appareils 3 fois de suite (~7 s) : detection terminee
        }
        return await Task.Run(TryConnect, ct);
    }

    /// <summary>Envoie un paquet SDK sans donnees. Pour REQUEST_CONTROLLER_COUNT (0), renvoie le nombre d'appareils.</summary>
    static uint SendPacket(uint packetId)
    {
        using var tcp = new System.Net.Sockets.TcpClient();
        tcp.ReceiveTimeout = tcp.SendTimeout = 2000;
        tcp.Connect("127.0.0.1", 6742);
        var stream = tcp.GetStream();
        var header = new byte[16];
        "ORGB"u8.CopyTo(header);
        BitConverter.GetBytes(packetId).CopyTo(header, 8);
        stream.Write(header);
        if (packetId != 0) return 0;

        var reply = new byte[20];
        for (int read = 0; read < reply.Length;)
        {
            int n = stream.Read(reply, read, reply.Length - read);
            if (n == 0) throw new IOException("OpenRGB a ferme la connexion.");
            read += n;
        }
        return BitConverter.ToUInt32(reply, 16);
    }

    bool TryConnect()
    {
        lock (_lock)
        {
            try
            {
                CloseClient();
                _client = new OpenRgbClient(name: "Lueur", autoConnect: true, timeoutMs: 1000);
                _devices = _client.GetAllControllerData();
                if (SizeEmptyArgbZones()) _devices = _client.GetAllControllerData();
                Devices = _devices
                    .Select((d, i) => new PcDevice(i, d.Name, d.Type.ToString(), d.Colors.Length))
                    .ToList();
                // Mode "Direct"/"Custom" : sinon l'effet materiel de la carte (arc-en-ciel...) reprend la main.
                for (int i = 0; i < _devices.Length; i++)
                {
                    try { _client.SetCustomMode(i); } catch (Exception) { }
                }
                StartKeepAlive();
                LastError = null;
                return true;
            }
            catch (Exception ex)
            {
                CloseClient();
                LastError = ex.Message;
                return false;
            }
        }
    }

    /// <summary>
    /// Ferme la connexion une seule fois : OpenRGB.NET leve une exception si on dispose deux fois le meme
    /// client (cas d'une reconnexion qui echoue), ce qui faisait planter Lueur.
    /// </summary>
    void CloseClient()
    {
        var old = _client;
        _client = null;
        try { old?.Dispose(); }
        catch (ObjectDisposedException) { }
    }

    /// <summary>
    /// Les prises ARGB de la carte mere (JRAINBOW chez MSI) arrivent avec 0 LED : OpenRGB ne peut pas
    /// savoir ce qui est branche dessus, donc il n'envoie rien aux ventilateurs. On leur donne une taille
    /// large (une chaine de ventilos depasse rarement 120 LEDs ; les LEDs en trop sont ignorees).
    /// </summary>
    bool SizeEmptyArgbZones()
    {
        const int argbLeds = 120;
        bool resized = false;
        for (int d = 0; d < _devices.Length; d++)
        {
            var zones = _devices[d].Zones;
            for (int z = 0; z < zones.Length; z++)
            {
                if (zones[z].LedCount != 0 || zones[z].LedsMax == 0) continue;
                try
                {
                    _client!.ResizeZone(d, z, (int)Math.Min(zones[z].LedsMax, argbLeds));
                    resized = true;
                }
                catch (Exception ex) { LastError = ex.Message; }
            }
        }
        return resized;
    }

    System.Threading.Timer? _keepAlive;

    /// <summary>
    /// Les peripheriques Logitech (Yeti X) repassent sur leur eclairage interne s'ils ne recoivent plus de
    /// couleur pendant quelques secondes : on leur renvoie la couleur courante toutes les 2 s.
    /// Les autres appareils gardent leur couleur et ne sont pas concernes.
    /// </summary>
    void StartKeepAlive()
    {
        _keepAlive ??= new System.Threading.Timer(_ =>
        {
            if (!Monitor.TryEnter(_lock)) return;
            try
            {
                if (_client?.Connected != true) return;
                for (int i = 0; i < _devices.Length; i++)
                {
                    if (_devices[i].Vendor != "Logitech" || Excluded.Contains(_devices[i].Name)) continue;
                    // Le micro peut repasser seul sur son eclairage interne : les couleurs envoyees en mode
                    // Direct sont alors ignorees. On remet le mode Direct avant chaque renvoi.
                    try { _client.SetCustomMode(i); } catch (Exception) { }
                    Push(i, LastColor);
                }
            }
            finally { Monitor.Exit(_lock); }
        }, null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
    }

    /// <summary>Met tous les peripheriques non exclus a la meme couleur (noir = eteint).</summary>
    public void SetAll(Rgb color)
    {
        LastColor = color;
        lock (_lock)
        {
            if (_client?.Connected != true) return;
            for (int i = 0; i < _devices.Length; i++)
                if (!Excluded.Contains(_devices[i].Name))
                    Push(i, color);
        }
    }

    public void SetDevice(string name, Rgb color)
    {
        lock (_lock)
        {
            if (_client?.Connected != true) return;
            for (int i = 0; i < _devices.Length; i++)
                if (_devices[i].Name == name)
                    Push(i, color);
        }
    }

    void Push(int index, Rgb c)
    {
        try
        {
            var colors = new Color[_devices[index].Colors.Length];
            Array.Fill(colors, new Color(c.R, c.G, c.B));
            _client!.UpdateLeds(index, colors);
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
    }

    static string? FindExe(string? configured)
    {
        var candidates = new List<string?>
        {
            configured,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "OpenRGB", "OpenRGB.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "OpenRGB", "OpenRGB.exe"),
        };
        // Installation winget "portable" : %LOCALAPPDATA%\Microsoft\WinGet\Packages\CalcProgrammer1.OpenRGB_*\...
        var wingetRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Packages");
        if (Directory.Exists(wingetRoot))
        {
            foreach (var dir in Directory.GetDirectories(wingetRoot, "CalcProgrammer1.OpenRGB*"))
                candidates.AddRange(Directory.GetFiles(dir, "OpenRGB.exe", SearchOption.AllDirectories));
        }
        return candidates.FirstOrDefault(p => !string.IsNullOrEmpty(p) && File.Exists(p));
    }

    public void Dispose()
    {
        _keepAlive?.Dispose();
        lock (_lock) CloseClient();
    }
}
