using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Lueur.Core;

/// <summary>Une lampe IKEA vue par la DIRIGERA. Observable pour que l'UI suive les changements (Siri, Alexa...).</summary>
public sealed class IkeaLight : ObservableObject
{
    public required string Id { get; init; }

    string _name = "";
    public string Name { get => _name; set => Set(ref _name, value); }

    string _room = "";
    public string Room { get => _room; set => Set(ref _room, value); }

    bool _isReachable = true;
    public bool IsReachable { get => _isReachable; set => Set(ref _isReachable, value); }

    bool _isOn;
    public bool IsOn { get => _isOn; set => Set(ref _isOn, value); }

    int _level = 100;
    /// <summary>Luminosite IKEA, 1..100.</summary>
    public int Level { get => _level; set { if (Set(ref _level, value)) OnPropertyChanged(nameof(Subtitle)); } }

    public double Hue { get; set; }
    public double Saturation { get; set; }
    public int Kelvin { get; set; } = 2700;
    /// <summary>"color" ou "temperature" : le dernier mode utilise par la lampe.</summary>
    public string ColorMode { get; set; } = "temperature";

    int _rawTempMin = 2202, _rawTempMax = 4000;
    public int KelvinWarm => Math.Min(_rawTempMin, _rawTempMax);
    public int KelvinCool => Math.Max(_rawTempMin, _rawTempMax);
    public bool CanColor { get; set; }
    public bool CanTemperature { get; set; }
    public bool CanDim { get; set; }

    public string Subtitle => string.IsNullOrEmpty(Room) ? $"{Level} %" : $"{Room} · {Level} %";

    /// <summary>Etat actuel de la lampe exprime comme un look (pour faire suivre le PC).</summary>
    public LightLook ToLook()
    {
        bool white = !CanColor || ColorMode == "temperature";
        return new LightLook(white, Hue, Saturation, Kelvin, Math.Clamp(Level / 100.0, 0.01, 1));
    }

    public static IkeaLight? FromJson(JsonElement device)
    {
        if (Str(device, "type") != "light" && Str(device, "deviceType") != "light") return null;
        if (!device.TryGetProperty("id", out var id)) return null;

        var light = new IkeaLight { Id = id.GetString()! };

        if (device.TryGetProperty("room", out var room) && room.ValueKind == JsonValueKind.Object)
            light.Room = Str(room, "name") ?? "";

        var canReceive = new HashSet<string>();
        if (device.TryGetProperty("capabilities", out var caps) &&
            caps.TryGetProperty("canReceive", out var recv) && recv.ValueKind == JsonValueKind.Array)
        {
            foreach (var c in recv.EnumerateArray())
                if (c.GetString() is { } s) canReceive.Add(s);
        }
        light.CanColor = canReceive.Contains("colorHue");
        light.CanTemperature = canReceive.Contains("colorTemperature");
        light.CanDim = canReceive.Contains("lightLevel");

        if (device.TryGetProperty("isReachable", out var reach) && reach.ValueKind is JsonValueKind.True or JsonValueKind.False)
            light.IsReachable = reach.GetBoolean();

        if (device.TryGetProperty("attributes", out var attrs))
            light.Apply(attrs);

        if (string.IsNullOrWhiteSpace(light.Name)) light.Name = "Lampe";
        return light;
    }

    /// <summary>Applique des attributs (complets ou partiels, comme dans les evenements websocket).</summary>
    public void Apply(JsonElement attrs)
    {
        foreach (var p in attrs.EnumerateObject())
        {
            var v = p.Value;
            switch (p.Name)
            {
                case "customName" when v.ValueKind == JsonValueKind.String:
                    Name = v.GetString()!;
                    break;
                case "isOn" when v.ValueKind is JsonValueKind.True or JsonValueKind.False:
                    IsOn = v.GetBoolean();
                    break;
                case "lightLevel" when v.ValueKind == JsonValueKind.Number:
                    Level = v.GetInt32();
                    break;
                case "colorHue" when v.ValueKind == JsonValueKind.Number:
                    Hue = v.GetDouble();
                    break;
                case "colorSaturation" when v.ValueKind == JsonValueKind.Number:
                    Saturation = v.GetDouble();
                    break;
                case "colorTemperature" when v.ValueKind == JsonValueKind.Number:
                    Kelvin = v.GetInt32();
                    break;
                case "colorMode" when v.ValueKind == JsonValueKind.String:
                    ColorMode = v.GetString()!;
                    break;
                // IKEA donne min/max dans un ordre qui varie selon les ampoules : on range nous-memes.
                case "colorTemperatureMin" when v.ValueKind == JsonValueKind.Number:
                    _rawTempMin = v.GetInt32();
                    break;
                case "colorTemperatureMax" when v.ValueKind == JsonValueKind.Number:
                    _rawTempMax = v.GetInt32();
                    break;
            }
        }
    }

    static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
