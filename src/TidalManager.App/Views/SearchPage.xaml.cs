using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Windows.System;

namespace TidalManager.App.Views;

public sealed partial class SearchPage : Page
{
    private readonly ObservableCollection<TileItem> _results = [];
    private CancellationTokenSource? _search;

    public SearchPage()
    {
        InitializeComponent();
        Results.ItemsSource = _results;
        Loaded += (_, _) => App.SyncSet.Changed += RefreshSelections;
        Unloaded += (_, _) => App.SyncSet.Changed -= RefreshSelections;
    }

    private void RefreshSelections()
    {
        foreach (var tile in _results) tile.Refresh();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await SearchAsync(e.Parameter as string ?? "");
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _search?.Cancel();
        base.OnNavigatedFrom(e);
    }

    private async Task SearchAsync(string query)
    {
        _search?.Cancel();
        var search = _search = new CancellationTokenSource();
        Heading.Text = $"results for “{query}”";
        Hint.Text = "Tap albums to put them on your player.";
        LoadingBar.Visibility = Visibility.Visible;
        _results.Clear();
        try
        {
            if (!await App.EnsureSessionAsync())
            {
                Hint.Text = "Sign in to TIDAL in settings to search.";
                return;
            }
            var page = await App.Library.SearchAlbumsAsync(query, search.Token);
            if (search.IsCancellationRequested) return;
            foreach (var album in page.Items) _results.Add(new TileItem(SyncKind.Album, album));
            if (_results.Count == 0) Hint.Text = $"No albums found for “{query}”.";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Hint.Text = "Couldn't search TIDAL. " + ex.Message; }
        finally
        {
            if (search == _search) LoadingBar.Visibility = Visibility.Collapsed;
        }
    }

    private void Tile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: TileItem tile }) tile.IsSelected = !tile.IsSelected;
    }

    private void Back_Click(object sender, RoutedEventArgs e) => App.CurrentWindow.GoBack();

    private async void SearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        e.Handled = true;
        await SubmitAsync();
    }

    private async void Search_Click(object sender, RoutedEventArgs e) => await SubmitAsync();

    private async Task SubmitAsync()
    {
        var query = SearchBox.Text.Trim();
        if (query.Length == 0) return;
        SearchBox.Text = "";
        await SearchAsync(query);
    }
}
