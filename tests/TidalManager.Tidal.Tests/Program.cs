using System.Net;
using System.Text;
using TidalManager.Tidal;

var store = new MemoryStore();
var handler = new StubHandler();
var auth = new BrowserAuthService(new HttpClient(handler), store);
var redirect = new Uri("http://127.0.0.1:8888/callback/");
var authorization = auth.CreateAuthorization("test-client", redirect);
Check(authorization.AuthorizationUri.Query.Contains("code_challenge_method=S256"), "PKCE challenge sent");
Check(authorization.AuthorizationUri.Query.Contains("collection.read"), "collection read scope requested");
var callback = new Uri($"{redirect}?code=one-time-code&state={authorization.State}");
try
{
    await auth.CompleteAuthorizationAsync(authorization, new Uri($"{redirect}?code=one-time-code&state=wrong"));
    throw new Exception("Failed: state mismatch accepted");
}
catch (TidalAuthException) { }
var session = await auth.CompleteAuthorizationAsync(authorization, callback);
Check(session.AccessToken == "first-token", "authorization code exchanged");
Check(session.RefreshToken == "first-refresh", "refresh token stored");
Check(await auth.GetAccessTokenAsync() == "second-token", "expired token refreshed");
Check(store.Session?.RefreshToken == "second-refresh", "rotated refresh token saved");

var restored = new BrowserAuthService(new HttpClient(handler), store);
Check(await restored.RestoreAsync("test-client"), "session restored");
var library = new LibraryClient(new HttpClient(handler), restored);

var playlists = await library.GetAllFavoritePlaylistsAsync();
Check(playlists.Count == 2 && playlists[0].Title == "Example playlist" && playlists[1].Title == "Unavailable playlist",
    "favorite playlists parsed across pages");
Check(playlists[0].Subtitle == "12 tracks", "playlist track count shown");
Check(playlists[0].Artwork?.ToString() == "https://resources.tidal.com/images/p/320x320.jpg",
    "playlist tile artwork picks the 320px image");

var albums = await library.SearchAlbumsAsync("be here now");
Check(handler.SearchQuery == "be here now" && handler.SearchCountry == "CA", "search sends query and user country");
Check(albums.Items.Count == 1 && albums.Items[0].Title == "Example album" && albums.Items[0].Subtitle == "Artist",
    "album search results hydrated");
Check(albums.Items[0].Artwork?.ToString() == "https://resources.tidal.com/images/a/320x320.jpg", "album tile artwork");

var playlistTracks = await library.GetAllTracksAsync(playlist: true, "p1");
Check(playlistTracks.Count == 1 && playlistTracks[0].Title == "Example track", "playlist tracks loaded; videos skipped");
var track = playlistTracks[0];
Check(track.Artist == "Artist" && track.Album == "Album", "relationships parsed");
Check(track.Version == "Remastered" && track.DisplayTitle == "Example track (Remastered)", "track version parsed");
Check(track.Artists!.SequenceEqual(["Artist", "Guest"]), "all track artists parsed in TIDAL order");
Check(track.AlbumArtwork?.ToString() == "https://resources.tidal.com/images/a/640x640.jpg",
    "track carries its album's 640px cover");
var albumTracks = await library.GetAllTracksAsync(playlist: false, "b1");
Check(albumTracks.Count == 1 && albumTracks[0].Title == "Example track", "album tracks loaded");
Check(handler.SeenExchange && handler.SeenRefresh && handler.SeenLibrary, "OAuth and library requests sent");
Console.WriteLine("PKCE, refresh, restore, playlists, search, tracks, and artwork checks passed.");

static void Check(bool condition, string description)
{
    if (!condition) throw new Exception($"Failed: {description}");
}

sealed class MemoryStore : ISessionStore
{
    public TidalSession? Session { get; private set; }
    public Task<TidalSession?> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(Session);
    public Task SaveAsync(TidalSession session, CancellationToken cancellationToken = default)
    {
        Session = session;
        return Task.CompletedTask;
    }
    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        Session = null;
        return Task.CompletedTask;
    }
}

sealed class StubHandler : HttpMessageHandler
{
    private const string AlbumArtwork = """
        {"type":"artworks","id":"art-a","attributes":{"files":[
          {"href":"https://resources.tidal.com/images/a/1280x1280.jpg","meta":{"width":1280,"height":1280}},
          {"href":"https://resources.tidal.com/images/a/640x640.jpg","meta":{"width":640,"height":640}},
          {"href":"https://resources.tidal.com/images/a/320x320.jpg","meta":{"width":320,"height":320}},
          {"href":"https://resources.tidal.com/images/a/160x160.jpg","meta":{"width":160,"height":160}}]}}
        """;

