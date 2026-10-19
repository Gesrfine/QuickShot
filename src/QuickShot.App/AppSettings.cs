using System.Security;
using System.Text.Json;
using QuickShot.Core;
using Shortcut = QuickShot.Core.Shortcut;

namespace QuickShot.App;

internal sealed record AppSettings
{
    public bool ShowNotifications { get; init; } = true;
    public string CaptureShortcut { get; init; } = "Alt+Shift+D";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static string SettingsPath => Path.Combine(LocalLog.DataDirectory, "settings.json");

    internal static Shortcut ParseCaptureShortcut(string text)
    {
        var shortcut = Shortcut.Parse(text);
        if (shortcut.Modifiers == ShortcutModifiers.None
            && shortcut.VirtualKey != 0x2C && shortcut.VirtualKey is not (>= 0x70 and <= 0x7B))
            throw new FormatException("截图快捷键请使用修饰键加一个按键，或单独使用 PrintScreen、F1–F12。");
        return shortcut;
    }

    internal static AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return new AppSettings();
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), JsonOptions)
                ?? throw new JsonException("设置文件内容为空。");
            return settings with { CaptureShortcut = ParseCaptureShortcut(settings.CaptureShortcut).Text };
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
        var validated = this with { CaptureShortcut = ParseCaptureShortcut(CaptureShortcut).Text };
        var settingsPath = SettingsPath;
        var directory = Path.GetDirectoryName(settingsPath)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".settings-{Guid.NewGuid():N}.tmp");
        try
        {
            // Finish and flush the new file before replacing the existing settings in the same directory.
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, validated, JsonOptions);
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
