using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Lueur.Core;

/// <summary>
/// Mises a jour depuis les Releases GitHub (comme Lumen), sans installateur :
///   1. on lit la derniere release de github.com/Flaimeur/Lueur ;
///   2. si elle est plus recente, on telecharge Lueur.exe et son empreinte Lueur.exe.sha256, on verifie,
///      et on la range a cote de l'appli (Lueur.update.exe) ;
///   3. au demarrage suivant, Lueur se remplace par cette version et se relance avec les memes arguments.
/// Un exe Windows en cours d'execution peut etre renomme (pas ecrase) : c'est ce qui permet l'echange.
/// </summary>
public sealed class Updater
{
    public const string Repository = "Flaimeur/Lueur";
    static readonly HttpClient Http = CreateClient();

    public static Version CurrentVersion { get; } =
        Assembly.GetExecutingAssembly().GetName().Version is { } v ? new Version(v.Major, v.Minor, v.Build) : new Version(1, 0, 0);

    static string ExePath => Environment.ProcessPath!;
    static string ExeDir => Path.GetDirectoryName(ExePath)!;
    static string PendingPath => Path.Combine(ExeDir, "Lueur.update.exe");
    static string OldPath => Path.Combine(ExeDir, "Lueur.old.exe");

    /// <summary>Version telechargee et prete a etre installee au prochain demarrage (null sinon).</summary>
    public Version? Ready { get; private set; }
    public string? LastError { get; private set; }

    static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Lueur-updater");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    /// <summary>
    /// A appeler tout au debut du demarrage. Supprime l'ancienne version laissee par une mise a jour, et
    /// si une nouvelle version attend, l'installe et relance Lueur. Renvoie true si Lueur doit quitter.
    /// </summary>
    public static bool ApplyPendingUpdate(string[] args)
    {
        try { if (File.Exists(OldPath)) File.Delete(OldPath); }
        catch (Exception) { } // encore verrouille quelques instants apres le redemarrage : on reessaiera

        if (!File.Exists(PendingPath)) return false;
        try
        {
            File.Move(ExePath, OldPath, overwrite: true);
            File.Move(PendingPath, ExePath);
            Process.Start(new ProcessStartInfo(ExePath)
            {
                UseShellExecute = false,
                WorkingDirectory = ExeDir,
                Arguments = string.Join(" ", args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a)),
            });
            return true;
        }
        catch (Exception)
        {
            // Dossier protege (Program Files...) ou fichier bloque : on garde la version actuelle.
            try { if (!File.Exists(ExePath) && File.Exists(OldPath)) File.Move(OldPath, ExePath); } catch (Exception) { }
            try { File.Delete(PendingPath); } catch (Exception) { }
            return false;
        }
    }

    /// <summary>Cherche une nouvelle version ; si download, la telecharge et la prepare. Renvoie la version trouvee.</summary>
    public async Task<Version?> CheckAsync(bool download, CancellationToken ct = default)
    {
        LastError = null;
        try
        {
            using var doc = JsonDocument.Parse(await Http.GetStringAsync($"https://api.github.com/repos/{Repository}/releases/latest", ct));
            var root = doc.RootElement;
            var tag = root.GetProperty("tag_name").GetString() ?? "";
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latest) || latest <= CurrentVersion) return null;
            if (!download) return latest;

            string? exeUrl = null, shaUrl = null;
            foreach (var asset in root.GetProperty("assets").EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString();
                var url = asset.GetProperty("browser_download_url").GetString();
                if (name == "Lueur.exe") exeUrl = url;
                else if (name == "Lueur.exe.sha256") shaUrl = url;
            }
            if (exeUrl == null || shaUrl == null) { LastError = "La release ne contient pas Lueur.exe et son empreinte."; return null; }

            var expected = (await Http.GetStringAsync(shaUrl, ct)).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[0];
            var temp = PendingPath + ".part";
            await using (var src = await Http.GetStreamAsync(exeUrl, ct))
            await using (var dst = File.Create(temp))
                await src.CopyToAsync(dst, ct);

            string actual;
            await using (var f = File.OpenRead(temp))
                actual = Convert.ToHexString(await SHA256.HashDataAsync(f, ct));
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(temp);
                LastError = "Le fichier telecharge est abime (empreinte differente). Nouvel essai plus tard.";
                return null;
            }

            File.Move(temp, PendingPath, overwrite: true);
            Ready = latest;
            return latest;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LastError = ex is HttpRequestException { StatusCode: System.Net.HttpStatusCode.NotFound }
                ? "Aucune version publiee pour l'instant."
                : "Verification impossible : " + ex.Message;
            return null;
        }
    }

    /// <summary>Installe tout de suite la version prete : relance Lueur, qui fera l'echange au demarrage.</summary>
    public static void RestartNow(string[] args)
    {
        Process.Start(new ProcessStartInfo(ExePath)
        {
            UseShellExecute = false,
            WorkingDirectory = ExeDir,
            Arguments = string.Join(" ", args.Append("--after-update")),
        });
    }
}
