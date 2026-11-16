using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace Lueur.Core;

/// <summary>
/// Pilote maison de l'eclairage du micro Logitech/Blue Yeti X (USB 046D:0AAF), en HID++ 2.0 direct, sans
/// OpenRGB. Le micro expose sur son interface HID 3 des collections vendeur (page 0xFF43) : 0x0701 pour les
/// messages HID++ courts (rapport 0x10, 7 octets), 0x0702 pour les longs (rapport 0x11, 20 octets). Ses LEDs
/// passent par la fonction ColorLedEffects (0x8070) : une zone par anneau, et dans chaque zone un effet
/// "couleur fixe" dont les trois premiers parametres sont R, G, B.
/// Toutes les ecritures passent par un seul fil : une couleur demandee pendant un envoi remplace la precedente.
/// </summary>
public sealed class YetiXLighting : IDisposable
{
    const ushort Vid = 0x046D, Pid = 0x0AAF;
    const ushort HidppPage = 0xFF43, ShortUsage = 0x0701, LongUsage = 0x0702;
    const byte DeviceIndex = 0xFF;          // peripherique USB direct
    const byte SoftwareId = 0x0B;           // marque nos requetes (OpenRGB/G HUB utilisent d'autres valeurs)
    const ushort ColorLedEffects = 0x8070;
    const ushort EffectFixedColor = 1;

    readonly object _lock = new();
    HidChannel? _short, _long;
    byte _featureIndex;
    readonly List<(byte Zone, byte FixedEffect)> _zones = new();
    readonly BlockingCollection<Rgb> _queue = new(new ConcurrentQueue<Rgb>());
    Thread? _worker;
    Timer? _keepAlive;
    Rgb? _wanted;

    public bool IsConnected { get { lock (_lock) return _long != null; } }
    public Rgb LastColor { get; private set; } = Rgb.Black;
    public string? LastError { get; private set; }
    /// <summary>Journal des echanges, pour les diagnostics (ligne de commande --yeti-probe).</summary>
    public Action<string>? Log { get; set; }

    /// <summary>
    /// Ouvre le micro et lit ses zones. Sans micro branche, renvoie false sans erreur bloquante :
    /// on retentera plus tard.
    /// </summary>
    public bool Connect()
    {
        lock (_lock)
        {
            if (_long != null) return true;
            try
            {
                var paths = FindCollections();
                if (!paths.TryGetValue(LongUsage, out var longPath))
                {
                    LastError = "Yeti X non trouvé.";
                    return false;
                }
                _long = new HidChannel(longPath, 20);
                if (paths.TryGetValue(ShortUsage, out var shortPath))
                {
                    try { _short = new HidChannel(shortPath, 7); }
                    catch (Exception) { _short = null; } // les erreurs courtes arrivent la ; pas indispensable
                }
                _long.Received += OnReport;
                if (_short != null) _short.Received += OnReport;

                Discover();
                LastError = null;
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                Log?.Invoke("Connexion : " + ex.Message);
                CloseChannels();
                return false;
            }
        }
    }

    void Discover()
    {
        // Root (index 0), fonction 0 getFeature(0x8070) -> index de la fonction dans la table du micro.
        var root = Call(0, 0, new byte[] { ColorLedEffects >> 8, ColorLedEffects & 0xFF });
        _featureIndex = root[0];
        if (_featureIndex == 0) throw new InvalidOperationException("Le micro ne propose pas ColorLedEffects (0x8070).");
        Log?.Invoke($"0x8070 a l'index {_featureIndex}");

        var info = Call(_featureIndex, 0, Array.Empty<byte>());
        int zoneCount = info[0];
        Log?.Invoke($"getInfo : {Hex(info)} -> {zoneCount} zone(s)");

        _zones.Clear();
        for (byte z = 0; z < zoneCount; z++)
        {
            var zi = Call(_featureIndex, 1, new[] { z });
            int effects = zi[3];
            Log?.Invoke($"zone {z} : {Hex(zi)} -> lieu {(zi[1] << 8) | zi[2]}, {effects} effet(s)");
            byte? fixedIdx = null;
            for (byte e = 0; e < effects; e++)
            {
                var ei = Call(_featureIndex, 2, new[] { z, e });
                int id = (ei[2] << 8) | ei[3];
                Log?.Invoke($"  effet {e} : id {id} ({Hex(ei)})");
                if (id == EffectFixedColor && fixedIdx == null) fixedIdx = e;
            }
            if (fixedIdx != null) _zones.Add((z, fixedIdx.Value));
        }
        if (_zones.Count == 0) throw new InvalidOperationException("Aucune zone du micro n'a d'effet couleur fixe.");

        // Prise de controle logicielle (fonction 8, versions 1 et plus) : le firmware ne repasse plus sur son
        // propre effet. Les anciennes versions repondent par une erreur, sans consequence.
        try
        {
            Call(_featureIndex, 8, new byte[] { 1, 0 });
            Log?.Invoke("controle logiciel pris");
        }
        catch (Exception ex) { Log?.Invoke("setSwControl refuse : " + ex.Message); }
    }

