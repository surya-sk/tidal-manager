using System.Net.Http.Headers;
using System.Text.Json;

namespace TidalManager.Tidal;

public sealed class LibraryClient(HttpClient httpClient, BrowserAuthService auth)
{
    private static readonly Uri ApiBase = new("https://openapi.tidal.com/v2/");
    private const int TileArtworkSize = 320;
    // Large enough for a player's album art screen, small enough for Rockbox to decode quickly.
    private const int PlayerArtworkSize = 640;
    private string? _countryCode;

    public async Task<IReadOnlyList<CollectionItem>> GetAllFavoritePlaylistsAsync(
        CancellationToken cancellationToken = default)
    {
        var items = new List<CollectionItem>();
        var seen = new HashSet<Uri>();
        Uri? next = null;
        do
        {
            var page = await GetFavoritePlaylistsAsync(next, cancellationToken);
            items.AddRange(page.Items);
            next = page.NextPage;
        } while (next is not null && seen.Add(next));
        return items;
    }

    public async Task<CollectionItemPage> GetFavoritePlaylistsAsync(Uri? pageUri = null,
        CancellationToken cancellationToken = default)
    {
        var uri = pageUri ?? new Uri(ApiBase, "userCollectionPlaylists/me/relationships/items");
        var (ids, next) = await GetRelationshipIdsAsync(uri, "playlists", cancellationToken);
        return new(await HydrateAsync(ids, "playlists", cancellationToken), next);
    }

