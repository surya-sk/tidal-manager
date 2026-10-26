using System.Text.Json;
using TidalManager.Queue;

namespace TidalManager.App;

internal sealed class DownloadSettingsStore(string path)
{
    private const string DefaultTool = "tiddl";
    private static string DefaultOutputFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "tiddl");

    public ExternalDownloadOptions Load()
    {
        ExternalDownloadOptions? options = null;
        try
        {
            if (File.Exists(path)) options = JsonSerializer.Deserialize<ExternalDownloadOptions>(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or JsonException) { }
        if (options is null) return new(DefaultTool, DefaultOutputFolder);
        var tool = options.ToolPath;
        var oldName = string.IsNullOrWhiteSpace(tool) ? "" : Path.GetFileNameWithoutExtension(tool);
        if (string.IsNullOrWhiteSpace(tool) ||
            oldName.Equals("tdl", StringComparison.OrdinalIgnoreCase) ||
            oldName.Equals("tidal-dl-ng", StringComparison.OrdinalIgnoreCase))
            tool = DefaultTool;
        return options with { ToolPath = tool };
    }

    public void Save(ExternalDownloadOptions options)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(options));
    }
}
