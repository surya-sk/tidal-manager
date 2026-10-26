using System.Text.Json;

namespace TidalManager.App;

internal enum SyncKind { Playlist, Album }

internal sealed record SyncItem(SyncKind Kind, string Id, string Title, string Subtitle, string? Artwork);

// The playlists and albums the user wants on the player. Kept across app restarts.
internal sealed class SyncSetStore(string path)
{
    private List<SyncItem>? _items;

    public IReadOnlyList<SyncItem> Items => _items ??= Load();

    public event Action? Changed;

    public bool Contains(SyncKind kind, string id) => Items.Any(item => item.Kind == kind && item.Id == id);

    public void Set(SyncItem item, bool include)
    {
        var items = (_items ??= Load());
        items.RemoveAll(existing => existing.Kind == item.Kind && existing.Id == item.Id);
        if (include) items.Add(item);
        Save(items);
        Changed?.Invoke();
    }

    public void RemoveSynced(IEnumerable<SyncItem> synced)
    {
        var keys = synced.Select(item => (item.Kind, item.Id)).ToHashSet();
        var items = (_items ??= Load());
        if (items.RemoveAll(item => keys.Contains((item.Kind, item.Id))) == 0) return;
        Save(items);
        Changed?.Invoke();
    }

    private List<SyncItem> Load()
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<List<SyncItem>>(File.ReadAllText(path)) ?? []
                : [];
        }
        catch (Exception ex) when (ex is IOException or JsonException) { return []; }
    }

    private void Save(List<SyncItem> items)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(items));
    }
}
