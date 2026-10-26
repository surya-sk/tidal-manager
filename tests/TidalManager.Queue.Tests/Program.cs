using TidalManager.Queue;

// This executable also acts as a harmless tiddl stand-in for the process test.
if (args.Length > 0 && args[0] == "download")
{
    if (args.Length < 7 || args[1] != "--path" || args[3] != "--scan-path" || args[2] != args[4] || args[5] != "url" ||
        args.Skip(6).Any(url => !url.StartsWith("https://tidal.com/browse/track/", StringComparison.Ordinal)))
    {
        Console.Error.WriteLine("Unexpected tiddl arguments.");
        Environment.ExitCode = 2;
        return;
    }
    if (Environment.GetEnvironmentVariable("PYTHONIOENCODING") != "utf-8")
    {
        Console.Error.WriteLine("UTF-8 output not requested.");
        Environment.ExitCode = 3;
        return;
    }
    Console.WriteLine($"Received {args.Length - 6} URLs in {args[2]}");
    return;
}

var downloader = new ExternalCliDownloadService();
string[] urls = [ExternalCliDownloadService.TrackUrl("101"), ExternalCliDownloadService.TrackUrl("102")];
Check(urls[0] == "https://tidal.com/browse/track/101", "track URL format");

await Expect<DownloadBackendNotConfiguredException>(() =>
    downloader.DownloadAsync(new("", ""), urls), "missing settings ask for configuration");
await Expect<FileNotFoundException>(() =>
    downloader.DownloadAsync(new(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".exe"), Path.GetTempPath()), urls),
    "missing executable reports a clear failure");
await Expect<ArgumentException>(() =>
    downloader.DownloadAsync(new("tiddl", Path.GetTempPath()), ["https://example.com/x"]), "non-TIDAL URLs rejected");

var testTool = Path.Combine(AppContext.BaseDirectory, "TidalManager.Queue.Tests.exe");
if (File.Exists(testTool))
{
    var lines = new List<string>();
    await downloader.DownloadAsync(new(testTool, Path.GetTempPath()), urls, new SyncProgress(lines));
    Check(lines.Any(line => line.StartsWith("Received 2 URLs")), "all URLs passed to one tiddl run");

    var previousPath = Environment.GetEnvironmentVariable("PATH");
    try
    {
        Environment.SetEnvironmentVariable("PATH", AppContext.BaseDirectory + Path.PathSeparator + previousPath);
        await downloader.DownloadAsync(new("TidalManager.Queue.Tests", Path.GetTempPath()), urls);
    }
    finally { Environment.SetEnvironmentVariable("PATH", previousPath); }
}

Console.WriteLine("tiddl downloader checks passed.");

static async Task Expect<T>(Func<Task> action, string description) where T : Exception
{
    try { await action(); }
    catch (T) { return; }
    throw new Exception($"Failed: {description}");
}

static void Check(bool condition, string description)
{
    if (!condition) throw new Exception($"Failed: {description}");
}

// Reports synchronously so the test sees every line once the download returns.
sealed class SyncProgress(List<string> lines) : IProgress<string>
{
    public void Report(string value) { lock (lines) lines.Add(value); }
}
