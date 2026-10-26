using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TidalManager.Tidal;

public sealed class BrowserAuthService(HttpClient httpClient, ISessionStore sessionStore)
{
    private static readonly Uri AuthorizationEndpoint = new("https://login.tidal.com/authorize");
    private static readonly Uri TokenEndpoint = new("https://auth.tidal.com/v1/oauth2/token");
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private TidalSession? _session;

    public async Task<bool> RestoreAsync(string clientId, CancellationToken cancellationToken = default)
    {
        _session = await sessionStore.LoadAsync(cancellationToken);
        if (_session is not null && _session.ClientId != clientId)
        {
            _session = null;
            await sessionStore.ClearAsync(cancellationToken);
        }
        return _session is not null;
    }

    public BrowserAuthorization CreateAuthorization(string clientId, Uri redirectUri)
    {
        if (string.IsNullOrWhiteSpace(clientId)) throw new ArgumentException("Enter a TIDAL client ID.", nameof(clientId));
        if (!redirectUri.IsAbsoluteUri || redirectUri.Scheme is not ("http" or "https"))
            throw new ArgumentException("Enter an absolute HTTP or HTTPS redirect URI.", nameof(redirectUri));
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Base64Url(RandomNumberGenerator.GetBytes(24));
        var query = new Dictionary<string, string>
        {
            ["response_type"] = "code", ["client_id"] = clientId.Trim(),
            ["redirect_uri"] = redirectUri.ToString(), ["scope"] = "user.read collection.read",
            ["code_challenge_method"] = "S256", ["code_challenge"] = challenge,
            ["state"] = state
        };
        var builder = new UriBuilder(AuthorizationEndpoint)
        {
            Query = string.Join("&", query.Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"))
        };
        return new(clientId.Trim(), redirectUri, builder.Uri, verifier, state);
    }

    public async Task<TidalSession> CompleteAuthorizationAsync(
        BrowserAuthorization authorization, Uri callbackUri, CancellationToken cancellationToken = default)
    {
        if (callbackUri.GetLeftPart(UriPartial.Path).TrimEnd('/') !=
            authorization.RedirectUri.GetLeftPart(UriPartial.Path).TrimEnd('/'))
            throw new TidalAuthException("The sign-in callback did not match the registered redirect URI.");
        var parameters = ParseQuery(callbackUri.Query);
        if (!parameters.TryGetValue("state", out var state) || state != authorization.State)
            throw new TidalAuthException("The sign-in callback failed the state check.");
        if (parameters.TryGetValue("error", out var error))
            throw new TidalAuthException(parameters.GetValueOrDefault("error_description") ?? error, error);
        if (!parameters.TryGetValue("code", out var code) || string.IsNullOrWhiteSpace(code))
            throw new TidalAuthException("The sign-in callback did not contain an authorization code.");
        using var response = await httpClient.PostAsync(TokenEndpoint, Form(new()
        {
            ["grant_type"] = "authorization_code", ["client_id"] = authorization.ClientId,
            ["code"] = code, ["redirect_uri"] = authorization.RedirectUri.ToString(),
            ["code_verifier"] = authorization.CodeVerifier
        }), cancellationToken);
        using var json = await ReadJsonAsync(response, cancellationToken);
        EnsureSuccess(response, json.RootElement);
        var session = ParseSession(authorization.ClientId, json.RootElement, null);
        await sessionStore.SaveAsync(session, cancellationToken);
        _session = session;
        return session;
    }

    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        if (_session is null) throw new TidalAuthException("Sign in to TIDAL first.");
        if (_session.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1)) return _session.AccessToken;
        await _refreshGate.WaitAsync(cancellationToken);
        try
        {
            if (_session.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1)) return _session.AccessToken;
            if (string.IsNullOrWhiteSpace(_session.RefreshToken))
                throw new TidalAuthException("The session cannot be refreshed. Sign in again.");
            using var response = await httpClient.PostAsync(TokenEndpoint, Form(new()
            {
                ["client_id"] = _session.ClientId,
                ["refresh_token"] = _session.RefreshToken,
                ["grant_type"] = "refresh_token"
            }), cancellationToken);
            using var json = await ReadJsonAsync(response, cancellationToken);
            if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized &&
                Optional(json.RootElement, "error") is "invalid_grant" or "access_denied")
            {
                await SignOutAsync(cancellationToken);
                throw new TidalAuthException("TIDAL rejected the saved session. Sign in again.", "invalid_grant");
            }
            EnsureSuccess(response, json.RootElement);
            var refreshed = ParseSession(_session.ClientId, json.RootElement, _session);
            await sessionStore.SaveAsync(refreshed, cancellationToken);
            _session = refreshed;
            return refreshed.AccessToken;
        }
        finally { _refreshGate.Release(); }
    }

    public string? UserId => _session?.UserId;
    public string? ClientId => _session?.ClientId;

    public async Task SignOutAsync(CancellationToken cancellationToken = default)
    {
        _session = null;
        await sessionStore.ClearAsync(cancellationToken);
    }

    private static TidalSession ParseSession(string clientId, JsonElement root, TidalSession? previous)
    {
        var accessToken = Required(root, "access_token", "accessToken");
        var refreshToken = Optional(root, "refresh_token", "refreshToken") ?? previous?.RefreshToken;
        var userId = Optional(root, "user_id", "userId") ?? previous?.UserId;
        if (userId is null && TryGet(root, out var user, "user") && user.ValueKind == JsonValueKind.Object)
            userId = Optional(user, "id");
        var expiresIn = Number(root, 3600, "expires_in", "expiresIn");
        return new(clientId, accessToken, refreshToken,
            DateTimeOffset.UtcNow.AddSeconds(expiresIn), userId);
    }

    private static FormUrlEncodedContent Form(Dictionary<string, string> values) => new(values);

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static Dictionary<string, string> ParseQuery(string query) => query.TrimStart('?')
        .Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Select(part => part.Split('=', 2))
        .Where(parts => parts.Length == 2)
        .ToDictionary(parts => Uri.UnescapeDataString(parts[0]),
            parts => Uri.UnescapeDataString(parts[1].Replace('+', ' ')));

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try { return JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct)); }
        catch (JsonException) { throw new TidalAuthException($"TIDAL returned HTTP {(int)response.StatusCode} with an unreadable response."); }
    }

    private static void EnsureSuccess(HttpResponseMessage response, JsonElement root)
    {
        if (response.IsSuccessStatusCode) return;
        var code = Optional(root, "error", "subStatus");
        var description = Optional(root, "error_description", "errorDescription") ?? code;
        throw new TidalAuthException($"TIDAL authentication failed (HTTP {(int)response.StatusCode}): {description ?? "unknown error"}.", code);
    }

    private static string Required(JsonElement root, params string[] names) =>
        Optional(root, names) ?? throw new TidalAuthException($"TIDAL response omitted {names[0]}.");

    private static string? Optional(JsonElement root, params string[] names) =>
        TryGet(root, out var value, names) && value.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined
            ? value.ToString() : null;

    private static int Number(JsonElement root, int fallback, params string[] names) =>
        TryGet(root, out var value, names) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out var number) && number > 0 ? number : fallback;

    private static bool TryGet(JsonElement root, out JsonElement value, params string[] names)
    {
        if (root.ValueKind == JsonValueKind.Object)
            foreach (var name in names)
                if (root.TryGetProperty(name, out value)) return true;
        value = default;
        return false;
    }
}
