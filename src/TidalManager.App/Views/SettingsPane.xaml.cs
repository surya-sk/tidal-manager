using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TidalManager.Queue;
using Windows.Storage.Pickers;

namespace TidalManager.App.Views;

public sealed partial class SettingsPane : UserControl
{
    private readonly SignInFlow _signIn = new();
    private CancellationTokenSource? _signInCancellation;

    public SettingsPane() => InitializeComponent();

    public event EventHandler? BackRequested;

    internal async void Refresh()
    {
        var options = App.DownloadSettings.Load();
        ToolBox.Text = options.ToolPath;
        OutputBox.Text = options.OutputFolder;
        PlayerBox.Text = options.PlayerFolder ?? "";
        DeleteBox.IsChecked = options.DeleteAfterSync;
        UpdateTiddlStatus();
        try
        {
            var (clientId, redirect) = await App.LoadAccountSettingsAsync();
            ClientIdBox.Text = clientId ?? "";
            if (!string.IsNullOrWhiteSpace(redirect)) RedirectBox.Text = redirect;
            await App.EnsureSessionAsync();
        }
        catch (Exception ex) { AccountText.Text = ex.Message; }
        ShowAccount();
    }

    internal void Save()
    {
        try
        {
            App.DownloadSettings.Save(new ExternalDownloadOptions(
                ToolBox.Text.Trim(), OutputBox.Text.Trim(), PlayerBox.Text.Trim(), DeleteBox.IsChecked == true));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AccountText.Text = ex.Message; }
    }

    private void ShowAccount()
    {
        if (_signInCancellation is not null) return;
        AccountText.Text = App.IsSignedIn ? "You're signed in to TIDAL." : "Sign in with your TIDAL developer app.";
        SignInButton.Content = App.IsSignedIn ? "sign out" : "sign in";
        ClientIdBox.IsEnabled = RedirectBox.IsEnabled = !App.IsSignedIn;
    }

    private void ToolBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateTiddlStatus();

    private void UpdateTiddlStatus()
    {
        if (TiddlStatus is null || ToolBox is null) return;
        string? executable;
        try { executable = ExternalCliDownloadService.FindExecutable(ToolBox.Text); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { executable = null; }
        TiddlStatus.Text = executable is not null
            ? $"Found: {executable}. Run 'tiddl auth login' separately before syncing."
            : "tiddl was not found. Install Python and ffmpeg, then run 'py -m pip install tiddl' and 'tiddl auth login' in a terminal. Restart this app or enter the full path to tiddl.exe above.";
    }

    private async void SignIn_Click(object sender, RoutedEventArgs e)
    {
        if (_signInCancellation is not null)
        {
            _signInCancellation.Cancel();
            return;
        }
        if (App.IsSignedIn)
        {
            await App.Auth.SignOutAsync();
            App.NotifySessionChanged();
            ShowAccount();
            return;
        }
        if (!Uri.TryCreate(RedirectBox.Text.Trim(), UriKind.Absolute, out var redirect))
        {
            AccountText.Text = "Enter the redirect URI registered for your TIDAL app.";
            return;
        }
        _signInCancellation = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        SignInButton.Content = "cancel";
        PastePanel.Visibility = Visibility.Visible;
        AccountText.Text = "Opening TIDAL in your browser…";
        try
        {
            await _signIn.SignInAsync(ClientIdBox.Text.Trim(), redirect, text => AccountText.Text = text,
                _signInCancellation.Token);
            App.NotifySessionChanged();
        }
        catch (OperationCanceledException) { AccountText.Text = "Sign in cancelled."; }
        catch (Exception ex) { AccountText.Text = ex.Message; }
        finally
        {
            _signInCancellation.Dispose();
            _signInCancellation = null;
            PastePanel.Visibility = Visibility.Collapsed;
            CallbackBox.Text = "";
        }
        if (App.IsSignedIn) ShowAccount();
        else
        {
            SignInButton.Content = "sign in";
            ClientIdBox.IsEnabled = RedirectBox.IsEnabled = true;
        }
    }

    private void FinishSignIn_Click(object sender, RoutedEventArgs e)
    {
        if (Uri.TryCreate(CallbackBox.Text.Trim(), UriKind.Absolute, out var callback)) _signIn.CompleteManually(callback);
        else AccountText.Text = "Paste the full address from your browser.";
    }

    private async void BrowseOutput_Click(object sender, RoutedEventArgs e)
    {
        if (await PickFolderAsync() is { } path) OutputBox.Text = path;
    }

    private async void BrowsePlayer_Click(object sender, RoutedEventArgs e)
    {
        if (await PickFolderAsync() is { } path) PlayerBox.Text = path;
    }

    private static async Task<string?> PickFolderAsync()
    {
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker,
            WinRT.Interop.WindowNative.GetWindowHandle(App.CurrentWindow));
        return (await picker.PickSingleFolderAsync())?.Path;
    }

    private void Back_Click(object sender, RoutedEventArgs e) => BackRequested?.Invoke(this, EventArgs.Empty);
}
