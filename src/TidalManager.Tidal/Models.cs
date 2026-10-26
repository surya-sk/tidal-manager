namespace TidalManager.Tidal;

public sealed record BrowserAuthorization(
    string ClientId,
    Uri RedirectUri,
    Uri AuthorizationUri,
    string CodeVerifier,
    string State);

public sealed record TidalSession(
    string ClientId,
    string AccessToken,
    string? RefreshToken,
    DateTimeOffset ExpiresAt,
    string? UserId);

public sealed record TidalTrack(string Id, string Title, string Artist, string Album,
    string? Version = null, IReadOnlyList<string>? Artists = null, Uri? AlbumArtwork = null,
    bool IsAvailable = true)
{
    // TIDAL's display title includes the version, e.g. "Song (Remastered)".
    public string DisplayTitle => string.IsNullOrWhiteSpace(Version) ? Title : $"{Title} ({Version})";
}

public sealed record TrackPage(IReadOnlyList<TidalTrack> Tracks, Uri? NextPage);

// A playlist or album. Artwork is a small image suitable for a tile.
public sealed record CollectionItem(string Id, string Title, string Subtitle, Uri? Artwork = null);

public sealed record CollectionItemPage(IReadOnlyList<CollectionItem> Items, Uri? NextPage);

public sealed class TidalAuthException(string message, string? errorCode = null) : Exception(message)
{
    public string? ErrorCode { get; } = errorCode;
}