    public async Task<CollectionItemPage> SearchAlbumsAsync(string query,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return new([], null);
        var country = await GetCountryCodeAsync(cancellationToken);
        var path = "searchResults?filter%5Bquery%5D=" + Uri.EscapeDataString(query.Trim()) +
                   "&explicitFilter=INCLUDE&include=albums" +
                   (country is null ? "" : "&countryCode=" + Uri.EscapeDataString(country));
        using var response = await SendAsync(new Uri(ApiBase, path), cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
        var ids = new List<string>();
        foreach (var result in DataItems(json.RootElement))
            if (result.TryGetProperty("relationships", out var relationships) &&
                relationships.TryGetProperty("albums", out var albums) &&
                albums.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                ids.AddRange(data.EnumerateArray().Where(item => String(item, "type") == "albums")
                    .Select(item => String(item, "id")).OfType<string>());
        return new(await HydrateAsync(ids.Distinct().ToArray(), "albums", cancellationToken), null);
    }

    public Task<TrackPage> GetPlaylistTracksAsync(string playlistId, Uri? pageUri = null,
        CancellationToken cancellationToken = default) =>
        GetTrackPageAsync(pageUri ?? new Uri(ApiBase,
            "playlists/" + Uri.EscapeDataString(playlistId) + "/relationships/items"), cancellationToken);

    public Task<TrackPage> GetAlbumTracksAsync(string albumId, Uri? pageUri = null,
        CancellationToken cancellationToken = default) =>
        GetTrackPageAsync(pageUri ?? new Uri(ApiBase,
            "albums/" + Uri.EscapeDataString(albumId) + "/relationships/items"), cancellationToken);

    // Every track of a playlist or album, in TIDAL order.
    public async Task<IReadOnlyList<TidalTrack>> GetAllTracksAsync(bool playlist, string id,
        CancellationToken cancellationToken = default)
    {
        var tracks = new List<TidalTrack>();
        var seen = new HashSet<Uri>();
        Uri? next = null;
        do
        {
            var page = playlist
                ? await GetPlaylistTracksAsync(id, next, cancellationToken)
                : await GetAlbumTracksAsync(id, next, cancellationToken);
            tracks.AddRange(page.Tracks);
            next = page.NextPage;
        } while (next is not null && seen.Add(next));
        return tracks;
    }

    private async Task<string?> GetCountryCodeAsync(CancellationToken ct)
    {
        if (_countryCode is not null) return _countryCode;
        try
        {
            using var response = await SendAsync(new Uri(ApiBase, "users/me"), ct);
            if (!response.IsSuccessStatusCode) return null;
            using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
            return _countryCode = json.RootElement.TryGetProperty("data", out var user) ? Attribute(user, "country") : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException) { return null; }
    }

    private async Task<(string[] Ids, Uri? Next)> GetRelationshipIdsAsync(Uri uri, string type, CancellationToken ct)
    {
        using var response = await SendAsync(uri, ct);
        await EnsureSuccessAsync(response, ct);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
        var root = json.RootElement;
        var ids = DataItems(root).Where(item => String(item, "type") == type)
            .Select(item => String(item, "id")).Where(id => !string.IsNullOrWhiteSpace(id))
            .Cast<string>().ToArray();
        var next = root.TryGetProperty("links", out var links) ? Link(links, "next", uri) : null;
        return (ids, next);
    }

    private async Task<IReadOnlyList<CollectionItem>> HydrateAsync(string[] ids, string type, CancellationToken ct)
    {
        if (ids.Length == 0) return [];
        var albums = type == "albums";
        var path = type + "?filter%5Bid%5D=" + Uri.EscapeDataString(string.Join(',', ids)) +
                   "&include=" + (albums ? "artists,coverArt" : "coverArt");
        using var response = await SendAsync(new Uri(ApiBase, path), ct);
        await EnsureSuccessAsync(response, ct);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
        var root = json.RootElement;
        var included = IncludedResources(root);
        var byId = new Dictionary<string, CollectionItem>();
        foreach (var item in DataItems(root))
        {
            var id = String(item, "id");
            if (id is null) continue;
            var title = Attribute(item, albums ? "title" : "name") ?? Attribute(item, "title") ?? id;
            var subtitle = albums
                ? RelatedNames(item, "artists", "name", included).FirstOrDefault() ?? "Unknown artist"
                : Attribute(item, "playlistType") == "MIX" ? "Mix" : Count(item, "numberOfItems") is { } count
                    ? $"{count} tracks" : "Playlist";
            byId[id] = new(id, title, subtitle, Artwork(item, included, TileArtworkSize));
        }
        return ids.Select(id => byId.GetValueOrDefault(id) ??
            new CollectionItem(id, "Unavailable " + (albums ? "album" : "playlist"), "TIDAL ID " + id))
            .ToArray();
    }

    private async Task<TrackPage> GetTrackPageAsync(Uri uri, CancellationToken cancellationToken)
    {
        var (ids, next) = await GetRelationshipIdsAsync(uri, "tracks", cancellationToken);
        return new(ids.Length == 0 ? [] : await GetTracksAsync(ids, cancellationToken), next);
    }

    private async Task<IReadOnlyList<TidalTrack>> GetTracksAsync(string[] ids, CancellationToken ct)
    {
        var path = "tracks?filter%5Bid%5D=" + Uri.EscapeDataString(string.Join(',', ids)) +
                   "&include=artists,albums,albums.coverArt";
        using var response = await SendAsync(new Uri(ApiBase, path), ct);
        await EnsureSuccessAsync(response, ct);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
        var root = json.RootElement;
        var included = IncludedResources(root);
        var byId = new Dictionary<string, TidalTrack>();
        foreach (var track in DataItems(root))
        {
            var id = String(track, "id");
            if (id is null) continue;
            var title = Attribute(track, "title") ?? id;
            var artists = RelatedNames(track, "artists", "name", included);
            var album = Related(track, "albums", included).FirstOrDefault();
            byId[id] = new(id, title, artists.FirstOrDefault() ?? "Unknown artist",
                album.ValueKind == JsonValueKind.Object ? Attribute(album, "title") ?? "Unknown album" : "Unknown album",
                Attribute(track, "version"), artists,
                album.ValueKind == JsonValueKind.Object ? Artwork(album, included, PlayerArtworkSize) : null);
        }
        return ids.Select(id => byId.GetValueOrDefault(id) ??
            new TidalTrack(id, "Unavailable track", "Not available in catalog", "TIDAL ID " + id, IsAvailable: false))
            .ToArray();
    }

    // Picks the smallest image at least `size` wide, or the largest available.
    private static Uri? Artwork(JsonElement resource, Dictionary<(string Type, string Id), JsonElement> included, int size)
    {
        foreach (var artwork in Related(resource, "coverArt", included))
        {
            if (!artwork.TryGetProperty("attributes", out var attributes) ||
                !attributes.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array)
                continue;
            var candidates = files.EnumerateArray()
                .Select(file => (Href: String(file, "href"),
                    Width: file.TryGetProperty("meta", out var meta) && meta.TryGetProperty("width", out var width) &&
                           width.TryGetInt32(out var value) ? value : 0))
                .Where(file => Uri.TryCreate(file.Href, UriKind.Absolute, out var uri) && uri.Scheme == "https")
                .OrderBy(file => file.Width)
                .ToArray();
            if (candidates.Length == 0) continue;
            var best = candidates.FirstOrDefault(file => file.Width >= size);
            return new Uri((best.Href is null ? candidates[^1] : best).Href!);
        }
        return null;
    }

    private static IEnumerable<JsonElement> DataItems(JsonElement root) =>
        root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array
            ? data.EnumerateArray().ToArray()
            : [];

    private static Dictionary<(string Type, string Id), JsonElement> IncludedResources(JsonElement root) =>
        root.TryGetProperty("included", out var resources) && resources.ValueKind == JsonValueKind.Array
            ? resources.EnumerateArray()
                .GroupBy(element => (Type: String(element, "type") ?? "", Id: String(element, "id") ?? ""))
                .ToDictionary(group => group.Key, group => group.First())
            : [];

    private async Task<HttpResponseMessage> SendAsync(Uri uri, CancellationToken ct)
    {
        if (uri.Scheme != "https" || uri.Host != ApiBase.Host || !uri.AbsolutePath.StartsWith("/v2/", StringComparison.Ordinal))
            throw new InvalidDataException("TIDAL returned a pagination link outside its API.");
        var token = await auth.GetAccessTokenAsync(ct);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.ParseAdd("application/vnd.api+json");
        return await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync(ct);
        throw new HttpRequestException($"TIDAL request failed (HTTP {(int)response.StatusCode}): {body[..Math.Min(body.Length, 250)]}");
    }

    private static Uri? Link(JsonElement links, string name, Uri currentUri)
    {
        if (links.ValueKind != JsonValueKind.Object || !links.TryGetProperty(name, out var link)) return null;
        var value = link.ValueKind == JsonValueKind.String ? link.GetString() : String(link, "href");
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (Uri.TryCreate(value, UriKind.Absolute, out var absolute)) return absolute;
        if (value[0] == '?') return new Uri(currentUri, value);
        if (value.StartsWith("/v2/", StringComparison.Ordinal)) return new Uri(ApiBase, value);
        return new Uri(ApiBase, value.TrimStart('/'));
    }

    private static IEnumerable<JsonElement> Related(JsonElement resource, string relationship,
        Dictionary<(string Type, string Id), JsonElement> included)
    {
        if (!resource.TryGetProperty("relationships", out var relationships) ||
            !relationships.TryGetProperty(relationship, out var relation) ||
            !relation.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            yield break;
        foreach (var item in data.EnumerateArray())
            if (String(item, "type") is { } type && String(item, "id") is { } id &&
                included.TryGetValue((type, id), out var related))
                yield return related;
    }

    private static IReadOnlyList<string> RelatedNames(JsonElement resource, string relationship, string attribute,
        Dictionary<(string Type, string Id), JsonElement> included) =>
        Related(resource, relationship, included).Select(item => Attribute(item, attribute)).OfType<string>().ToArray();

    private static int? Count(JsonElement element, string name) =>
        element.TryGetProperty("attributes", out var attributes) && attributes.TryGetProperty(name, out var value) &&
        value.TryGetInt32(out var count) ? count : null;

    private static string? Attribute(JsonElement element, string name)
    {
        var value = element.TryGetProperty("attributes", out var attributes) ? String(attributes, name) : null;
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static string? String(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) &&
        value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined) ? value.ToString() : null;
}
