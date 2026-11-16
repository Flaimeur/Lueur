using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace Lueur.Core;

/// <summary>
/// Le chef d'orchestre : applique un look a la chambre (DIRIGERA) et au PC (OpenRGB) en meme temps,
/// et fait suivre le PC quand la chambre change ailleurs (Siri, Alexa, app IKEA, interrupteur).
/// </summary>
public sealed class LightController : IDisposable
{
    readonly SynchronizationContext? _ui = SynchronizationContext.Current;
    readonly CancellationTokenSource _cts = new();
    /// <summary>Pendant ce delai apres une commande de l'appli, on ignore l'echo websocket de nos propres changements.</summary>
    DateTime _ignoreEventsUntil;

    public AppSettings Settings { get; }
    public DirigeraClient? Ikea { get; private set; }
    public OpenRgbService Pc { get; } = new();
    public ObservableCollection<IkeaLight> Lights { get; } = new();

    public bool IkeaOnline { get; private set; }
    public bool IkeaLive { get; private set; }

    /// <summary>Message court a afficher dans l'UI (erreurs, confirmations).</summary>
    public event Action<string>? Notice;
    /// <summary>L'etat des connexions a change.</summary>
    public event Action? ConnectionChanged;
    /// <summary>La chambre a change hors de l'appli : look de reference, ou null si tout est eteint.</summary>
    public event Action<LightLook?>? RoomChangedExternally;

    public LightController(AppSettings settings)
    {
        Settings = settings;
        Pc.Excluded = new HashSet<string>(settings.ExcludedPcDevices);
    }

    public bool IsPaired => !string.IsNullOrEmpty(Settings.DirigeraHost) && Settings.GetToken() != null;
    public bool AnyRoomLightOn => Lights.Any(l => l.IsOn && l.IsReachable);

    /// <summary>Connexion aux deux cotes en parallele. listenEvents=false pour le mode ligne de commande.</summary>
    public async Task InitAsync(bool listenEvents = true)
    {
        if (listenEvents && CloseGHubIfRunning()) await Task.Delay(TimeSpan.FromSeconds(2));
        var pcTask = Pc.ConnectAsync(Settings.OpenRgbPath, launchIfNeeded: true, _cts.Token);
        var ikeaTask = ConnectIkeaAsync(listenEvents);
        await Task.WhenAll(pcTask, ikeaTask);
        RaiseConnectionChanged();
        if (!listenEvents) return;

        // Au demarrage, la carte mere a oublie ses couleurs : on remet tout de suite l'etat courant.
        Pc.SetAll(!Settings.SyncPcWithRoom || AnyRoomLightOn ? Settings.Look.ToRgb() : Rgb.Black);
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        _ = WatchPcAsync(_cts.Token);
    }

    bool _rescanning;

    /// <summary>Redetection complete cote OpenRGB (~20 s), puis renvoi de la couleur courante.</summary>
    async Task RescanPcAsync()
    {
        if (_rescanning) return;
        _rescanning = true;
        try
        {
            try { await Pc.RescanAsync(_cts.Token); }
            catch (OperationCanceledException) { return; }
            RaiseConnectionChanged();
            if (!IsScreenModeOn) Pc.SetAll(Pc.LastColor);
        }
        finally { _rescanning = false; }
    }

    // ------------------------------------------------------------------ PC toujours a jour

