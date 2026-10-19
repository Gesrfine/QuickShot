using Microsoft.Win32;
using QuickShot.Core;
using System.Text;
using System.Text.Json;

namespace QuickShot.App;

internal static class AppPaths
{
    private static readonly string PackageRoot = ResolvePackageDirectory(AppContext.BaseDirectory);
    internal static string PackageDirectory => PackageRoot;
    internal static bool Portable => File.Exists(Path.Combine(PackageDirectory, "portable.flag")) || !File.Exists(Path.Combine(PackageDirectory, "quickshot.install.json"));

    internal static string ResolvePackageDirectory(string baseDirectory)
    {
        var directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(baseDirectory));
        bool IsPackage(string path) => File.Exists(Path.Combine(path, "extension", "manifest.json"))
            || File.Exists(Path.Combine(path, "portable.flag")) || File.Exists(Path.Combine(path, "quickshot.install.json"));
        if (IsPackage(directory)) return directory;
        var parent = Directory.GetParent(directory);
        if (Path.GetFileName(directory).Equals("app", StringComparison.OrdinalIgnoreCase) && parent != null && IsPackage(parent.FullName))
            return parent.FullName;
        return directory;
    }
    internal static string DataDirectory => Environment.GetEnvironmentVariable("QUICKSHOT_DATA_DIR")
        ?? (Portable ? Path.Combine(PackageDirectory, "data") : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QuickShot"));
    internal static string HostManifest => Path.Combine(PackageDirectory, "native-host.json");
    internal const string HostRegistryKey = @"Software\Microsoft\Edge\NativeMessagingHosts\com.quickshot.edge";

    internal static string? RegisterConnection()
    {
        if (Environment.GetEnvironmentVariable("QUICKSHOT_NO_REGISTER") == "1") return null;
        try
        {
            var manifest = JsonSerializer.Serialize(new
            {
                name = Protocol.HostName, description = "QuickShot Edge native bridge", path = Environment.ProcessPath!, type = "stdio",
                allowed_origins = new[] { $"chrome-extension://{Protocol.ExtensionId}/" }
            });
            if (!File.Exists(HostManifest) || File.ReadAllText(HostManifest) != manifest)
                File.WriteAllText(HostManifest, manifest, new UTF8Encoding(false));
            using var key = Registry.CurrentUser.CreateSubKey(HostRegistryKey);
            if (key.GetValue("") as string != HostManifest) key.SetValue("", HostManifest);
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            LocalLog.Write("register", e);
            return "浏览器连接注册失败。请把文件解压到可写目录，再手动启动快投。";
        }
    }

    internal static void UnregisterConnection()
    {
        var hostKeyPath = HostRegistryKey;
        var runKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        if (Environment.GetEnvironmentVariable("QUICKSHOT_TEST_REGISTRY_ROOT") is { Length: > 0 } testRoot)
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(testRoot, @"^HKCU:\\Software\\QuickShot\\Tests\\[a-zA-Z0-9-]{10,64}$"))
                throw new IOException("无效的测试注册表路径。");
            hostKeyPath = testRoot[6..] + @"\NativeHost";
            runKeyPath = testRoot[6..] + @"\Run";
        }
        var executable = Environment.ProcessPath!;
        var ownedManifest = false;
        if (File.Exists(HostManifest))
        {
            try
            {
                using var manifest = JsonDocument.Parse(File.ReadAllText(HostManifest));
                var root = manifest.RootElement;
                ownedManifest = root.TryGetProperty("name", out var name) && name.GetString() == Protocol.HostName
                    && root.TryGetProperty("type", out var kind) && kind.GetString() == "stdio"
                    && root.TryGetProperty("path", out var path) && string.Equals(path.GetString(), executable, StringComparison.OrdinalIgnoreCase)
                    && root.TryGetProperty("allowed_origins", out var origins) && origins.GetArrayLength() == 1
                    && origins[0].GetString() == $"chrome-extension://{Protocol.ExtensionId}/";
            }
            catch (Exception error) when (error is JsonException or InvalidOperationException) { /* An unrelated or invalid manifest is preserved. */ }
        }
        var removeEmptyHost = false;
        using (var host = Registry.CurrentUser.OpenSubKey(hostKeyPath, writable: true))
        {
            if (host?.GetValue("") is string registered && string.Equals(registered, HostManifest, StringComparison.OrdinalIgnoreCase))
            {
                host.DeleteValue("", throwOnMissingValue: false);
                removeEmptyHost = host.ValueCount == 0 && host.SubKeyCount == 0;
            }
        }
        if (removeEmptyHost) Registry.CurrentUser.DeleteSubKey(hostKeyPath, throwOnMissingSubKey: false);
        using (var run = Registry.CurrentUser.OpenSubKey(runKeyPath, writable: true))
        {
            if (run?.GetValue("QuickShot") is string startup && string.Equals(startup, $"\"{executable}\" --background", StringComparison.OrdinalIgnoreCase))
                run.DeleteValue("QuickShot", throwOnMissingValue: false);
        }
        if (ownedManifest) File.Delete(HostManifest);
    }

    internal static string DefaultInstallationDirectory()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(HostRegistryKey);
            if (key?.GetValue("") is string manifest && File.Exists(manifest))
            {
                var root = Path.GetDirectoryName(manifest)!;
                if (File.Exists(Path.Combine(root, "quickshot.install.json"))) return root;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "QuickShot");
    }
}
