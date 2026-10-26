using System.Text.Json;
using Microsoft.UI.Xaml;
using TidalManager.Tidal;

namespace TidalManager.App;

public partial class App : Application
{
    private Window? _window;
    internal static MainWindow CurrentWindow { get; private set; } = null!;
    internal static HttpClient Http { get; } = new() { Timeout = TimeSpan.FromSeconds(30) };
    internal static BrowserAuthService Auth { get; private set; } = null!;
    internal static LibraryClient Library { get; private set; } = null!;
    internal static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TidalManager");
    internal static string SettingsPath { get; } = Path.Combine(DataDirectory, "settings.json");
    internal static DownloadSettingsStore DownloadSettings { get; } =
        new(Path.Combine(DataDirectory, "download-settings.json"));
    internal static SyncSetStore SyncSet { get; } = new(Path.Combine(DataDirectory, "sync-set.json"));

    // Raised after signing in or out so pages can reload.
    internal static event Action? SessionChanged;

    private static Task<bool>? _sessionRestore;

    public App()
    {
        UnhandledException += (_, args) =>
        {
            Directory.CreateDirectory(DataDirectory);
            File.WriteAllText(Path.Combine(DataDirectory, "startup-error.txt"), args.Exception.ToString());
        };
        InitializeComponent();
        Auth = new(Http, new ProtectedSessionStore(Path.Combine(DataDirectory, "session.dat")));
        Library = new(Http, Auth);
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        CurrentWindow = new MainWindow();
        _window = CurrentWindow;
        _window.Activate();
    }

    // Restores the saved TIDAL session once, whichever page needs it first.
    internal static Task<bool> EnsureSessionAsync() => _sessionRestore ??= RestoreSessionAsync();

    internal static bool IsSignedIn => Auth.ClientId is not null;

    internal static void NotifySessionChanged()
    {
        _sessionRestore = Task.FromResult(IsSignedIn);
        SessionChanged?.Invoke();
    }

    private static async Task<bool> RestoreSessionAsync()
    {
        try
        {
            var (clientId, _) = await LoadAccountSettingsAsync();
            return !string.IsNullOrWhiteSpace(clientId) && await Auth.RestoreAsync(clientId);
        }
        catch (Exception ex) when (ex is IOException or JsonException) { return false; }
    }

    internal static async Task<(string? ClientId, string? RedirectUri)> LoadAccountSettingsAsync()
    {
        if (!File.Exists(SettingsPath)) return (null, null);
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(SettingsPath));
        var root = json.RootElement;
        return (root.TryGetProperty("clientId", out var id) ? id.GetString() : null,
            root.TryGetProperty("redirectUri", out var redirect) ? redirect.GetString() : null);
    }

    internal static async Task SaveAccountSettingsAsync(string clientId, string redirectUri)
    {
        Directory.CreateDirectory(DataDirectory);
        await File.WriteAllTextAsync(SettingsPath, JsonSerializer.Serialize(new { clientId, redirectUri }));
    }
}
