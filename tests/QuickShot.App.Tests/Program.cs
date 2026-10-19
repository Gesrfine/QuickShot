using System.Reflection;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Text;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.ExceptionServices;

internal static class Program
{
    private delegate bool Enumerator(nint window, nint parameter);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(nint window, Enumerator callback, nint parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint window, StringBuilder text, int length);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint window, StringBuilder text, int length);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint thread, ref GuiInfo info);
    [StructLayout(LayoutKind.Sequential)] private struct GuiInfo { internal uint Size, Flags; internal nint Active, Focus, Capture, Menu, Move, Caret; internal Rectangle Rect; }
    private static string Class(nint window) { var name = new StringBuilder(256); GetClassName(window, name, name.Capacity); return name.ToString(); }
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--installer-ui-check")
        {
            ApplicationConfiguration.Initialize();
            return CheckInstallerUi(args[1]);
        }
        if (args.Length != 2 || !args[0].StartsWith("[QuickShot:", StringComparison.Ordinal)) return 2;
        ApplicationConfiguration.Initialize();
        using var dispatcher = new Control(); _ = dispatcher.Handle;
        Application.DoEvents();
        var methods = Assembly.Load("QuickShot").GetType("QuickShot.App.NativeMethods", throwOnError: true)!;
        var activate = methods.GetMethod("TryActivateEdgeWindow", BindingFlags.Static | BindingFlags.NonPublic)!;
        var foreground = methods.GetMethod("IsExpectedEdgeForeground", BindingFlags.Static | BindingFlags.NonPublic)!;
        var activated = false;
        var contentDeadline = DateTime.UtcNow.AddSeconds(4);
        while (!activated && DateTime.UtcNow < contentDeadline)
        {
            activated = (bool)activate.Invoke(null, [args[0]])!;
            if (!activated) { Application.DoEvents(); Thread.Sleep(100); }
        }
        var contentFocus = Assembly.Load("QuickShot").GetType("QuickShot.App.BrowserFocus", throwOnError: true)!.GetMethod("TryFocusContent", BindingFlags.Static | BindingFlags.NonPublic)!;
        var expected = (bool)foreground.Invoke(null, [args[0]])!;
        var children = new List<object>();
        EnumChildWindows(GetForegroundWindow(), (window, _) => { children.Add(new { name = Class(window), visible = IsWindowVisible(window) }); return true; }, 0);
        var gui = new GuiInfo { Size = (uint)Marshal.SizeOf<GuiInfo>() }; GetGUIThreadInfo(0, ref gui);
        File.WriteAllText(args[1], JsonSerializer.Serialize(new { activated, expected, children, focusedClass = Class(gui.Focus), gui.Flags }));
        var deadline = DateTime.UtcNow.AddSeconds(10);
        var probed = false;
        var focusProbed = false;
        var activationProbed = false;
        while (!File.Exists(args[1] + ".release") && DateTime.UtcNow < deadline)
        {
            if (!activationProbed && File.Exists(args[1] + ".activate"))
            {
                activationProbed = true;
                activated = (bool)activate.Invoke(null, [args[0]])!;
                expected = (bool)foreground.Invoke(null, [args[0]])!;
                File.WriteAllText(args[1] + ".activation-result", JsonSerializer.Serialize(new { activated, expected }));
            }
            // Match the production protocol: Edge's public focus APIs run first;
            // UI Automation is a fallback only when the page asks for content focus.
            if (!focusProbed && File.Exists(args[1] + ".focus"))
            {
                focusProbed = true;
                using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(4));
                var errors = new ConcurrentQueue<object>();
                var watch = Stopwatch.StartNew();
                var attempts = new List<object>();
                var focusCompleted = true;
                var contentFocused = false;
                while (!cancellation.IsCancellationRequested && watch.Elapsed < TimeSpan.FromSeconds(4))
                {
                    var number = attempts.Count + 1;
                    var startedAtMilliseconds = watch.ElapsedMilliseconds;
                    var attemptWatch = Stopwatch.StartNew();
                    var focusTask = Task.Run(() =>
                    {
                        var worker = Environment.CurrentManagedThreadId;
                        void FirstChance(object? sender, FirstChanceExceptionEventArgs raised)
                        {
                            if (Environment.CurrentManagedThreadId == worker && errors.Count < 16
                                && raised.Exception is COMException or InvalidCastException or OperationCanceledException)
                                errors.Enqueue(FocusError($"production-attempt-{number}-first-chance", raised.Exception));
                        }
                        AppDomain.CurrentDomain.FirstChanceException += FirstChance;
                        try { return (bool)contentFocus.Invoke(null, [args[0], cancellation.Token])!; }
                        catch (Exception error) { errors.Enqueue(FocusError($"production-attempt-{number}-invoke", error)); return false; }
                        finally { AppDomain.CurrentDomain.FirstChanceException -= FirstChance; }
                    });
                    var remaining = TimeSpan.FromSeconds(4) - watch.Elapsed;
                    focusCompleted = remaining > TimeSpan.Zero && focusTask.Wait(remaining);
                    var result = focusCompleted && focusTask.Result;
                    attempts.Add(new
                    {
                        number, startedAtMilliseconds, elapsedMilliseconds = attemptWatch.ElapsedMilliseconds,
                        completed = focusCompleted, result, expected = (bool)foreground.Invoke(null, [args[0]])!
                    });
                    if (result && !cancellation.IsCancellationRequested && watch.Elapsed < TimeSpan.FromSeconds(4))
                    { contentFocused = true; break; }
                    if (!focusCompleted || cancellation.IsCancellationRequested) break;
                    // The tray retries completed false attempts on its 100 ms activity tick,
                    // with one shared four-second deadline and unchanged production guards.
                    var delay = Math.Min(100, Math.Max(0, (TimeSpan.FromSeconds(4) - watch.Elapsed).TotalMilliseconds));
                    if (delay > 0) Thread.Sleep((int)Math.Ceiling(delay));
                }
                var productionMilliseconds = watch.ElapsedMilliseconds;
                cancellation.Cancel();
                var stillExpected = (bool)foreground.Invoke(null, [args[0]])!;
                object? uiAutomation = null;
                if (!contentFocused)
                {
                    // Observe only the exact marked test window, after the production attempt.
                    // This probe never focuses an element or inspects another foreground window.
                    var scan = Task.Run(() => ScanOwnedDocuments(args[0], foreground));
                    uiAutomation = scan.Wait(TimeSpan.FromMilliseconds(600)) ? scan.Result : new { timedOut = true };
                }
                File.WriteAllText(args[1] + ".focus-result", JsonSerializer.Serialize(new
                {
                    contentFocused, expected = stillExpected, focusCompleted, productionMilliseconds, attempts,
                    elapsedMilliseconds = watch.ElapsedMilliseconds, errors = errors.ToArray(), uiAutomation
                }));
            }
            if (!probed && File.Exists(args[1] + ".paste"))
            {
                probed = true;
                var safe = (bool)foreground.Invoke(null, [args[0]])! && !(bool)methods.GetProperty("HasModifiers", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
                var dispatched = safe && (bool)methods.GetMethod("Paste", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null)!;
                File.WriteAllText(args[1] + ".paste-result", JsonSerializer.Serialize(new { dispatched }));
            }
            Application.DoEvents(); Thread.Sleep(20);
        }
        return activated && expected ? 0 : 1;
    }

    private static object FocusError(string stage, Exception error) => new
    {
        stage, type = error.GetType().FullName, hresult = $"0x{unchecked((uint)error.HResult):X8}",
        error.Message, error.StackTrace,
        inner = error.InnerException is { } inner ? new { type = inner.GetType().FullName, hresult = $"0x{unchecked((uint)inner.HResult):X8}", inner.Message, inner.StackTrace } : null
    };

    private static object ScanOwnedDocuments(string token, MethodInfo expectedForeground)
    {
        IProbeAutomation? automation = null; IProbeElement? root = null; IProbeElements? elements = null; nint condition = 0;
        var documents = new List<Dictionary<string, object?>>();
        var errors = new List<object>();
        var stage = "scope-check";
        var count = -1;
        var matches = 0;
        var window = nint.Zero;
        var title = "";
        var watch = Stopwatch.StartNew();
        try
        {
            if (!(bool)expectedForeground.Invoke(null, [token])!)
                return new { skipped = true, reason = "Marked test Edge window is no longer foreground." };
            window = GetForegroundWindow();
            var text = new StringBuilder(2048); GetWindowText(window, text, text.Capacity); title = text.ToString();
            if (!title.Contains(token, StringComparison.Ordinal))
                return new { skipped = true, reason = "Foreground changed before the owned-window snapshot." };
            stage = "create-automation";
            automation = (IProbeAutomation)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("ff48dba4-60ef-4201-aa87-54103eef594e"), throwOnError: true)!)!;
            stage = "element-from-owned-window";
            automation.ElementFromHandle(window, out root);
            stage = "document-condition";
            automation.CreatePropertyCondition(30003, 50030, out condition);
            stage = "find-documents";
            root.FindAll(4, condition, out elements);
            stage = "document-count";
            elements.Length(out count);
            for (var index = 0; index < Math.Min(count, 100); index++)
            {
                IProbeElement? element = null;
                try
                {
                    stage = $"document[{index}]";
                    elements.GetElement(index, out element);
                    object? Read(int property, string name)
                    {
                        try
                        {
                            element.GetCurrentPropertyValue(property, out var value);
                            if (value is null or string or bool or int or double) return value;
                            var kind = value.GetType().FullName;
                            ReleaseProbe(value);
                            return kind;
                        }
                        catch (Exception error) { errors.Add(FocusError($"document[{index}].{name}", error)); return null; }
                    }
                    var name = Read(30005, "name");
                    var offscreen = Read(30022, "offscreen");
                    var match = name is string documentName && documentName.StartsWith(token + " ", StringComparison.Ordinal) && offscreen is false;
                    if (match) matches++;
                    documents.Add(new Dictionary<string, object?>
                    {
                        ["index"] = index, ["name"] = name, ["offscreen"] = offscreen, ["matchesProductionRule"] = match,
                        ["hasKeyboardFocus"] = Read(30008, "hasKeyboardFocus"), ["keyboardFocusable"] = Read(30009, "keyboardFocusable"),
                        ["enabled"] = Read(30010, "enabled"), ["nativeWindow"] = Read(30020, "nativeWindow"), ["processId"] = Read(30002, "processId")
                    });
                }
                catch (Exception error) { errors.Add(FocusError(stage, error)); }
                finally { ReleaseProbe(element); }
            }
            stage = "completed";
        }
        catch (Exception error) { errors.Add(FocusError(stage, error)); }
        finally
        {
            ReleaseProbe(elements); ReleaseProbe(root);
            if (condition != 0) Marshal.Release(condition);
            ReleaseProbe(automation);
        }
        return new
        {
            stage, window = window.ToString(), title, count, matches, truncated = count > 100,
            documents, errors, elapsedMilliseconds = watch.ElapsedMilliseconds
        };
    }

    private static void ReleaseProbe(object? value) { if (value != null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value); }

    // Same Windows SDK slots as production; these interfaces are used only for observation.
    [ComImport, Guid("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IProbeAutomation
    {
        void CompareElements(); void CompareRuntimeIds(); void GetRootElement();
        void ElementFromHandle(nint window, out IProbeElement element);
        void ElementFromPoint(); void GetFocusedElement(); void GetRootElementBuildCache();
        void ElementFromHandleBuildCache(); void ElementFromPointBuildCache(); void GetFocusedElementBuildCache();
        void CreateTreeWalker(); void ControlViewWalker(); void ContentViewWalker(); void RawViewWalker();
        void RawViewCondition(); void ControlViewCondition(); void ContentViewCondition();
        void CreateCacheRequest(); void CreateTrueCondition(); void CreateFalseCondition();
        void CreatePropertyCondition(int property, [MarshalAs(UnmanagedType.Struct)] object value, out nint condition);
    }
    [ComImport, Guid("d22108aa-8ac5-49a5-837b-37bbb3d7591e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IProbeElement
    {
        void SetFocus(); void GetRuntimeId(); void FindFirst();
        void FindAll(int scope, nint condition, out IProbeElements elements);
        void FindFirstBuildCache(); void FindAllBuildCache(); void BuildUpdatedCache();
        void GetCurrentPropertyValue(int property, [MarshalAs(UnmanagedType.Struct)] out object value);
    }
    [ComImport, Guid("14314595-b4bc-4055-95f2-58f2e42c9855"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IProbeElements
    {
        void Length(out int length);
        void GetElement(int index, out IProbeElement element);
    }

    private static int CheckInstallerUi(string resultPath)
    {
        var checks = new Dictionary<string, bool>
        {
            ["temporaryScriptCreated"] = false,
            ["controlsDisabledWhileInstalling"] = false,
            ["closeBlockedWhileInstalling"] = false,
            ["installationFinished"] = false,
            ["installationSucceeded"] = false,
            ["workerWorkingDirectoryIsWindows"] = false,
            ["workerWorkingDirectoryIsNonTemporary"] = false,
            ["workerTargetMatches"] = false,
            ["workerInstallerProcessMatches"] = false,
            ["workerLaunchSuppressed"] = false,
            ["closedAfterCompletion"] = false,
            ["safeCleanupCompleted"] = false
        };
        string? artifactsRoot = null, testDirectory = null, scriptPath = null;
        var scriptText = "";
        var scriptCreated = false;
        var testDirectoryCreated = false;
        Form? form = null;
        try
        {
            var workspace = FindWorkspaceRoot();
            artifactsRoot = Path.GetFullPath(Path.Combine(workspace, "artifacts"));
            testDirectory = Path.GetFullPath(Path.Combine(artifactsRoot, "installer-ui-check-" + Guid.NewGuid().ToString("N")));
            var assembly = Assembly.Load("QuickShot");
            var paths = assembly.GetType("QuickShot.App.AppPaths", throwOnError: true)!;
            var packageDirectory = (string)paths.GetProperty("PackageDirectory", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            var testBinDirectory = Path.Combine(workspace, "tests", "QuickShot.App.Tests", "bin");
            if (!IsWithinDirectory(packageDirectory, testBinDirectory)) throw new IOException("The installer check must run from the test project's bin directory.");
            scriptPath = Path.Combine(packageDirectory, "Install.ps1");
            if (File.Exists(scriptPath)) throw new IOException("Refusing to replace an existing Install.ps1.");
            if (Directory.Exists(testDirectory)) throw new IOException("The dedicated test directory already exists.");
            Directory.CreateDirectory(testDirectory);
            testDirectoryCreated = true;
            var escapedDirectory = testDirectory.Replace("'", "''", StringComparison.Ordinal);
            scriptText = $$"""
                param([string]$InstallDirectory, [int]$InstallerProcessId, [switch]$NoLaunch)
                $ErrorActionPreference = 'Stop'
                if ([IO.Path]::GetFullPath($InstallDirectory) -ne '{{escapedDirectory}}' -or $InstallerProcessId -ne {{Environment.ProcessId}} -or -not $NoLaunch) { throw 'Unexpected installer test arguments.' }
                Start-Sleep -Milliseconds 600
                [pscustomobject]@{
                    workingDirectory = [Environment]::CurrentDirectory
                    installDirectory = $InstallDirectory
                    installerProcessId = $InstallerProcessId
                    noLaunch = [bool]$NoLaunch
                } | ConvertTo-Json -Compress | Set-Content -LiteralPath (Join-Path $InstallDirectory 'worker.json') -Encoding UTF8
                """;
            using (var stream = new FileStream(scriptPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            {
                scriptCreated = true;
                using var writer = new StreamWriter(stream, new UTF8Encoding(false));
                writer.Write(scriptText);
            }
            checks["temporaryScriptCreated"] = true;
            var formType = assembly.GetType("QuickShot.App.InstallationForm", throwOnError: true)!;
            form = (Form)Activator.CreateInstance(formType, BindingFlags.Instance | BindingFlags.NonPublic, null, [true], null)!;
            form.Opacity = 0;
            form.ShowInTaskbar = false;
            InstallerField<CheckBox>(form, "_launch").Checked = false;
            InstallerField<TextBox>(form, "_directory").Text = testDirectory;
            form.Show();
            Application.DoEvents();
            var installation = (Task)formType.GetMethod("InstallAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, null)!;
            if (installation.IsCompleted) throw new InvalidOperationException("The installer finished before its busy state could be checked.");
            checks["controlsDisabledWhileInstalling"] = InstallerFlag(form, "_installing")
                && new[] { "_install", "_directory", "_browse", "_cancel", "_launch" }.All(name => !InstallerField<Control>(form, name).Enabled);
            form.Close();
            Application.DoEvents();
            checks["closeBlockedWhileInstalling"] = !form.IsDisposed && form.Visible && InstallerFlag(form, "_installing");
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!installation.IsCompleted && DateTime.UtcNow < deadline)
            {
                Application.DoEvents();
                Thread.Sleep(10);
            }
            if (!installation.IsCompleted) throw new TimeoutException("The installer check did not finish within ten seconds.");
            installation.GetAwaiter().GetResult();
            checks["installationFinished"] = !InstallerFlag(form, "_installing");
            checks["installationSucceeded"] = InstallerFlag(form, "_completed");
            using (var worker = JsonDocument.Parse(File.ReadAllText(Path.Combine(testDirectory, "worker.json"))))
            {
                var record = worker.RootElement;
                var workingDirectory = record.GetProperty("workingDirectory").GetString() ?? "";
                checks["workerWorkingDirectoryIsWindows"] = SameDirectory(workingDirectory, Environment.GetFolderPath(Environment.SpecialFolder.Windows));
                checks["workerWorkingDirectoryIsNonTemporary"] = !IsWithinDirectory(workingDirectory, Path.GetTempPath());
                checks["workerTargetMatches"] = SameDirectory(record.GetProperty("installDirectory").GetString() ?? "", testDirectory);
                checks["workerInstallerProcessMatches"] = record.GetProperty("installerProcessId").GetInt32() == Environment.ProcessId;
                checks["workerLaunchSuppressed"] = record.GetProperty("noLaunch").GetBoolean();
            }
            form.Close();
            Application.DoEvents();
            checks["closedAfterCompletion"] = form.IsDisposed;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("Installer UI check failed: " + error.Message);
        }
        finally
        {
            form?.Dispose();
            try
            {
                if (scriptCreated && scriptPath is not null && File.Exists(scriptPath))
                {
                    if (File.ReadAllText(scriptPath) != scriptText) throw new IOException("The temporary installer script changed; refusing to delete it.");
                    File.Delete(scriptPath);
                }
                if (testDirectoryCreated && testDirectory is not null && Directory.Exists(testDirectory))
                {
                    if (artifactsRoot is null || !IsWithinDirectory(testDirectory, artifactsRoot)
                        || !Path.GetFileName(testDirectory).StartsWith("installer-ui-check-", StringComparison.Ordinal)
                        || !Guid.TryParseExact(Path.GetFileName(testDirectory)["installer-ui-check-".Length..], "N", out _))
                        throw new IOException("Refusing to delete a directory outside the dedicated artifacts test location.");
                    Directory.Delete(Path.GetFullPath(testDirectory), recursive: true);
                }
                checks["safeCleanupCompleted"] = true;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine("Installer UI check cleanup failed: " + error.Message);
            }
        }
        checks["success"] = checks.Values.All(value => value);
        File.WriteAllText(resultPath, JsonSerializer.Serialize(checks));
        return checks["success"] ? 0 : 1;
    }

    private static string FindWorkspaceRoot()
    {
        for (DirectoryInfo? candidate = new(AppContext.BaseDirectory); candidate is not null; candidate = candidate.Parent)
            if (File.Exists(Path.Combine(candidate.FullName, "tests", "QuickShot.App.Tests", "QuickShot.App.Tests.csproj"))
                && File.Exists(Path.Combine(candidate.FullName, "src", "QuickShot.App", "QuickShot.App.csproj"))) return candidate.FullName;
        throw new DirectoryNotFoundException("Could not locate the test workspace.");
    }

    private static T InstallerField<T>(Form form, string name) where T : class =>
        form.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(form) as T
        ?? throw new MissingFieldException(form.GetType().FullName, name);

    private static bool InstallerFlag(Form form, string name) =>
        form.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(form) is true;

    private static bool SameDirectory(string left, string right) =>
        string.Equals(Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);

    private static bool IsWithinDirectory(string candidate, string parent) =>
        Path.GetFullPath(candidate).StartsWith(Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || SameDirectory(candidate, parent);
}