    public bool SeenExchange { get; private set; }
    public bool SeenRefresh { get; private set; }
    public bool SeenLibrary { get; private set; }
    public string? SearchQuery { get; private set; }
    public string? SearchCountry { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        var path = uri.AbsolutePath;
        var query = Uri.UnescapeDataString(uri.Query);
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        if (path.EndsWith("/oauth2/token") && body.Contains("grant_type=authorization_code"))
        {
            SeenExchange = body.Contains("code=one-time-code") && body.Contains("code_verifier=") &&
                body.Contains("redirect_uri=");
            return Json("""{"access_token":"first-token","refresh_token":"first-refresh","expires_in":1}""");
        }
        if (path.EndsWith("/oauth2/token") && body.Contains("grant_type=refresh_token"))
        {
            SeenRefresh = body.Contains("refresh_token=first-refresh");
            return Json("""{"access_token":"second-token","refresh_token":"second-refresh","expires_in":3600}""");
        }
        if (path.EndsWith("/userCollectionPlaylists/me/relationships/items"))
        {
            SeenLibrary = request.Headers.Authorization?.Parameter == "second-token";
            return query.Contains("page[cursor]=next")
                ? Json("""{"data":[{"type":"playlists","id":"missing"}]}""")
                : Json("""{"data":[{"type":"playlists","id":"p1"}],"links":{"next":"?page%5Bcursor%5D=next"}}""");
        }
        if (path.EndsWith("/playlists"))
            return Json("""
                {"data":[{"type":"playlists","id":"p1","attributes":{"name":"Example playlist","numberOfItems":12,"playlistType":"USER"},
                  "relationships":{"coverArt":{"data":[{"type":"artworks","id":"art-p"}]}}}],
                 "included":[{"type":"artworks","id":"art-p","attributes":{"files":[
                   {"href":"https://resources.tidal.com/images/p/750x750.jpg","meta":{"width":750,"height":750}},
                   {"href":"https://resources.tidal.com/images/p/320x320.jpg","meta":{"width":320,"height":320}}]}}]}
                """);
        if (path.EndsWith("/users/me"))
            return Json("""{"data":{"id":"u1","type":"users","attributes":{"country":"CA"}}}""");
        if (path.EndsWith("/searchResults"))
        {
            SearchQuery = query.Split("filter[query]=")[1].Split('&')[0];
            SearchCountry = query.Contains("countryCode=") ? query.Split("countryCode=")[1].Split('&')[0] : null;
            return Json("""
                {"data":[{"type":"searchResults","id":"s1","relationships":{"albums":{"data":[{"type":"albums","id":"b1"}]}}}]}
                """);
        }
        if (path.EndsWith("/albums"))
            return Json("""
                {"data":[{"type":"albums","id":"b1","attributes":{"title":"Example album"},
                  "relationships":{"artists":{"data":[{"type":"artists","id":"a1"}]},"coverArt":{"data":[{"type":"artworks","id":"art-a"}]}}}],
                 "included":[{"type":"artists","id":"a1","attributes":{"name":"Artist"}},
                """ + AlbumArtwork + "]}");
        if (path.EndsWith("/tracks"))
            return Json("""
                {"data":[{"type":"tracks","id":"101","attributes":{"title":"Example track","version":"Remastered"},
                  "relationships":{"artists":{"data":[{"type":"artists","id":"a1"},{"type":"artists","id":"a2"}]},
                                   "albums":{"data":[{"type":"albums","id":"b1"}]}}}],
                 "included":[{"type":"artists","id":"a1","attributes":{"name":"Artist"}},
                             {"type":"artists","id":"a2","attributes":{"name":"Guest"}},
                             {"type":"albums","id":"b1","attributes":{"title":"Album"},
                              "relationships":{"coverArt":{"data":[{"type":"artworks","id":"art-a"}]}}},
                """ + AlbumArtwork + "]}");
        if (path.EndsWith("/playlists/p1/relationships/items") || path.EndsWith("/albums/b1/relationships/items"))
            return Json("""{"data":[{"type":"tracks","id":"101"},{"type":"videos","id":"v1"}]}""");
        throw new Exception($"Unexpected request: {uri}");
    }

    private static HttpResponseMessage Json(string text) =>
        new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
}
