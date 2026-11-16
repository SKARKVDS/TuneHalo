using System;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TuneHalo;

/// <summary>
/// Minimal Spotify Web API client, used only for "like" (save to library).
/// Authorization code + PKCE: no client secret is stored, only a DPAPI-protected refresh token.
/// Since the February 2026 dev-mode changes, library calls go through /me/library with URIs;
/// the legacy /me/tracks endpoints are kept as a fallback.
/// </summary>
public sealed class SpotifyApi
{
    public const int CallbackPort = 8765;
    public static readonly string RedirectUri = $"http://127.0.0.1:{CallbackPort}/callback";
    private const string Scopes = "user-read-currently-playing user-library-read user-library-modify "
        + "user-read-playback-state user-modify-playback-state";
    private const string PlayerScope = "user-modify-playback-state";
    private string _grantedScopes = "";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly AppSettings _settings;
    private string? _accessToken;
    private DateTimeOffset _accessExpires;
    private DateTimeOffset _blockedUntil = DateTimeOffset.MinValue;

    public SpotifyApi(AppSettings settings) { _settings = settings; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_settings.SpotifyClientId) && _settings.RefreshToken != null;

    // ---- login (PKCE) --------------------------------------------------------

    public async Task<string?> LoginAsync(CancellationToken ct)
    {
        var clientId = _settings.SpotifyClientId?.Trim();
        if (string.IsNullOrEmpty(clientId)) return "Renseignez d'abord le Client ID.";

        string verifier = Base64Url(RandomNumberGenerator.GetBytes(48));
        string challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        string state = Base64Url(RandomNumberGenerator.GetBytes(16));

        var listener = new TcpListener(IPAddress.Loopback, CallbackPort);
        try { listener.Start(); }
        catch (SocketException) { return $"Le port {CallbackPort} est déjà utilisé."; }

        try
        {
            string url = "https://accounts.spotify.com/authorize"
                + "?client_id=" + Uri.EscapeDataString(clientId)
                + "&response_type=code"
                + "&redirect_uri=" + Uri.EscapeDataString(RedirectUri)
                + "&code_challenge_method=S256&code_challenge=" + challenge
                + "&state=" + state
                + "&scope=" + Uri.EscapeDataString(Scopes);
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(3));
            while (true)
            {
                using var client = await listener.AcceptTcpClientAsync(timeout.Token);
                using var stream = client.GetStream();
                var buf = new byte[8192];
                int n = await stream.ReadAsync(buf, timeout.Token);
                string request = Encoding.ASCII.GetString(buf, 0, n);
                string firstLine = request.Split('\n')[0];
                var parts = firstLine.Split(' ');
                if (parts.Length < 2 || !parts[1].StartsWith("/callback")) { await Respond(stream, 404, "Not found"); continue; }

                var q = ParseQuery(parts[1]);
                q.TryGetValue("code", out var code);
                q.TryGetValue("error", out var error);
                q.TryGetValue("state", out var gotState);

                if (error != null || code == null || gotState != state)
                {
                    await Respond(stream, 400, "Connexion refusée ou invalide. Vous pouvez fermer cet onglet.");
                    return "Connexion annulée : " + (error ?? "réponse invalide");
                }

                await Respond(stream, 200, "TuneHalo est connecté à Spotify. Vous pouvez fermer cet onglet.");
                var err = await RequestTokenAsync(new()
                {
                    ["grant_type"] = "authorization_code",
                    ["code"] = code,
                    ["redirect_uri"] = RedirectUri,
                    ["client_id"] = clientId,
                    ["code_verifier"] = verifier,
                });
                return err;
            }
        }
        catch (OperationCanceledException) { return "Délai dépassé, connexion annulée."; }
        finally { listener.Stop(); }
    }

    public void Logout()
    {
        _settings.RefreshToken = null;
        _settings.Save();
        _accessToken = null;
    }

    private static async Task Respond(NetworkStream s, int code, string message)
    {
        string body = $"<!doctype html><meta charset=utf-8><title>TuneHalo</title>"
            + $"<body style=\"font-family:Segoe UI;background:#121212;color:#eee;display:grid;place-items:center;height:90vh\"><h2>{WebUtility.HtmlEncode(message)}</h2>";
        var bytes = Encoding.UTF8.GetBytes(body);
        string head = $"HTTP/1.1 {code} OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n";
        await s.WriteAsync(Encoding.ASCII.GetBytes(head));
        await s.WriteAsync(bytes);
    }

    private static System.Collections.Generic.Dictionary<string, string> ParseQuery(string path)
    {
        var d = new System.Collections.Generic.Dictionary<string, string>();
        int i = path.IndexOf('?');
        if (i < 0) return d;
        foreach (var kv in path[(i + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var p = kv.Split('=', 2);
            d[Uri.UnescapeDataString(p[0])] = p.Length > 1 ? Uri.UnescapeDataString(p[1].Replace('+', ' ')) : "";
        }
        return d;
    }

    private static string Base64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    // ---- tokens --------------------------------------------------------------

    private async Task<string?> RequestTokenAsync(System.Collections.Generic.Dictionary<string, string> form)
    {
        try
        {
            using var resp = await Http.PostAsync("https://accounts.spotify.com/api/token", new FormUrlEncodedContent(form));
            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!resp.IsSuccessStatusCode)
            {
                if (form["grant_type"] == "refresh_token" && root.TryGetProperty("error", out var e) && e.GetString() == "invalid_grant")
                    Logout(); // revoked or expired: the user has to reconnect
                return "Spotify : " + (root.TryGetProperty("error_description", out var d) ? d.GetString() : resp.StatusCode.ToString());
            }
            _accessToken = root.GetProperty("access_token").GetString();
            _accessExpires = DateTimeOffset.Now.AddSeconds(root.GetProperty("expires_in").GetInt32() - 60);
            if (root.TryGetProperty("scope", out var sc)) _grantedScopes = sc.GetString() ?? "";
            if (root.TryGetProperty("refresh_token", out var rt) && rt.GetString() is { Length: > 0 } r)
            {
                _settings.RefreshToken = r; // Spotify may rotate it
                _settings.Save();
            }
            return null;
        }
        catch (Exception ex) { return "Spotify injoignable : " + ex.Message; }
    }

    private async Task<string?> EnsureTokenAsync()
    {
        if (_accessToken != null && DateTimeOffset.Now < _accessExpires) return null;
        if (!IsConfigured) return "Spotify non connecté (Paramètres).";
        return await RequestTokenAsync(new()
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = _settings.RefreshToken!,
            ["client_id"] = _settings.SpotifyClientId!.Trim(),
        });
    }

    private async Task<(HttpStatusCode Status, string Body)> SendAsync(HttpMethod method, string url)
    {
        if (DateTimeOffset.Now < _blockedUntil) throw new SpotifyException("Trop de requêtes, réessayez dans un instant.");
        var err = await EnsureTokenAsync();
        if (err != null) throw new SpotifyException(err);
        using var req = new HttpRequestMessage(method, "https://api.spotify.com/v1" + url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
        if (method == HttpMethod.Put || method == HttpMethod.Delete) req.Content = new StringContent("");
        using var resp = await Http.SendAsync(req);
        if (resp.StatusCode == HttpStatusCode.TooManyRequests)
            _blockedUntil = DateTimeOffset.Now + (resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(30));
        if (resp.StatusCode == HttpStatusCode.Unauthorized) _accessToken = null;
        return (resp.StatusCode, await resp.Content.ReadAsStringAsync());
    }

    // ---- API calls -------------------------------------------------------------

    /// <summary>
    /// True when the token allows driving the player (volume). Accounts connected with an older
    /// build lack the scope and must click "Reconnecter" once.
    /// </summary>
    public async Task<bool> CanControlPlayerAsync()
    {
        if (!IsConfigured) return false;
        if (await EnsureTokenAsync() != null) return false;
        return _grantedScopes.Split(' ').AsSpan().Contains(PlayerScope);
    }

    /// <summary>Volume of the active Spotify device (the slider inside Spotify), or null when there is none.</summary>
    public async Task<int?> GetPlayerVolumeAsync()
    {
        var (status, body) = await SendAsync(HttpMethod.Get, "/me/player");
        if (status == HttpStatusCode.NoContent || string.IsNullOrWhiteSpace(body)) return null;
        if ((int)status >= 400) throw new SpotifyException(ApiError(status, body));
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("device", out var dev) && dev.ValueKind == JsonValueKind.Object
            && dev.TryGetProperty("volume_percent", out var v) && v.ValueKind == JsonValueKind.Number)
            return v.GetInt32();
        return null;
    }

    public async Task SetPlayerVolumeAsync(int percent)
    {
        var (status, body) = await SendAsync(HttpMethod.Put, "/me/player/volume?volume_percent=" + Math.Clamp(percent, 0, 100));
        if ((int)status >= 400) throw new SpotifyException(ApiError(status, body));
    }

    public sealed record CurrentTrack(string Uri, string Id, string Name, string Artist);

    public async Task<CurrentTrack?> GetCurrentTrackAsync()
    {
        var (status, body) = await SendAsync(HttpMethod.Get, "/me/player/currently-playing");
        if (status == HttpStatusCode.NoContent || string.IsNullOrWhiteSpace(body)) return null;
        if ((int)status >= 400) throw new SpotifyException(ApiError(status, body));
        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("item", out var item) || item.ValueKind != JsonValueKind.Object) return null;
        if (item.TryGetProperty("type", out var t) && t.GetString() != "track") return null; // podcasts etc.
        string artist = "";
        if (item.TryGetProperty("artists", out var arts) && arts.GetArrayLength() > 0)
            artist = arts[0].GetProperty("name").GetString() ?? "";
        return new CurrentTrack(item.GetProperty("uri").GetString()!, item.GetProperty("id").GetString()!,
            item.GetProperty("name").GetString() ?? "", artist);
    }

    public async Task<bool> IsSavedAsync(CurrentTrack t)
    {
        var (status, body) = await SendAsync(HttpMethod.Get, "/me/library/contains?uris=" + Uri.EscapeDataString(t.Uri));
        if (IsMissingEndpoint(status))
            (status, body) = await SendAsync(HttpMethod.Get, "/me/tracks/contains?ids=" + t.Id);
        if ((int)status >= 400) throw new SpotifyException(ApiError(status, body));
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetArrayLength() > 0 && doc.RootElement[0].GetBoolean();
    }

    public async Task SetSavedAsync(CurrentTrack t, bool saved)
    {
        var method = saved ? HttpMethod.Put : HttpMethod.Delete;
        var (status, body) = await SendAsync(method, "/me/library?uris=" + Uri.EscapeDataString(t.Uri));
        if (IsMissingEndpoint(status))
            (status, body) = await SendAsync(method, "/me/tracks?ids=" + t.Id);
        if ((int)status >= 400) throw new SpotifyException(ApiError(status, body));
    }

    private static bool IsMissingEndpoint(HttpStatusCode s) => s is HttpStatusCode.NotFound or HttpStatusCode.Gone;

    private static string ApiError(HttpStatusCode status, string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.Object
                && e.TryGetProperty("message", out var m)) return $"Spotify ({(int)status}) : {m.GetString()}";
        }
        catch { }
        return $"Spotify ({(int)status})";
    }
}

public sealed class SpotifyException(string message) : Exception(message);
