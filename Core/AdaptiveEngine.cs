using System;
using System.Threading;
using System.Threading.Tasks;

namespace Lueur.Core;

/// <summary>
/// Eclairage adaptatif en couleurs. Toutes les 20 s on calcule la lumiere du moment dans le profil
/// (choisi a la main, ou selon la meteo en mode "auto") et on l'applique :
///   - au PC a chaque fois (glissement imperceptible) ;
///   - aux lampes seulement si l'ecart se voit, et au plus une fois par minute (le Zigbee reste calme).
/// Il ne rallume jamais rien : lampes eteintes = on n'y touche pas, et le PC reste eteint avec elles.
/// Une couleur choisie a la main (ambiance, curseur, routine) met l'adaptatif en pause.
/// </summary>
public sealed class AdaptiveEngine : IDisposable
{
    readonly LightController _ctl;
    readonly WeatherService _weather;
    CancellationTokenSource? _cts;
    LightLook? _lastSentToRoom;
    DateTime _lastRoomSend = DateTime.MinValue;

    public AdaptiveEngine(LightController controller, WeatherService weather)
    {
        _ctl = controller;
        _weather = weather;
        _ctl.ManualAction += () => { if (IsOn) Stop(); };
    }

    public bool IsOn => _cts != null;
    /// <summary>Profil en cours (resolu depuis la meteo en mode auto).</summary>
    public AdaptiveProfile Current { get; private set; } = AdaptiveProfile.Cloudy;
    public LightLook? CurrentLook { get; private set; }

    /// <summary>Le profil ou la couleur du moment a change (pour l'UI).</summary>
    public event Action? Changed;

    public void Start()
    {
        if (_cts != null) return;
        _ctl.Settings.AdaptiveOn = true;
        _ctl.Settings.Save();
        _lastSentToRoom = null;
        _cts = new CancellationTokenSource();
        _ = LoopAsync(_cts.Token);
        Changed?.Invoke();
    }

    public void Stop()
    {
        if (_cts == null) return;
        _cts.Cancel();
        _cts = null;
        _ctl.Settings.AdaptiveOn = false;
        _ctl.Settings.Save();
        Changed?.Invoke();
    }

    /// <summary>Change de profil ("auto", "sun", "rain", "cloud") et applique tout de suite.</summary>
    public void SetProfile(string key)
    {
        _ctl.Settings.AdaptiveProfile = key;
        _ctl.Settings.Save();
        _lastSentToRoom = null;
        if (IsOn) _ = TickAsync(force: true);
        else _ = ResolveAsync().ContinueWith(_ => Changed?.Invoke(), TaskScheduler.FromCurrentSynchronizationContext());
    }

    public async Task<AdaptiveProfile> ResolveAsync()
    {
        var key = _ctl.Settings.AdaptiveProfile;
        if (key != "auto" && AdaptiveProfile.ByKey(key) is { } chosen) Current = chosen;
        else
        {
            var w = await _weather.GetAsync();
            Current = AdaptiveProfile.ForSky(w?.Sky ?? Sky.Cloudy);
        }
        return Current;
    }

    async Task LoopAsync(CancellationToken ct)
    {
        await TickAsync(force: true);
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(20), ct); }
            catch (OperationCanceledException) { break; }
            await TickAsync(force: false);
        }
    }

    async Task TickAsync(bool force)
    {
        try
        {
            var profile = await ResolveAsync();
            var look = profile.At(DateTime.Now.TimeOfDay);
            CurrentLook = look;
            if (!IsOn) return;

            bool roomLit = _ctl.AnyRoomLightOn || (_ctl.Ikea == null && _ctl.Pc.IsLit);
            if (roomLit)
            {
                bool sendRoom = force || (DateTime.UtcNow - _lastRoomSend > TimeSpan.FromSeconds(60) && Visible(_lastSentToRoom, look));
                await _ctl.ApplyAutomatedAsync(look, toRoom: sendRoom);
                if (sendRoom)
                {
                    _lastSentToRoom = look;
                    _lastRoomSend = DateTime.UtcNow;
                }
            }
            Changed?.Invoke();
        }
        catch (Exception) { }
    }

    /// <summary>L'ecart entre deux looks se voit-il a l'oeil ?</summary>
    static bool Visible(LightLook? a, LightLook b)
    {
        if (a == null || a.IsWhite != b.IsWhite) return true;
        if (Math.Abs(a.Level - b.Level) >= 0.03) return true;
        if (b.IsWhite) return Math.Abs(a.Kelvin - b.Kelvin) >= 60;
        double dh = Math.Abs(((b.Hue - a.Hue + 540) % 360) - 180);
        return dh >= 3 || Math.Abs(a.Saturation - b.Saturation) >= 0.04;
    }

    public void Dispose() => _cts?.Cancel();
}
