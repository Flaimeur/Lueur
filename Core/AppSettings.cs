using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Lueur.Core;

/// <summary>Reglages persistants dans %APPDATA%\Lueur\settings.json.</summary>
public sealed class AppSettings
{
    public static string Folder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Lueur");

    static string FilePath => Path.Combine(Folder, "settings.json");

    // --- DIRIGERA ---
    public string? DirigeraHost { get; set; }
    /// <summary>Token d'acces chiffre avec DPAPI (lisible seulement par ton compte Windows).</summary>
    public string? DirigeraTokenProtected { get; set; }
    /// <summary>Empreinte SHA-256 du certificat de la passerelle, epinglee a l'appairage.</summary>
    public string? DirigeraCertSha256 { get; set; }

    // --- OpenRGB ---
    public string? OpenRgbPath { get; set; }
    public List<string> ExcludedPcDevices { get; set; } = new();
    /// <summary>Fermer G HUB s'il tourne : il reprend l'eclairage du Yeti X a OpenRGB.</summary>
    public bool CloseGHub { get; set; } = true;

    // --- Comportement ---
    public bool SyncPcWithRoom { get; set; } = true;
    public LightLook Look { get; set; } = LightLook.Default;

    // --- Routines et meteo ---
    public List<Routine> Routines { get; set; } = new();
    /// <summary>Ville choisie dans les reglages (vide = meteo et soleil desactives jusqu'au choix).</summary>
    public string City { get; set; } = "";
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }

    // --- Personnalisation ---
    /// <summary>Ambiances affichees dans Lueur : modifiables (ajout, remplacement, suppression).</summary>
    public List<SceneDef> Scenes { get; set; } = SceneDef.Defaults();

    // --- Mises a jour ---
    public bool AutoUpdate { get; set; } = true;

    // --- Eclairage adaptatif ---
    public bool AdaptiveOn { get; set; }
    /// <summary>"auto" (selon la meteo), "sun", "rain" ou "cloud".</summary>
    public string AdaptiveProfile { get; set; } = "auto";

    public string? GetToken()
    {
        if (string.IsNullOrEmpty(DirigeraTokenProtected)) return null;
        try
        {
            var raw = ProtectedData.Unprotect(Convert.FromBase64String(DirigeraTokenProtected), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(raw);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    public void SetToken(string? token)
    {
        DirigeraTokenProtected = token == null
            ? null
            : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(token), null, DataProtectionScope.CurrentUser));
    }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch (Exception)
        {
            // Fichier corrompu : on repart des valeurs par defaut plutot que de planter.
        }
        return new AppSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(Folder);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, FilePath, overwrite: true);
    }
}
