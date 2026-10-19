using Microsoft.Win32;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace QuickShot.Unregister.Tests;

internal static class Program
{
    private const string HostName = "com.quickshot.edge";
    private const string Origin = "chrome-extension://kgjahiekhopambbcmenggajjghlmbhel/";
    private const string RealHost = @"Software\Microsoft\Edge\NativeMessagingHosts\com.quickshot.edge";
    private const string RealRun = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic;
    private static readonly Type Paths = Assembly.Load("QuickShot").GetType("QuickShot.App.AppPaths", true)!;
    private static int _checks;

    private static int Main()
    {
        try { Run(); return 0; }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
    }

    private static void Run()
    {
        var repo = FindRepository();
        var artifacts = Path.GetFullPath(Path.Combine(repo, "artifacts"));
        var name = "root-layout-" + Guid.NewGuid().ToString("N");
        var fixture = Path.GetFullPath(Path.Combine(artifacts, name));
        var registry = @"Software\QuickShot\Tests\" + Guid.NewGuid().ToString("N");
        var registryEnvironment = @"HKCU:\" + registry;
        var oldRegistry = Environment.GetEnvironmentVariable("QUICKSHOT_TEST_REGISTRY_ROOT");
        var oldData = Environment.GetEnvironmentVariable("QUICKSHOT_DATA_DIR");
        var hostBefore = SnapshotRegistryValue(RealHost, "");
        var runBefore = SnapshotRegistryValue(RealRun, "QuickShot");
        if (Directory.Exists(fixture) || RegistryKeyExists(registry))
            throw new InvalidOperationException("Isolated test namespace already exists.");
        RequireExactFixture(fixture, artifacts, name);
        Directory.CreateDirectory(fixture);
        string? ownedManifest = null;
        string? expectedManifestContents = null;
        try
        {
            Environment.SetEnvironmentVariable("QUICKSHOT_TEST_REGISTRY_ROOT", registryEnvironment);
            var data = Path.Combine(fixture, "portable-data");
            Environment.SetEnvironmentVariable("QUICKSHOT_DATA_DIR", data);
            Directory.CreateDirectory(data);
            var keep = Path.Combine(data, "keep.txt");
            File.WriteAllText(keep, "Portable user data must survive unregister.");
            var dataBefore = SHA256.HashData(File.ReadAllBytes(keep));

            CheckLayouts(fixture, repo);
            var package = Path.GetFullPath(ReadPath("PackageDirectory"));
            var manifest = Path.GetFullPath(ReadPath("HostManifest"));
            var testProject = Path.Combine(repo, "tests", "QuickShot.Unregister.Tests");
            if (!IsWithin(package, testProject) && !IsWithin(package, fixture))
                throw new InvalidOperationException("AppPaths resolved outside this test project's output and fixture directories.");
            if (!SamePath(Path.GetDirectoryName(manifest), package) || Path.GetFileName(manifest) != "native-host.json")
                throw new InvalidOperationException("Unexpected native-host manifest path.");
            if (File.Exists(manifest))
                throw new InvalidOperationException("A native-host.json already exists at the test location; refusing to overwrite or delete it.");
            Check(SamePath(Path.GetFullPath(ReadPath("DataDirectory")), data), "isolated data override is used");
            var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Current process path is unavailable.");
            var hostKey = registry + @"\NativeHost";
            var runKey = registry + @"\Run";
            var startup = '"' + executable + "\" --background";
            ownedManifest = manifest;

            expectedManifestContents = WriteNewManifest(manifest, executable);
            using (var host = Registry.CurrentUser.CreateSubKey(hostKey)) host.SetValue("", manifest);
            using (var run = Registry.CurrentUser.CreateSubKey(runKey))
            {
                run.SetValue("QuickShot", startup);
                run.SetValue("OtherApplication", "leave-this-test-value");
            }
            InvokeUnregister();
            Check(!RegistryKeyExists(hostKey), "matching native-host registration is removed");
            Check(!SnapshotRegistryValue(runKey, "QuickShot").ValueExists, "matching QuickShot startup is removed");
            Check(ReadRegistryValue(runKey, "OtherApplication") as string == "leave-this-test-value", "unrelated startup value is preserved");
            Check(!File.Exists(manifest), "matching local native manifest is removed");
            Check(Directory.Exists(package) && File.Exists(Environment.ProcessPath), "unregister retains its program directory and executable");
            Check(File.Exists(keep) && SHA256.HashData(File.ReadAllBytes(keep)).SequenceEqual(dataBefore), "unregister preserves portable user data");
            expectedManifestContents = null;

            var foreign = Path.Combine(fixture, "other-package", "native-host.json");
            var foreignExecutable = Path.Combine(fixture, "other-package", "QuickShot.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(foreign)!);
            File.WriteAllText(foreign, "unrelated native-host test file");
            using (var host = Registry.CurrentUser.CreateSubKey(hostKey)) host.SetValue("", foreign);
            using (var run = Registry.CurrentUser.CreateSubKey(runKey)) run.SetValue("QuickShot", '"' + foreignExecutable + "\" --background");
            var foreignHostBefore = SnapshotRegistryValue(hostKey, "");
            var foreignRunBefore = SnapshotRegistryValue(runKey, "QuickShot");
            expectedManifestContents = WriteNewManifest(manifest, executable);
            InvokeUnregister();
            Check(SnapshotRegistryValue(hostKey, "") == foreignHostBefore, "registration for another package is preserved");
            Check(SnapshotRegistryValue(runKey, "QuickShot") == foreignRunBefore, "startup for another executable is preserved");
            Check(File.ReadAllText(foreign) == "unrelated native-host test file", "another package's native manifest is preserved");
            // Whether or not the active registration belongs to this copy, a safe local
            // cleanup may remove only a manifest whose contents identify this executable.
            if (File.Exists(manifest)) DeleteOwnedManifest(manifest, expectedManifestContents);
            expectedManifestContents = null;

            expectedManifestContents = WriteNewManifest(manifest, foreignExecutable);
            InvokeUnregister();
            Check(File.Exists(manifest) && File.ReadAllText(manifest) == expectedManifestContents, "local manifest pointing at another executable is preserved");
            Check(SnapshotRegistryValue(hostKey, "") == foreignHostBefore && SnapshotRegistryValue(runKey, "QuickShot") == foreignRunBefore,
                "mismatched manifest does not affect unrelated connection and startup");
            Check(File.Exists(keep) && SHA256.HashData(File.ReadAllBytes(keep)).SequenceEqual(dataBefore), "mismatched-manifest cleanup preserves user data");
            DeleteOwnedManifest(manifest, expectedManifestContents);
            expectedManifestContents = null;

            InvokeUnregister();
            Check(!File.Exists(manifest) && SnapshotRegistryValue(hostKey, "") == foreignHostBefore && SnapshotRegistryValue(runKey, "QuickShot") == foreignRunBefore,
                "repeating unregister with no local manifest is safe");
            Check(SnapshotRegistryValue(RealHost, "") == hostBefore && SnapshotRegistryValue(RealRun, "QuickShot") == runBefore,
                "real user's native-host and login-startup registrations remain unchanged");
            Console.WriteLine($"Root-layout and unregister checks passed: {_checks}.");
        }
        finally
        {
            try
            {
                if (ownedManifest is not null && File.Exists(ownedManifest))
                {
                    if (expectedManifestContents is null)
                        throw new InvalidOperationException("A test native manifest was unexpectedly recreated; retaining it for inspection.");
                    DeleteOwnedManifest(ownedManifest, expectedManifestContents);
                }
                if (!System.Text.RegularExpressions.Regex.IsMatch(registry, @"^Software\\QuickShot\\Tests\\[a-f0-9]{32}$"))
                    throw new InvalidOperationException("Unsafe isolated registry cleanup.");
                Registry.CurrentUser.DeleteSubKeyTree(registry, false);
                RequireExactFixture(fixture, artifacts, name);
                if (Directory.Exists(fixture))
                {
                    var entries = Directory.EnumerateFileSystemEntries(fixture, "*", SearchOption.AllDirectories);
                    if ((File.GetAttributes(fixture) & FileAttributes.ReparsePoint) != 0 || entries.Any(path => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0))
                        throw new InvalidOperationException("Test cleanup refuses a linked directory.");
                    Directory.Delete(fixture, true);
                }
            }
            finally
            {
                Environment.SetEnvironmentVariable("QUICKSHOT_TEST_REGISTRY_ROOT", oldRegistry);
                Environment.SetEnvironmentVariable("QUICKSHOT_DATA_DIR", oldData);
            }
            if (SnapshotRegistryValue(RealHost, "") != hostBefore || SnapshotRegistryValue(RealRun, "QuickShot") != runBefore)
                throw new InvalidOperationException("Real user registration changed during isolated tests.");
        }
    }

    private static void CheckLayouts(string fixture, string repo)
    {
        var driveRoot = Path.GetPathRoot(Path.GetFullPath(fixture))!;
        Check(SamePath(Resolve(driveRoot), driveRoot) && Path.IsPathFullyQualified(Resolve(driveRoot)), "drive root remains a fully qualified root instead of a drive-relative path");
        var root = Path.Combine(fixture, "current-root");
        Directory.CreateDirectory(Path.Combine(root, "extension"));
        File.Copy(Path.Combine(repo, "extension", "manifest.json"), Path.Combine(root, "extension", "manifest.json"));
        File.WriteAllText(Path.Combine(root, "QuickShot.exe"), "NOT EXECUTABLE");
        Check(SamePath(Resolve(root), root), "root executable layout resolves its own directory");
        Check(SamePath(Resolve(root + Path.DirectorySeparatorChar), root), "root layout accepts a trailing separator");

        var legacy = Path.Combine(fixture, "legacy-root");
        var legacyApp = Path.Combine(legacy, "app");
        Directory.CreateDirectory(Path.Combine(legacy, "extension"));
        File.Copy(Path.Combine(repo, "extension", "manifest.json"), Path.Combine(legacy, "extension", "manifest.json"));
        Directory.CreateDirectory(legacyApp);
        File.WriteAllText(Path.Combine(legacyApp, "QuickShot.exe"), "NOT EXECUTABLE");
        Check(SamePath(Resolve(legacyApp), legacy), "legacy app subdirectory resolves the original package root");
        Check(SamePath(Resolve(legacyApp + Path.DirectorySeparatorChar), legacy), "legacy layout accepts a trailing separator");

        var namedApp = Path.Combine(fixture, "app");
        Directory.CreateDirectory(Path.Combine(namedApp, "extension"));
        File.Copy(Path.Combine(repo, "extension", "manifest.json"), Path.Combine(namedApp, "extension", "manifest.json"));
        File.WriteAllText(Path.Combine(namedApp, "QuickShot.exe"), "NOT EXECUTABLE");
        Check(SamePath(Resolve(namedApp), namedApp), "a modern package directory named app is not mistaken for the legacy layout");
    }

    private static string Resolve(string directory)
    {
        var method = Paths.GetMethod("ResolvePackageDirectory", Static, [typeof(string)])
            ?? throw new MissingMethodException("AppPaths.ResolvePackageDirectory(string) has not been implemented.");
        return Path.GetFullPath((string)method.Invoke(null, [directory])!);
    }

    private static string ReadPath(string property) => (string)(Paths.GetProperty(property, Static)?.GetValue(null)
        ?? throw new MissingMemberException("AppPaths." + property));

    private static void InvokeUnregister()
    {
        var method = Paths.GetMethod("UnregisterConnection", Static, Type.EmptyTypes)
            ?? throw new MissingMethodException("AppPaths.UnregisterConnection() has not been implemented.");
        try { method.Invoke(null, null); }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        { ExceptionDispatchInfo.Capture(exception.InnerException).Throw(); }
    }

    private static string WriteNewManifest(string path, string executable)
    {
        var contents = JsonSerializer.Serialize(new { name = HostName, path = executable, type = "stdio", allowed_origins = new[] { Origin } });
        // Never overwrite an existing manifest, even in this test project's own output.
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(contents);
        return contents;
    }

    private static void DeleteOwnedManifest(string path, string contents)
    {
        if (File.ReadAllText(path) != contents) throw new InvalidOperationException("The test manifest changed externally; refusing cleanup.");
        File.Delete(path);
    }

    private sealed record RegistryValue(bool KeyExists, bool ValueExists, string Kind, string Contents);

    private static RegistryValue SnapshotRegistryValue(string path, string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(path);
        if (key is null) return new(false, false, "", "");
        if (!key.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase)) return new(true, false, "", "");
        return new(true, true, key.GetValueKind(name).ToString(),
            JsonSerializer.Serialize(key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames)));
    }

    private static object? ReadRegistryValue(string path, string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(path);
        return key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
    }

    private static bool RegistryKeyExists(string path)
    {
        using var key = Registry.CurrentUser.OpenSubKey(path);
        return key is not null;
    }

    private static bool IsWithin(string path, string parent) => path.StartsWith(Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static bool SamePath(string? first, string? second) => StringComparer.OrdinalIgnoreCase.Equals(first, second);

    private static void RequireExactFixture(string path, string parent, string name)
    {
        if (!SamePath(Path.GetDirectoryName(path), parent) || Path.GetFileName(path) != name ||
            !System.Text.RegularExpressions.Regex.IsMatch(name, "^root-layout-[a-f0-9]{32}$"))
            throw new InvalidOperationException("Unsafe root-layout test fixture cleanup.");
    }

    private static string FindRepository()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "src", "QuickShot.App", "QuickShot.App.csproj")) && File.Exists(Path.Combine(directory.FullName, "scripts", "build.ps1")))
                return directory.FullName;
        throw new DirectoryNotFoundException("Could not find the owning workspace from this test executable.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        _checks++;
        Console.WriteLine("PASS: " + message);
    }
}
