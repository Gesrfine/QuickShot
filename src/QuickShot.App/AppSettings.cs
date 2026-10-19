using System.Security;
using System.Text.Json;

namespace QuickShot.App;

internal sealed record AppSettings
{
    public bool ShowNotifications { get; init; } = true;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static string SettingsPath => Path.Combine(LocalLog.DataDirectory, "settings.json");

    internal static AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return new AppSettings();
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), JsonOptions)
                ?? throw new JsonException("设置文件内容为空。");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException or JsonException or FormatException)
        {
            try { LocalLog.Write("settings", error); }
            catch (Exception loggingError) when (loggingError is IOException or UnauthorizedAccessException or SecurityException) { }
            return new AppSettings();
        }
    }

    internal void Save()
    {
        var settingsPath = SettingsPath;
        var directory = Path.GetDirectoryName(settingsPath)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".settings-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, this, JsonOptions);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, settingsPath, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception cleanupError) when (cleanupError is IOException or UnauthorizedAccessException or SecurityException) { }
        }
    }
}