    /// <summary>Demande une couleur (noir = eteint). Rend la main tout de suite ; l'envoi se fait en fond.</summary>
    public void SetColor(Rgb color)
    {
        LastColor = color;
        if (_queue.IsAddingCompleted) return;
        EnsureWorker();
        try { _queue.Add(color); } catch (InvalidOperationException) { } // ferme entre-temps
    }

    void EnsureWorker()
    {
        lock (_lock)
        {
            if (_worker != null) return;
            _worker = new Thread(WorkerLoop) { IsBackground = true, Name = "Yeti X" };
            _worker.Start();
            // Filet de securite : si un autre logiciel (G HUB, OpenRGB) repasse le micro sur son effet, ou s'il
            // est rebranche, on lui renvoie la couleur toutes les 5 s. Une ecriture identique ne se voit pas.
            _keepAlive = new Timer(_ => { if (_queue.Count == 0) SetColor(LastColor); }, null,
                TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        }
    }

    void WorkerLoop()
    {
        foreach (var first in _queue.GetConsumingEnumerable())
        {
            var color = first;
            while (_queue.TryTake(out var newer)) color = newer; // seule la plus recente compte
            _wanted = color;
            Apply(color);
        }
    }

    void Apply(Rgb c)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            if (!Connect()) return;
            try
            {
                lock (_lock)
                {
                    foreach (var (zone, effect) in _zones)
                    {
                        // setZoneEffect : zone, effet, 10 parametres (R G B ...), persistance 0 = RAM seulement.
                        var args = new byte[13];
                        args[0] = zone;
                        args[1] = effect;
                        args[2] = c.R; args[3] = c.G; args[4] = c.B;
                        args[12] = 0;
                        Call(_featureIndex, 3, args);
                    }
                }
                LastError = null;
                return;
            }
            catch (Exception ex)
            {
                // Micro debranche, ou table changee : on rouvre et on retente une fois.
                LastError = ex.Message;
                Log?.Invoke("Envoi : " + ex.Message);
                lock (_lock) CloseChannels();
            }
        }
    }

    // ------------------------------------------------------------------ HID++ 2.0

    readonly BlockingCollection<byte[]> _replies = new(new ConcurrentQueue<byte[]>());

    void OnReport(byte[] report)
    {
        // Seulement nos reponses : meme numero de logiciel, ou erreur (fonction 0xFF) qui nous concerne.
        if (report.Length < 5 || report[1] != DeviceIndex) return;
        bool ours = report[2] == 0xFF ? (report[4] & 0x0F) == SoftwareId : (report[3] & 0x0F) == SoftwareId;
        if (ours) _replies.Add(report);
    }

    /// <summary>Envoie une requete longue et attend la reponse ; renvoie les 16 octets de parametres.</summary>
    byte[] Call(byte feature, byte function, byte[] args)
    {
        var ch = _long ?? throw new IOException("Yeti X ferme.");
        while (_replies.TryTake(out _)) { }

        var report = new byte[20];
        report[0] = 0x11;
        report[1] = DeviceIndex;
        report[2] = feature;
        report[3] = (byte)((function << 4) | SoftwareId);
        Array.Copy(args, 0, report, 4, Math.Min(args.Length, 16));
        ch.Write(report);

        var deadline = DateTime.UtcNow.AddMilliseconds(600);
        while (true)
        {
            var left = deadline - DateTime.UtcNow;
            if (left <= TimeSpan.Zero || !_replies.TryTake(out var r, left))
                throw new TimeoutException($"Le micro ne repond pas (fonction {feature}.{function}).");
            if (r[2] == 0xFF && r[3] == feature && r[4] == report[3])
                throw new IOException($"Erreur HID++ {r[5]} (fonction {feature}.{function}).");
            if (r[2] == feature && r[3] == report[3])
            {
                var payload = new byte[16];
                Array.Copy(r, 4, payload, 0, Math.Min(16, r.Length - 4));
                return payload;
            }
        }
    }

    static string Hex(byte[] b) => BitConverter.ToString(b, 0, Math.Min(b.Length, 8));

    void CloseChannels()
    {
        _long?.Dispose();
        _short?.Dispose();
        _long = _short = null;
    }

    /// <summary>Laisse partir la derniere couleur demandee (mode ligne de commande), puis ferme.</summary>
    public void Dispose()
    {
        _keepAlive?.Dispose();
        _queue.CompleteAdding();
        _worker?.Join(TimeSpan.FromSeconds(3));
        lock (_lock) CloseChannels();
    }

    // ------------------------------------------------------------------ Enumeration Windows

    static Dictionary<ushort, string> FindCollections()
    {
        var found = new Dictionary<ushort, string>();
        HidD_GetHidGuid(out var hidGuid);
        var set = SetupDiGetClassDevs(ref hidGuid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (set == new IntPtr(-1)) return found;
        try
        {
            var ifData = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
            for (uint i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref hidGuid, i, ref ifData); i++)
            {
                SetupDiGetDeviceInterfaceDetail(set, ref ifData, IntPtr.Zero, 0, out int size, IntPtr.Zero);
                if (size <= 0) continue;
                var buf = Marshal.AllocHGlobal(size);
                try
                {
                    Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6); // cbSize de SP_DEVICE_INTERFACE_DETAIL_DATA_W
                    if (!SetupDiGetDeviceInterfaceDetail(set, ref ifData, buf, size, out _, IntPtr.Zero)) continue;
                    var path = Marshal.PtrToStringUni(buf + 4)!;
                    if (!path.Contains("vid_046d&pid_0aaf", StringComparison.OrdinalIgnoreCase)) continue;
                    var usage = ReadUsage(path);
                    if (usage is { } u && u.Page == HidppPage) found[u.Usage] = path;
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return found;
    }

    static (ushort Page, ushort Usage)? ReadUsage(string path)
    {
        using var h = CreateFile(path, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (h.IsInvalid) return null;
        if (!HidD_GetPreparsedData(h, out var pre)) return null;
        try
        {
            if (HidP_GetCaps(pre, out var caps) != HIDP_STATUS_SUCCESS) return null;
            return (caps.UsagePage, caps.Usage);
        }
        finally { HidD_FreePreparsedData(pre); }
    }

    /// <summary>Une collection HID ouverte en lecture/ecriture partagees, avec un fil de lecture.</summary>
    sealed class HidChannel : IDisposable
    {
        readonly FileStream _stream;
        readonly int _length;
        readonly CancellationTokenSource _cts = new();
        public event Action<byte[]>? Received;

        public HidChannel(string path, int reportLength)
        {
            _length = reportLength;
            var h = CreateFile(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, IntPtr.Zero);
            if (h.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "Ouverture du Yeti X impossible");
            _stream = new FileStream(h, FileAccess.ReadWrite, 0, isAsync: true);
            _ = Task.Run(ReadLoop);
        }

        async Task ReadLoop()
        {
            var buf = new byte[_length];
            while (!_cts.IsCancellationRequested)
            {
                int n;
                try { n = await _stream.ReadAsync(buf, _cts.Token); }
                catch (Exception) { return; }
                if (n <= 0) return;
                try { Received?.Invoke(buf[..n]); } catch (Exception) { }
            }
        }

        public void Write(byte[] report)
        {
            var buf = new byte[_length];
            Array.Copy(report, buf, Math.Min(report.Length, _length));
            _stream.Write(buf, 0, buf.Length);
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _stream.Dispose(); } catch (Exception) { }
        }
    }

    // ------------------------------------------------------------------ Win32

    const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
    const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3, FILE_FLAG_OVERLAPPED = 0x40000000;
    const int DIGCF_PRESENT = 2, DIGCF_DEVICEINTERFACE = 0x10;
    const int HIDP_STATUS_SUCCESS = 0x00110000;

    [StructLayout(LayoutKind.Sequential)]
    struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid InterfaceClassGuid; public int Flags; public IntPtr Reserved; }

    [StructLayout(LayoutKind.Sequential)]
    struct HIDP_CAPS
    {
        public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices,
            NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices,
            NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
    }

    [DllImport("hid.dll")] static extern void HidD_GetHidGuid(out Guid guid);
    [DllImport("hid.dll")] static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr data);
    [DllImport("hid.dll")] static extern bool HidD_FreePreparsedData(IntPtr data);
    [DllImport("hid.dll")] static extern int HidP_GetCaps(IntPtr data, out HIDP_CAPS caps);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SetupDiGetClassDevs(ref Guid guid, IntPtr enumerator, IntPtr parent, int flags);
    [DllImport("setupapi.dll", SetLastError = true)]
    static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr info, ref Guid guid, uint index, ref SP_DEVICE_INTERFACE_DATA data);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, int size, out int required, IntPtr info);
    [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sec, uint disposition, uint flags, IntPtr template);
}
