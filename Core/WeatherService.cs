using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Lueur.Core;

public enum Sky { Unknown, Clear, Cloudy, Rain, Snow }

public sealed record WeatherNow(Sky Sky, int Code, bool IsDay, DateTime Sunrise, DateTime Sunset, DateTime FetchedAt)
{
    public string Describe() => Sky switch
    {
        Sky.Clear => "Ciel degage",
        Sky.Cloudy => "Nuageux",
        Sky.Rain => "Pluie",
        Sky.Snow => "Neige",
        _ => "Meteo inconnue",
    };
}

/// <summary>
/// Meteo et heures de lever/coucher du soleil via Open-Meteo (gratuit, sans compte, sans cle).
/// Seules la latitude et la longitude de la ville sont envoyees. Resultat garde 10 minutes.
/// </summary>
public sealed class WeatherService
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };
    WeatherNow? _cache;

    double? _latitude, _longitude;
    public WeatherNow? Last => _cache;
    public bool HasLocation => _latitude.HasValue && _longitude.HasValue;

    /// <summary>Change la ville (vide = pas de meteo). La prochaine lecture repart d'Open-Meteo.</summary>
    public void SetLocation(double? latitude, double? longitude)
    {
        _latitude = latitude;
        _longitude = longitude;
        _cache = null;
    }

    /// <summary>
    /// Cherche une ville par son nom (geocodage Open-Meteo, en francais). Renvoie jusqu'a 5 resultats :
    /// nom lisible ("Lyon, Auvergne-Rhone-Alpes, France"), latitude, longitude.
    /// </summary>
    public static async Task<List<(string Label, double Lat, double Lon)>> FindCityAsync(string query, CancellationToken ct = default)
    {
        var results = new List<(string, double, double)>();
        if (string.IsNullOrWhiteSpace(query)) return results;
        var url = $"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(query.Trim())}&count=5&language=fr&format=json";
        using var doc = JsonDocument.Parse(await Http.GetStringAsync(url, ct));
        if (!doc.RootElement.TryGetProperty("results", out var list)) return results;
        foreach (var r in list.EnumerateArray())
        {
            var parts = new[] { "name", "admin1", "country" }
                .Select(k => r.TryGetProperty(k, out var v) ? v.GetString() : null)
                .Where(s => !string.IsNullOrEmpty(s))
                .Distinct();
            results.Add((string.Join(", ", parts), r.GetProperty("latitude").GetDouble(), r.GetProperty("longitude").GetDouble()));
        }
        return results;
    }

    public async Task<WeatherNow?> GetAsync(CancellationToken ct = default)
    {
        if (!HasLocation) return null;
        double Latitude = _latitude!.Value, Longitude = _longitude!.Value;
        if (_cache != null && DateTime.Now - _cache.FetchedAt < TimeSpan.FromMinutes(10) && _cache.Sunrise.Date == DateTime.Today)
            return _cache;
        try
        {
            var url = string.Create(CultureInfo.InvariantCulture,
                $"https://api.open-meteo.com/v1/forecast?latitude={Latitude:F4}&longitude={Longitude:F4}&current=weather_code,is_day&daily=sunrise,sunset&timezone=auto&forecast_days=1");
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(url, ct));
            var current = doc.RootElement.GetProperty("current");
            var daily = doc.RootElement.GetProperty("daily");
            int code = current.GetProperty("weather_code").GetInt32();
            _cache = new WeatherNow(
                Classify(code),
                code,
                current.GetProperty("is_day").GetInt32() == 1,
                DateTime.Parse(daily.GetProperty("sunrise")[0].GetString()!, CultureInfo.InvariantCulture),
                DateTime.Parse(daily.GetProperty("sunset")[0].GetString()!, CultureInfo.InvariantCulture),
                DateTime.Now);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // Pas de reseau : on garde la derniere valeur connue.
        }
        return _cache;
    }

    /// <summary>Codes meteo WMO : 0-1 degage, 2-3 et 45-48 nuageux/brouillard, bruine/pluie/averses/orage = pluie.</summary>
    public static Sky Classify(int code) => code switch
    {
        0 or 1 => Sky.Clear,
        2 or 3 or 45 or 48 => Sky.Cloudy,
        >= 51 and <= 67 or >= 80 and <= 82 or >= 95 => Sky.Rain,
        >= 71 and <= 77 or 85 or 86 => Sky.Snow,
        _ => Sky.Unknown,
    };
}