    /// <summary>
    /// Toutes les 15 s : reconnexion si OpenRGB a redemarre, et renvoi de la derniere couleur. Les prises ARGB
    /// de la carte mere (ventilos) perdent parfois leurs couleurs, et rien ne les remettait sans action.
    /// </summary>
    async Task WatchPcAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(15), ct); }
            catch (OperationCanceledException) { return; }
            if (_rescanning) continue;
            if (CloseGHubIfRunning())
            {
                // Le micro est pilote en direct (YetiXLighting) : il suffit de lui renvoyer la couleur.
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
                if (!IsScreenModeOn) Pc.SetAll(Pc.LastColor);
                continue;
            }
            await RefreshPcAsync(reconnect: !Pc.IsConnected);
        }
    }

    static readonly string[] GHubProcesses = { "lghub_system_tray", "lghub_agent", "lghub" };

    /// <summary>
    /// G HUB reimpose son propre eclairage au Yeti X par-dessus OpenRGB, et Windows le relance a l'ouverture
    /// de session meme sans entree de demarrage. Si l'option est active, on le ferme (la reconnexion OpenRGB
    /// qui suit repasse le micro en mode Direct).
    /// </summary>
    bool CloseGHubIfRunning()
    {
        if (!Settings.CloseGHub) return false;
        bool closed = false;
        foreach (var name in GHubProcesses)
        {
            foreach (var p in System.Diagnostics.Process.GetProcessesByName(name))
            {
                using (p)
                {
                    try { p.Kill(); closed = true; }
                    catch (Exception) { }
                }
            }
        }
        if (closed) Notice?.Invoke("G HUB fermé : il bloquait l'éclairage du micro.");
        return closed;
    }

    /// <summary>
    /// Au reveil de veille, les LEDs repartent eteintes et les appareils USB (Yeti X) sont re-enumeres :
    /// redetection cote OpenRGB, puis renvoi de la couleur.
    /// </summary>
    void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Resume) return;
        Post(async () =>
        {
            try { await Task.Delay(TimeSpan.FromSeconds(5), _cts.Token); }
            catch (OperationCanceledException) { return; }
            await RefreshPcAsync(reconnect: true);
            await RescanPcAsync();
        });
    }

    async Task RefreshPcAsync(bool reconnect)
    {
        if (reconnect)
        {
            bool ok = await Pc.ConnectAsync(Settings.OpenRgbPath, launchIfNeeded: false, _cts.Token);
            RaiseConnectionChanged();
            if (!ok) return;
        }
        if (!IsScreenModeOn) Pc.SetAll(Pc.LastColor);
    }

    public async Task ConnectIkeaAsync(bool listenEvents = true)
    {
        if (!IsPaired) return;
        Ikea?.Dispose();
        Ikea = new DirigeraClient(Settings.DirigeraHost!, Settings.GetToken(), Settings.DirigeraCertSha256);
        await RefreshLightsAsync();
        if (listenEvents)
            _ = Ikea.RunEventsAsync(OnIkeaEvent, live => Post(() => { IkeaLive = live; RaiseConnectionChanged(); if (live) _ = RefreshLightsAsync(); }), _cts.Token);
    }

    public async Task RefreshLightsAsync()
    {
        if (Ikea == null) return;
        try
        {
            var lights = await Ikea.GetLightsAsync(_cts.Token);
            Post(() =>
            {
                Lights.Clear();
                foreach (var l in lights) Lights.Add(l);
                IkeaOnline = true;
                RaiseConnectionChanged();
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            IkeaOnline = false;
            RaiseConnectionChanged();
            Notice?.Invoke("DIRIGERA injoignable : " + ex.Message);
        }
    }

    /// <summary>Enregistre le resultat d'un appairage reussi et se connecte.</summary>
    public async Task CompletePairingAsync(DirigeraClient paired, string token)
    {
        Settings.DirigeraHost = paired.Host;
        Settings.SetToken(token);
        Settings.DirigeraCertSha256 = paired.SeenCertSha256;
        Settings.Save();
        paired.Dispose();
        await ConnectIkeaAsync();
    }

    // ------------------------------------------------------------------ Commandes

    public async Task AllOffAsync()
    {
        StopScreenMode();
        MuteEcho();
        Pc.SetAll(Rgb.Black);
        await ForEachLight(l => l.IsOn, async l =>
        {
            await Ikea!.SetAttributesAsync(l.Id, new() { ["isOn"] = false });
            Post(() => l.IsOn = false);
        });
    }

    /// <summary>Rallume avec le dernier look (celui de l'adaptatif s'il tourne : il reste actif).</summary>
    public Task AllOnAsync()
    {
        StopScreenMode();
        return ApplyCoreAsync(Settings.Look, LookParts.All, onlyLit: false);
    }

    public Task ToggleAllAsync() => AnyRoomLightOn || Pc.IsLit ? AllOffAsync() : AllOnAsync();

    /// <summary>Une couleur a ete choisie a la main : l'adaptatif se met en pause.</summary>
    public event Action? ManualAction;

    /// <summary>Applique un look choisi par l'utilisateur a toute la chambre et au PC. parts limite ce qu'on renvoie aux lampes.</summary>
    public Task ApplyAsync(LightLook look, LookParts parts)
    {
        StopScreenMode();
        ManualAction?.Invoke();
        return ApplyCoreAsync(look, parts, onlyLit: false);
    }

    /// <summary>
    /// Application par l'adaptatif : jamais de rallumage, seulement les lampes deja allumees, et le PC.
    /// toRoom=false met a jour le PC seul (les lampes recoivent moins souvent).
    /// </summary>
    public async Task ApplyAutomatedAsync(LightLook look, bool toRoom)
    {
        if (IsScreenModeOn) return;
        Settings.Look = look;
        Pc.SetAll(look.ToRgb());
        if (toRoom) await ApplyCoreAsync(look, LookParts.Color | LookParts.Level, onlyLit: true);
    }

    async Task ApplyCoreAsync(LightLook look, LookParts parts, bool onlyLit)
    {
        Settings.Look = look;
        MuteEcho();
        Pc.SetAll(look.ToRgb());

        await ForEachLight(l => !onlyLit || l.IsOn, async l =>
        {
            if (parts.HasFlag(LookParts.Power) && !l.IsOn)
            {
                await Ikea!.SetAttributesAsync(l.Id, new() { ["isOn"] = true });
                Post(() => l.IsOn = true);
            }

            if (parts.HasFlag(LookParts.Color))
            {
                if (!look.IsWhite && l.CanColor)
                {
                    await Ikea!.SetAttributesAsync(l.Id, new()
                    {
                        ["colorHue"] = Math.Round(look.Hue),
                        ["colorSaturation"] = Math.Round(look.Saturation, 2),
                    });
                    l.Hue = look.Hue; l.Saturation = look.Saturation; l.ColorMode = "color";
                }
                else if (l.CanTemperature)
                {
                    // Une ampoule "blanc seulement" prend le blanc le plus proche de la couleur demandee.
                    int kelvin = look.IsWhite ? look.Kelvin : (look.Hue is > 20 and < 200 ? l.KelvinCool : l.KelvinWarm);
                    kelvin = Math.Clamp(kelvin, l.KelvinWarm, l.KelvinCool);
                    await Ikea!.SetAttributesAsync(l.Id, new() { ["colorTemperature"] = kelvin });
                    l.Kelvin = kelvin; l.ColorMode = "temperature";
                }
            }

            if (parts.HasFlag(LookParts.Level) && l.CanDim)
            {
                int level = Math.Clamp((int)Math.Round(look.Level * 100), 1, 100);
                await Ikea!.SetAttributesAsync(l.Id, new() { ["lightLevel"] = level });
                Post(() => l.Level = level);
            }
        });
    }

    public async Task SetLightOnAsync(IkeaLight light, bool on)
    {
        if (Ikea == null) return;
        MuteEcho();
        try
        {
            await Ikea.SetAttributesAsync(light.Id, new() { ["isOn"] = on });
            Post(() => light.IsOn = on);
            if (Settings.SyncPcWithRoom) MirrorRoomToPc(light, raise: false);
        }
        catch (Exception ex)
        {
            Notice?.Invoke($"{light.Name} : {ex.Message}");
        }
    }

    async Task ForEachLight(Func<IkeaLight, bool> filter, Func<IkeaLight, Task> action)
    {
        if (Ikea == null) return;
        var targets = Lights.Where(l => l.IsReachable && filter(l)).ToList();
        var results = await Task.WhenAll(targets.Select(async l =>
        {
            try { await action(l); return null; }
            catch (Exception ex) { return $"{l.Name} : {ex.Message}"; }
        }));
        var errors = results.Where(r => r != null).ToList();
        if (errors.Count > 0) Notice?.Invoke(errors[0]!);
    }

    // ------------------------------------------------------------------ Synchro chambre -> PC

    void OnIkeaEvent(JsonElement e)
    {
        if (!e.TryGetProperty("type", out var type) || type.GetString() != "deviceStateChanged") return;
        if (!e.TryGetProperty("data", out var data) || !data.TryGetProperty("id", out var idEl)) return;
        var id = idEl.GetString();

        Post(() =>
        {
            var light = Lights.FirstOrDefault(l => l.Id == id);
            if (light == null) return;
            if (data.TryGetProperty("attributes", out var attrs)) light.Apply(attrs);
            if (data.TryGetProperty("isReachable", out var r) && r.ValueKind is JsonValueKind.True or JsonValueKind.False)
                light.IsReachable = r.GetBoolean();

            if (DateTime.UtcNow < _ignoreEventsUntil || !Settings.SyncPcWithRoom || IsScreenModeOn) return;
            MirrorRoomToPc(light, raise: true);
        });
    }

    /// <summary>Le PC prend la couleur de la lampe qui vient de changer (ou s'eteint si toute la chambre est eteinte).</summary>
    void MirrorRoomToPc(IkeaLight changed, bool raise)
    {
        var lit = Lights.Where(l => l.IsOn && l.IsReachable).ToList();
        if (lit.Count == 0)
        {
            Pc.SetAll(Rgb.Black);
            if (raise) RoomChangedExternally?.Invoke(null);
            return;
        }
        var source = changed.IsOn ? changed : lit[0];
        var look = source.ToLook();
        Pc.SetAll(look.ToRgb());
        if (raise) RoomChangedExternally?.Invoke(look);
    }

    void MuteEcho() => _ignoreEventsUntil = DateTime.UtcNow.AddSeconds(1.5);

    // ------------------------------------------------------------------ Mode ecran

    CancellationTokenSource? _screenCts;

    public bool IsScreenModeOn => _screenCts != null;
    /// <summary>Les lampes de la chambre suivent aussi l'ecran (plus lentes : le Zigbee n'aime pas etre inonde).</summary>
    public bool ScreenModeIncludesRoom { get; set; } = true;
    /// <summary>Couleur captee, environ 10 fois par seconde (apercu dans l'UI).</summary>
    public event Action<Rgb>? ScreenColor;

    public void StartScreenMode()
    {
        if (_screenCts != null) return;
        _screenCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        var ct = _screenCts.Token;
        _ = Task.Run(() => ScreenLoopAsync(ct), ct);
    }

    public void StopScreenMode()
    {
        if (_screenCts == null) return;
        _screenCts.Cancel();
        _screenCts = null;
        Post(() => ScreenModeChanged?.Invoke());
    }

    /// <summary>Le mode ecran s'est arrete tout seul (une autre commande a repris la main).</summary>
    public event Action? ScreenModeChanged;

    async Task ScreenLoopAsync(CancellationToken ct)
    {
        using var sampler = new ScreenSampler();
        double r = 0, g = 0, b = 0;
        int frame = 0;
        var lastRoomSend = DateTime.MinValue;
        Rgb lastRoomColor = Rgb.Black;
        Task roomSend = Task.CompletedTask;

        while (!ct.IsCancellationRequested)
        {
            Rgb target;
            try { target = sampler.Sample(); }
            catch (Exception) { await Task.Delay(500, ct).ContinueWith(_ => { }); continue; }

            // Lissage : transitions douces, pas de clignotement sur les changements de plan rapides.
            const double k = 0.35;
            r += (target.R - r) * k; g += (target.G - g) * k; b += (target.B - b) * k;
            var color = new Rgb((byte)r, (byte)g, (byte)b);
            if (ct.IsCancellationRequested) break;
            Pc.SetAll(color);

            if (++frame % 3 == 0) Post(() => ScreenColor?.Invoke(color));

            if (ScreenModeIncludesRoom && Ikea != null && roomSend.IsCompleted
                && DateTime.UtcNow - lastRoomSend > TimeSpan.FromMilliseconds(1200)
                && Distance(color, lastRoomColor) > 18)
            {
                lastRoomSend = DateTime.UtcNow;
                lastRoomColor = color;
                roomSend = SendScreenColorToRoomAsync(color, ct);
            }

            try { await Task.Delay(33, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    async Task SendScreenColorToRoomAsync(Rgb color, CancellationToken ct)
    {
        MuteEcho();
        var (h, s, v) = ColorMath.ToHsv(color);
        int level = Math.Clamp((int)Math.Round(v * 100), 1, 100);
        IkeaLight[] lights;
        try { lights = Lights.Where(l => l.IsReachable).ToArray(); }
        catch (InvalidOperationException) { return; }

        await Task.WhenAll(lights.Select(async l =>
        {
            try
            {
                if (!l.IsOn) await Ikea!.SetAttributesAsync(l.Id, new() { ["isOn"] = true }, ct);
                if (l.CanColor)
                    await Ikea!.SetAttributesAsync(l.Id, new() { ["colorHue"] = Math.Round(h), ["colorSaturation"] = Math.Round(s, 2) }, ct);
                if (l.CanDim)
                    await Ikea!.SetAttributesAsync(l.Id, new() { ["lightLevel"] = level }, ct);
            }
            catch (Exception) { }
        }));
    }

    static int Distance(Rgb a, Rgb b) => Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);

    // ------------------------------------------------------------------ Divers

    public async Task ReconnectPcAsync()
    {
        await Pc.ConnectAsync(Settings.OpenRgbPath, launchIfNeeded: true, _cts.Token);
        RaiseConnectionChanged();
    }

    public void SetPcDeviceIncluded(string name, bool included)
    {
        if (included) Pc.Excluded.Remove(name); else Pc.Excluded.Add(name);
        Settings.ExcludedPcDevices = Pc.Excluded.ToList();
        Settings.Save();
        Pc.SetDevice(name, included && Pc.IsLit ? Pc.LastColor : Rgb.Black);
    }

    void RaiseConnectionChanged() => Post(() => ConnectionChanged?.Invoke());

    void Post(Action action)
    {
        if (_ui == null || SynchronizationContext.Current == _ui) action();
        else _ui.Post(_ => action(), null);
    }

    public void Dispose()
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        StopScreenMode();
        _cts.Cancel();
        Ikea?.Dispose();
        Pc.Dispose();
    }
}
