using TidalManager.Local;
using TidalManager.Queue;

namespace TidalManager.App;

internal sealed record SyncUpdate(double Fraction, string Message);

// The whole "sync" button: TIDAL track lists → tiddl downloads of what is missing → player copy,
// playlists and album art.
internal sealed class SyncEngine
{
    private const int DownloadBatchSize = 10;
    private readonly ExternalCliDownloadService _downloader = new();
    private readonly PlayerSyncService _player = new(Path.Combine(App.DataDirectory, "tag-cache.json"));

    public async Task<string> RunAsync(IProgress<SyncUpdate> progress, CancellationToken ct)
    {
        var settings = App.DownloadSettings.Load();
        var items = App.SyncSet.Items.ToArray();
        if (items.Length == 0) throw new InvalidOperationException("Pick some playlists or albums to sync first.");
        if (string.IsNullOrWhiteSpace(settings.PlayerFolder))
            throw new InvalidOperationException("Choose your player's folder in settings.");
        if (!Directory.Exists(settings.PlayerFolder))
            throw new InvalidOperationException($"Can't find your player at {settings.PlayerFolder}. Connect it and try again.");
        if (string.IsNullOrWhiteSpace(settings.OutputFolder))
            throw new InvalidOperationException("Choose a download folder in settings.");
        if (!await App.EnsureSessionAsync()) throw new InvalidOperationException("Sign in to TIDAL in settings.");

        // 0–10%: what should be on the player.
        var collections = new List<SyncCollection>();
        var unavailable = 0;
        for (var index = 0; index < items.Length; index++)
        {
            var item = items[index];
            progress.Report(new(0.10 * index / items.Length, $"Getting {item.Title} from TIDAL"));
            var tracks = await App.Library.GetAllTracksAsync(item.Kind == SyncKind.Playlist, item.Id, ct);
            unavailable += tracks.Count(track => !track.IsAvailable);
            collections.Add(new(item.Id, item.Title, item.Kind == SyncKind.Playlist, tracks
                .Where(track => track.IsAvailable)
                .Select(track => new PlaylistTrackReference(track.Id, track.Title, track.Artist, track.Album,
                    track.Version, track.Artists, track.AlbumArtwork))
                .ToArray()));
        }

        // 10–15%: what is already downloaded or on the player.
        progress.Report(new(0.10, "Looking at your player"));
        var scan = new Progress<SyncStep>(step =>
            progress.Report(new(0.10 + 0.05 * step.Done / Math.Max(1, step.Total), step.Message)));
        var plan = await Task.Run(() => _player.Preview(settings.OutputFolder, settings.PlayerFolder, collections, scan, ct), ct);

        // 15–70%: download only the missing tracks.
        var missing = plan.MissingTracks;
        var downloadErrors = new List<string>();
        for (var start = 0; start < missing.Count; start += DownloadBatchSize)
        {
            ct.ThrowIfCancellationRequested();
            var batch = missing.Skip(start).Take(DownloadBatchSize).ToArray();
            var fraction = 0.15 + 0.55 * start / missing.Count;
            var label = $"Downloading {start + 1}–{start + batch.Length} of {missing.Count}";
            progress.Report(new(fraction, label));
            var output = new Progress<string>(line => progress.Report(new(fraction, $"{label} · {line}")));
            try
            {
                await _downloader.DownloadAsync(settings,
                    batch.Select(track => ExternalCliDownloadService.TrackUrl(track.Id)).ToArray(), output, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (ex is DownloadBackendNotConfiguredException or FileNotFoundException) throw;
                downloadErrors.Add(ex.Message);
            }
        }

        // 70–100%: copy, write playlists, add covers.
        if (missing.Count > 0)
        {
            progress.Report(new(0.70, "Matching downloads"));
            var rescan = new Progress<SyncStep>(step =>
                progress.Report(new(0.70 + 0.02 * step.Done / Math.Max(1, step.Total), step.Message)));
            plan = await Task.Run(() => _player.Preview(settings.OutputFolder, settings.PlayerFolder, collections, rescan, ct), ct);
        }
        var steps = new Progress<SyncStep>(step =>
            progress.Report(new(0.72 + 0.28 * step.Done / Math.Max(1, step.Total), step.Message)));
        var result = await Task.Run(() => _player.SyncAsync(plan, DownloadArtworkAsync, steps, ct,
            settings.DeleteAfterSync, settings.OutputFolder, RockboxArtwork.ToBaselineJpegAsync), ct);

        var playlists = items.Count(item => item.Kind == SyncKind.Playlist);
        var albums = items.Length - playlists;
        var parts = new List<string>
        {
            $"Synced {Plural(playlists, "playlist")} and {Plural(albums, "album")}",
            $"{result.Copied} new, {AlreadyOnPlayer(collections, plan)} already on the player"
        };
        if (result.Moved > 0) parts.Add($"{Plural(result.Moved, "track")} moved into Music");
        if (result.ArtworkWritten > 0) parts.Add($"{Plural(result.ArtworkWritten, "cover")} added or fixed");
        if (plan.MissingTracks.Count > 0) parts.Add($"{Plural(plan.MissingTracks.Count, "track")} couldn't be downloaded");
        if (unavailable > 0) parts.Add($"{unavailable} unavailable on TIDAL");
        if (result.Deleted > 0) parts.Add($"{result.Deleted} downloads cleaned up");
        if (downloadErrors.Count > 0) parts.Add($"tiddl: {downloadErrors[^1]}");
        progress.Report(new(1, "Done"));
        return string.Join(" · ", parts);
    }

    private static int AlreadyOnPlayer(IEnumerable<SyncCollection> collections, PlayerSyncPlan plan) =>
        collections.SelectMany(collection => collection.Tracks).Select(track => track.Id).Distinct().Count()
        - plan.MissingTracks.Count - plan.Copies.Count;

    private static string Plural(int count, string noun) => $"{count} {noun}{(count == 1 ? "" : "s")}";

    private static async Task<byte[]> DownloadArtworkAsync(Uri uri, CancellationToken ct)
    {
        if (uri.Scheme != "https" || !uri.Host.EndsWith("tidal.com", StringComparison.OrdinalIgnoreCase))
            throw new HttpRequestException($"Refusing album art from {uri.Host}.");
        return await App.Http.GetByteArrayAsync(uri, ct);
    }
}
