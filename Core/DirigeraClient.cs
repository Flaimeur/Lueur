using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Lueur.Core;

/// <summary>
/// Client de l'API locale de la passerelle IKEA DIRIGERA (https://ip:8443/v1), sans cloud.
///
/// Securite : la passerelle a un certificat auto-signe. A l'appairage on verifie qu'il est bien
/// emis pour "DIRIGERA HUB", puis on epingle son empreinte SHA-256 : ensuite seul ce certificat
/// precis est accepte (pas de "accepter n'importe quel certificat").
/// </summary>
public sealed class DirigeraClient : IDisposable
{
    public const int Port = 8443;

    readonly HttpClient _http;
    string? _token;
    string? _pinnedSha256;

    public string Host { get; }
    /// <summary>Empreinte du certificat vu lors de la derniere connexion (a sauvegarder apres l'appairage).</summary>
    public string? SeenCertSha256 { get; private set; }
    public bool HasToken => !string.IsNullOrEmpty(_token);

    public DirigeraClient(string host, string? token, string? pinnedCertSha256)
    {
        Host = host;
        _token = token;
        _pinnedSha256 = pinnedCertSha256;

        var handler = new SocketsHttpHandler
        {
            SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = ValidateCertificate },
            ConnectTimeout = TimeSpan.FromSeconds(4),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        };
        _http = new HttpClient(handler)
        {
            BaseAddress = new Uri($"https://{host}:{Port}/v1/"),
            Timeout = TimeSpan.FromSeconds(8),
        };
    }

    bool ValidateCertificate(object sender, X509Certificate? cert, X509Chain? chain, SslPolicyErrors errors)
    {
        if (cert == null) return false;
        var sha = CertSha256(cert);
        SeenCertSha256 = sha;
        if (_pinnedSha256 != null)
            return string.Equals(sha, _pinnedSha256, StringComparison.OrdinalIgnoreCase);
        return IsDirigeraCertificate(cert);
    }

    static bool IsDirigeraCertificate(X509Certificate cert) =>
        cert.Subject.Contains("DIRIGERA", StringComparison.OrdinalIgnoreCase);

    static string CertSha256(X509Certificate cert) => Convert.ToHexString(SHA256.HashData(cert.GetRawCertData()));

    // ------------------------------------------------------------------ Decouverte

    /// <summary>Teste si une IP repond sur 8443 avec un certificat DIRIGERA.</summary>
    public static async Task<bool> ProbeAsync(string ip, int timeoutMs, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);
        try
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(ip, Port, cts.Token);
            bool isDirigera = false;
            using var ssl = new SslStream(tcp.GetStream(), false, (_, cert, _, _) =>
            {
                isDirigera = cert != null && IsDirigeraCertificate(cert);
                return true; // on ne fait que regarder le certificat, aucune donnee n'est envoyee
            });
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = ip }, cts.Token);
            return isDirigera;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Balaye les sous-reseaux /24 des cartes reseau qui ont une passerelle et renvoie l'IP de la DIRIGERA.</summary>
    public static async Task<string?> DiscoverAsync(CancellationToken ct)
    {
        var prefixes = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up)
            .Select(n => n.GetIPProperties())
            .Where(p => p.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any)))
            .SelectMany(p => p.UnicastAddresses)
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
            .Select(a => a.Address.GetAddressBytes())
            .Select(b => $"{b[0]}.{b[1]}.{b[2]}.")
            .Distinct()
            .ToList();

        using var found = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var gate = new SemaphoreSlim(96);
        string? result = null;

        var probes = prefixes.SelectMany(prefix => Enumerable.Range(1, 254).Select(i => prefix + i)).Select(async ip =>
        {
            try { await gate.WaitAsync(found.Token); }
            catch (OperationCanceledException) { return; }
            try
            {
                if (await ProbeAsync(ip, 1200, found.Token))
                {
                    Interlocked.CompareExchange(ref result, ip, null);
                    found.Cancel();
                }
            }
            finally { gate.Release(); }
        });
        await Task.WhenAll(probes);
        return result;
    }

    // ------------------------------------------------------------------ Appairage (OAuth PKCE local)

    /// <summary>
    /// Appairage : 1) on demande un code avec un challenge PKCE, 2) l'utilisateur appuie sur le bouton
    /// ACTION sous la DIRIGERA, 3) on echange le code contre un token. On reessaie l'echange
    /// toutes les 1,5 s pendant 60 s, ce qui laisse le temps d'aller appuyer sur le bouton.
    /// </summary>
    public async Task<string> PairAsync(IProgress<int>? secondsLeft, CancellationToken ct)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._~";
        var verifier = new string(RandomNumberGenerator.GetItems<char>(alphabet, 128));
        var challenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        var authUrl = $"oauth/authorize?audience=homesmart.local&response_type=code&code_challenge={challenge}&code_challenge_method=S256";
        using var auth = await _http.GetAsync(authUrl, ct);
        auth.EnsureSuccessStatusCode();
        using var authDoc = JsonDocument.Parse(await auth.Content.ReadAsStringAsync(ct));
        var code = authDoc.RootElement.GetProperty("code").GetString()
            ?? throw new InvalidOperationException("La DIRIGERA n'a pas renvoye de code.");

        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            secondsLeft?.Report((int)Math.Ceiling((deadline - DateTime.UtcNow).TotalSeconds));
            using var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["code"] = code,
                ["name"] = Environment.MachineName,
                ["grant_type"] = "authorization_code",
                ["code_verifier"] = verifier,
            });
            using var resp = await _http.PostAsync("oauth/token", form, ct);
            if (resp.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
                _token = doc.RootElement.GetProperty("access_token").GetString();
                _pinnedSha256 = SeenCertSha256;
                return _token!;
            }
            await Task.Delay(1500, ct);
        }
        throw new TimeoutException("Le bouton de la DIRIGERA n'a pas ete presse dans les 60 secondes.");
    }

    // ------------------------------------------------------------------ Lampes

    public async Task<List<IkeaLight>> GetLightsAsync(CancellationToken ct = default)
    {
        using var req = Authorized(HttpMethod.Get, "devices");
        using var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        return doc.RootElement.EnumerateArray()
            .Select(IkeaLight.FromJson)
            .Where(l => l != null)
            .Select(l => l!)
            .OrderBy(l => l.Room).ThenBy(l => l.Name)
            .ToList();
    }

    /// <summary>PATCH /devices/{id} avec [{ "attributes": { ... } }].</summary>
    public async Task SetAttributesAsync(string deviceId, Dictionary<string, object> attributes, CancellationToken ct = default)
    {
        using var req = Authorized(HttpMethod.Patch, $"devices/{Uri.EscapeDataString(deviceId)}");
        req.Content = JsonContent.Create(new[] { new { attributes } });
        using var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
    }

    // ------------------------------------------------------------------ Scenes (visibles dans l'app IKEA)

    /// <summary>
    /// Cree une scene "userScene". Format verifie sur le firmware actuel :
    /// triggers = [{type:"app"}, {type:"time", disabled, trigger:{days:["Mon",...], time:"HH:mm"}}],
    /// actions = [{type:"device", id:"&lt;lampe&gt;", enabled:true, attributes:{...}}] (une par lampe).
    /// Renvoie l'id de la scene.
    /// </summary>
    public async Task<string> CreateSceneAsync(string name, string icon, IEnumerable<object> triggers, IEnumerable<object> actions, CancellationToken ct = default)
    {
        using var req = Authorized(HttpMethod.Post, "scenes");
        req.Content = JsonContent.Create(new
        {
            info = new { name, icon },
            type = "userScene",
            triggers = triggers.ToArray(),
            actions = actions.ToArray(),
        });
        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"DIRIGERA a refuse la scene ({(int)resp.StatusCode}) : {body}");
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("id").GetString()!;
    }

    public async Task DeleteSceneAsync(string sceneId, CancellationToken ct = default)
    {
        using var req = Authorized(HttpMethod.Delete, $"scenes/{Uri.EscapeDataString(sceneId)}");
        using var resp = await _http.SendAsync(req, ct);
        // 404 : deja supprimee (par exemple depuis l'app IKEA), ce n'est pas une erreur.
        if (resp.StatusCode != HttpStatusCode.NotFound) resp.EnsureSuccessStatusCode();
    }

    HttpRequestMessage Authorized(HttpMethod method, string path)
    {
        var req = new HttpRequestMessage(method, path);
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _token);
        return req;
    }

    // ------------------------------------------------------------------ Evenements temps reel

    /// <summary>
    /// Ecoute le websocket wss://ip:8443/v1. Chaque changement (Siri, Alexa, app IKEA, interrupteur)
    /// arrive en "deviceStateChanged". Reconnexion automatique toutes les 3 s en cas de coupure.
    /// </summary>
    public async Task RunEventsAsync(Action<JsonElement> onEvent, Action<bool> onConnection, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var ws = new ClientWebSocket();
                ws.Options.SetRequestHeader("Authorization", $"Bearer {_token}");
                ws.Options.RemoteCertificateValidationCallback = ValidateCertificate;
                ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                await ws.ConnectAsync(new Uri($"wss://{Host}:{Port}/v1"), ct);
                onConnection(true);

                var buffer = new byte[16 * 1024];
                using var message = new MemoryStream();
                while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    var res = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                    if (res.MessageType == WebSocketMessageType.Close) break;
                    message.Write(buffer, 0, res.Count);
                    if (!res.EndOfMessage) continue;

                    try
                    {
                        using var doc = JsonDocument.Parse(message.ToArray());
                        onEvent(doc.RootElement.Clone());
                    }
                    catch (JsonException) { }
                    message.SetLength(0);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception)
            {
                // passerelle redemarree, wifi coupe... on reessaie plus bas
            }

            onConnection(false);
            try { await Task.Delay(3000, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    public void Dispose() => _http.Dispose();
}
