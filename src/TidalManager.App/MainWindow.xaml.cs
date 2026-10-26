using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using TidalManager.App.Views;

namespace TidalManager.App;

public sealed partial class MainWindow : Window
{
    private readonly SyncEngine _engine = new();
    private CancellationTokenSource? _sync;
    private double _fraction;

    public MainWindow()
    {
        InitializeComponent();
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (File.Exists(iconPath)) AppWindow.SetIcon(iconPath);
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(DragRegion);
        AppWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        RootGrid.ActualThemeChanged += (_, _) => UpdateTitleBarTheme();
        RootGrid.Loaded += (_, _) => UpdateTitleBarTheme();
        if (AppWindow.Presenter is OverlappedPresenter presenter) presenter.Maximize();
        SizeChanged += (_, _) => PlaceSettings();
        App.SyncSet.Changed += ShowReadyState;
        ShowReadyState();
        ContentFrame.Navigate(typeof(HubPage));
    }

    private void UpdateTitleBarTheme()
    {
        var dark = RootGrid.ActualTheme == ElementTheme.Dark;
        var foreground = dark ? Colors.White : Colors.Black;
        AppWindow.TitleBar.ButtonForegroundColor = foreground;
        AppWindow.TitleBar.ButtonHoverForegroundColor = foreground;
        AppWindow.TitleBar.ButtonPressedForegroundColor = foreground;
        AppWindow.TitleBar.ButtonHoverBackgroundColor = dark
            ? Windows.UI.Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)
            : Windows.UI.Color.FromArgb(0x1A, 0, 0, 0);
    }

    internal void Navigate(Type page, object? parameter = null) => ContentFrame.Navigate(page, parameter);

    internal void GoBack()
    {
        if (ContentFrame.CanGoBack) ContentFrame.GoBack();
    }

    internal void ShowSettings()
    {
        PlaceSettings();
        Settings.Refresh();
        SettingsPopup.IsOpen = true;
    }

    private void PlaceSettings()
    {
        if (RootGrid.XamlRoot is null) return;
        SettingsPopup.HorizontalOffset = RootGrid.ActualWidth - Settings.Width;
        Settings.Height = RootGrid.ActualHeight;
    }

    private void Settings_BackRequested(object? sender, EventArgs e) => SettingsPopup.IsOpen = false;

    private void SettingsPopup_Closed(object? sender, object e)
    {
        Settings.Save();
        ShowReadyState();
    }

    private async void Sync_Click(object sender, RoutedEventArgs e)
    {
        if (_sync is not null)
        {
            _sync.Cancel();
            return;
        }
        _sync = new CancellationTokenSource();
        var syncing = App.SyncSet.Items.ToArray();
        SyncButton.Content = "cancel";
        SyncButton.Tag = "";
        DetailText.Text = "";
        var progress = new Progress<SyncUpdate>(update =>
        {
            SetProgress(update.Fraction);
            StatusText.Text = update.Message;
        });
        try
        {
            var summary = await _engine.RunAsync(progress, _sync.Token);
            App.SyncSet.RemoveSynced(syncing);
            StatusText.Text = "Your player is up to date";
            DetailText.Text = summary;
            DetailText.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["MetroDimTextBrush"];
        }
        catch (OperationCanceledException)
        {
            SetProgress(0);
            StatusText.Text = "Sync cancelled";
        }
        catch (Exception ex)
        {
            SetProgress(0);
            StatusText.Text = "Couldn't sync";
            DetailText.Text = ex.Message;
            DetailText.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["MetroErrorBrush"];
        }
        finally
        {
            _sync.Dispose();
            _sync = null;
            SyncButton.Content = "sync";
            SyncButton.Tag = "";
        }
    }

    private void ShowReadyState()
    {
        if (_sync is not null) return;
        var items = App.SyncSet.Items;
        var playlists = items.Count(item => item.Kind == SyncKind.Playlist);
        var albums = items.Count - playlists;
        StatusText.Text = items.Count == 0
            ? "Tap playlists or albums to put them on your player"
            : $"{playlists} playlist{(playlists == 1 ? "" : "s")} and {albums} album{(albums == 1 ? "" : "s")} ready to sync";
        if (string.IsNullOrEmpty(DetailText.Text) || DetailText.Text.StartsWith("Player:", StringComparison.Ordinal))
        {
            var player = App.DownloadSettings.Load().PlayerFolder;
            DetailText.Text = string.IsNullOrWhiteSpace(player) ? "Player: not set — open settings"
                : Directory.Exists(player) ? $"Player: {player}" : $"Player: {player} (not connected)";
            DetailText.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["MetroDimTextBrush"];
        }
    }

    private void SetProgress(double fraction)
    {
        _fraction = Math.Clamp(fraction, 0, 1);
        ProgressFill.Width = ProgressTrack.ActualWidth * _fraction;
    }

    private void ProgressTrack_SizeChanged(object sender, SizeChangedEventArgs e) => SetProgress(_fraction);
}
