using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Windows.System;

namespace TidalManager.App.Views;

public sealed partial class HubPage : Page
{
    private readonly ObservableCollection<TileItem> _playlists = [];
    private readonly ObservableCollection<TileItem> _albums = [];
    private bool _loadingPlaylists;

    public HubPage()
    {
        InitializeComponent();
        PlaylistTiles.ItemsSource = _playlists;
        AlbumTiles.ItemsSource = _albums;
        App.SessionChanged += () => _ = LoadPlaylistsAsync();
        Loaded += (_, _) => App.SyncSet.Changed += RefreshSelections;
        Unloaded += (_, _) => App.SyncSet.Changed -= RefreshSelections;
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ShowChosenAlbums();
        foreach (var tile in _playlists) tile.Refresh();
        if (_playlists.Count == 0) await LoadPlaylistsAsync();
    }

    private void ShowChosenAlbums()
    {
        _albums.Clear();
        foreach (var album in App.SyncSet.Items.Where(item => item.Kind == SyncKind.Album))
            _albums.Add(new TileItem(album));
    }

    private void RefreshSelections()
    {
        foreach (var tile in _playlists) tile.Refresh();
        ShowChosenAlbums();
    }

    private async Task LoadPlaylistsAsync()
    {
        if (_loadingPlaylists) return;
        _loadingPlaylists = true;
        PlaylistMessage.Visibility = Visibility.Collapsed;
        PlaylistLoading.Visibility = Visibility.Visible;
        _playlists.Clear();
        try
        {
            if (!await App.EnsureSessionAsync())
            {
                ShowPlaylistMessage("Sign in to TIDAL to see your playlists.", "sign in");
                return;
            }
            var playlists = await App.Library.GetAllFavoritePlaylistsAsync();
            foreach (var playlist in playlists) _playlists.Add(new TileItem(SyncKind.Playlist, playlist));
            if (_playlists.Count == 0) ShowPlaylistMessage("You haven't saved any playlists on TIDAL yet.", null);
        }
        catch (Exception ex)
        {
            ShowPlaylistMessage("Couldn't load your playlists. " + ex.Message, "settings");
        }
        finally
        {
            PlaylistLoading.Visibility = Visibility.Collapsed;
            _loadingPlaylists = false;
        }
    }

    private void ShowPlaylistMessage(string text, string? button)
    {
        PlaylistMessageText.Text = text;
        PlaylistMessageButton.Content = button;
        PlaylistMessageButton.Visibility = button is null ? Visibility.Collapsed : Visibility.Visible;
        PlaylistMessage.Visibility = Visibility.Visible;
    }

    private void Tile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: TileItem tile }) tile.IsSelected = !tile.IsSelected;
    }

    private void FindAlbums_Click(object sender, RoutedEventArgs e) => SearchBox.Focus(FocusState.Programmatic);

    private void SearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        e.Handled = true;
        Search();
    }

    private void Search_Click(object sender, RoutedEventArgs e) => Search();

    private void Search()
    {
        var query = SearchBox.Text.Trim();
        if (query.Length == 0)
        {
            SearchBox.Focus(FocusState.Programmatic);
            return;
        }
        SearchBox.Text = "";
        App.CurrentWindow.Navigate(typeof(SearchPage), query);
    }

    private void Settings_Click(object sender, RoutedEventArgs e) => App.CurrentWindow.ShowSettings();
}
