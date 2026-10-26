using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace TidalManager.Queue;

public sealed record ExternalDownloadOptions(string ToolPath, string OutputFolder, string? PlayerFolder = null,
    bool DeleteAfterSync = false);

public sealed class DownloadBackendNotConfiguredException(string message) : Exception(message);

// Runs a separately installed tiddl. This project never handles audio bytes.
public sealed class ExternalCliDownloadService
{
    public static string TrackUrl(string trackId) =>
        $"https://tidal.com/browse/track/{Uri.EscapeDataString(trackId)}";

    // Downloads the given TIDAL URLs into the output folder with one tiddl run.
    public async Task DownloadAsync(ExternalDownloadOptions options, IReadOnlyList<string> urls,
        IProgress<string>? output = null, CancellationToken cancellationToken = default)
    {
        if (urls.Count == 0) return;
        if (string.IsNullOrWhiteSpace(options.ToolPath))
            throw new DownloadBackendNotConfiguredException("Set the tiddl command in settings.");
        if (string.IsNullOrWhiteSpace(options.OutputFolder))
            throw new DownloadBackendNotConfiguredException("Set the download folder in settings.");
        if (urls.Any(url => !url.StartsWith("https://tidal.com/browse/", StringComparison.Ordinal)))
            throw new ArgumentException("Only TIDAL browse URLs can be downloaded.", nameof(urls));

        var toolPath = ResolveExecutable(options.ToolPath);
        var outputFolder = Path.GetFullPath(options.OutputFolder);
        Directory.CreateDirectory(outputFolder);

        var startInfo = new ProcessStartInfo(toolPath)
        {
            WorkingDirectory = outputFolder,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        // tiddl is Python: with redirected output it would otherwise encode with the ANSI code page
        // and crash on track names outside it.
        startInfo.Environment["PYTHONIOENCODING"] = "utf-8";
        startInfo.Environment["PYTHONUTF8"] = "1";
        // The folder must be passed explicitly; tiddl ignores the working directory.
        foreach (var argument in new[] { "download", "--path", outputFolder, "--scan-path", outputFolder, "url" })
            startInfo.ArgumentList.Add(argument);
        foreach (var url in urls) startInfo.ArgumentList.Add(url);

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
                throw new InvalidOperationException($"Could not start tiddl: {toolPath}");
        }
        catch (Exception ex) when (ex is Win32Exception or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"Could not start tiddl '{toolPath}': {ex.Message}", ex);
        }

        var errors = new Queue<string>();
        var lines = new Queue<string>();
        var stdoutTask = ReadLinesAsync(process.StandardOutput, lines, output, cancellationToken);
        var stderrTask = ReadLinesAsync(process.StandardError, errors, output, cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
            await Task.WhenAll(stdoutTask, stderrTask);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }

        if (process.ExitCode != 0)
        {
            var detail = errors.Count > 0 ? string.Join(" | ", errors) : string.Join(" | ", lines);
            throw new InvalidOperationException(
                $"tiddl exited with code {process.ExitCode}." + (detail.Length == 0 ? "" : $" {detail}"));
        }
    }

    public static string? FindExecutable(string? configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath)) return null;
        var name = configuredPath.Trim().Trim('"');
        if (Path.IsPathFullyQualified(name) || name.Contains(Path.DirectorySeparatorChar) ||
            name.Contains(Path.AltDirectorySeparatorChar))
        {
            var fullPath = Path.GetFullPath(name);
            return File.Exists(fullPath) ? fullPath : null;
        }

        var extensions = OperatingSystem.IsWindows() &&
            !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
            !name.EndsWith(".com", StringComparison.OrdinalIgnoreCase)
            ? new[] { ".exe", ".com" } : new[] { "" };
        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? "")
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(folder.Trim().Trim('"'), name + extension);
                if (File.Exists(candidate)) return Path.GetFullPath(candidate);
            }
        }
        return null;
    }

    private static string ResolveExecutable(string configuredPath)
    {
        if (FindExecutable(configuredPath) is { } executable) return executable;
        throw new FileNotFoundException(
            $"'{configuredPath}' was not found. Install tiddl (py -m pip install tiddl), restart the app, or enter its full executable path in settings.");
    }

    private static async Task ReadLinesAsync(StreamReader reader, Queue<string> tail,
        IProgress<string>? output, CancellationToken cancellationToken)
    {
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            line = line.Trim();
            if (line.Length > 300) line = line[..300] + "…";
            tail.Enqueue(line);
            if (tail.Count > 8) tail.Dequeue();
            output?.Report(line);
        }
    }
}
