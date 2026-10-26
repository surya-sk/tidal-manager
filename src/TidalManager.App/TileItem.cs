using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using TidalManager.Tidal;

namespace TidalManager.App;

// One square tile: a playlist or album with its cover, checked when it is in the sync set.
public sealed class TileItem : INotifyPropertyChanged
{
    private bool _isSelected;
    private ImageSource? _image;

    internal TileItem(SyncKind kind, CollectionItem item) : this(kind, item.Id, item.Title, item.Subtitle,
        item.Artwork?.ToString()) { }

    internal TileItem(SyncItem item) : this(item.Kind, item.Id, item.Title, item.Subtitle, item.Artwork) { }

    private TileItem(SyncKind kind, string id, string title, string subtitle, string? artwork)
    {
        Kind = kind;
        Id = id;
        Title = title;
        Subtitle = subtitle;
        Artwork = artwork;
        _isSelected = App.SyncSet.Contains(kind, id);
    }

    internal SyncKind Kind { get; }
    public string Id { get; }
    public string Title { get; }
    public string Subtitle { get; }
    internal string? Artwork { get; }

    public ImageSource? Image => _image ??= Artwork is not null && Uri.TryCreate(Artwork, UriKind.Absolute, out var uri)
        ? new BitmapImage(uri) { DecodePixelWidth = 200, DecodePixelType = DecodePixelType.Logical }
        : null;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            App.SyncSet.Set(ToSyncItem(), value);
            PropertyChanged?.Invoke(this, new(nameof(IsSelected)));
            PropertyChanged?.Invoke(this, new(nameof(CheckVisibility)));
        }
    }

    public Visibility CheckVisibility => _isSelected ? Visibility.Visible : Visibility.Collapsed;

    // Picks up changes made to the sync set elsewhere (another page).
    internal void Refresh()
    {
        var selected = App.SyncSet.Contains(Kind, Id);
        if (selected == _isSelected) return;
        _isSelected = selected;
        PropertyChanged?.Invoke(this, new(nameof(IsSelected)));
        PropertyChanged?.Invoke(this, new(nameof(CheckVisibility)));
    }

    internal SyncItem ToSyncItem() => new(Kind, Id, Title, Subtitle, Artwork);

    public event PropertyChangedEventHandler? PropertyChanged;
}
