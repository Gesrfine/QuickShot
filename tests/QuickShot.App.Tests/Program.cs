using System.Reflection;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Text;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using QuickShot.Core;
using Shortcut = QuickShot.Core.Shortcut;

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
        if (args.Length == 2 && args[0] == "--edge-launch") return LaunchOwnedEdge(args[1]);
        if (args.Length is 3 or 4 && args[0] == "--cover")
        {
            ApplicationConfiguration.Initialize();
            using var initialClaim = args.Length == 4 ? AcquireFromHotkey(int.Parse(args[3])) : null;
            using var cover = new Form { Text = "QuickShot owned " + args[1] + " fixture", KeyPreview = true };
            if (args[1] is "fullscreen" or "topmost")
            {
                cover.FormBorderStyle = FormBorderStyle.None;
                cover.Bounds = Screen.PrimaryScreen!.Bounds;
                cover.TopMost = args[1] == "topmost";
            }
            else cover.WindowState = FormWindowState.Maximized;
            cover.Show(); cover.Activate(); Application.DoEvents();
            Assembly.Load("QuickShot").GetType("QuickShot.App.NativeMethods", true)!.GetMethod("TryActivateWindow", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [cover.Handle]);
            using var timer = new System.Windows.Forms.Timer { Interval = 100 };
            var expires = DateTime.UtcNow.AddSeconds(45);
            var returning = false;
            timer.Tick += (_, _) =>
            {
                if (File.Exists(args[2] + ".release") || DateTime.UtcNow >= expires) cover.Close();
                else
                {
                    if (!returning && File.Exists(args[2] + ".activate"))
                    {
                        returning = true;
                        using var returnedClaim = args.Length == 4 ? AcquireFromHotkey(int.Parse(args[3])) : null;
                        Assembly.Load("QuickShot").GetType("QuickShot.App.NativeMethods", true)!.GetMethod("TryActivateWindow", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [cover.Handle]);
                    }
                    File.WriteAllText(args[2], JsonSerializer.Serialize(new { foreground = GetForegroundWindow() == cover.Handle, process = Environment.ProcessId }));
                }
            };
            timer.Start(); Application.Run(cover); return 0;
        }
        if (args.Length == 2 && args[0] == "--unresponsive")
        {
            ApplicationConfiguration.Initialize();
            using var blocked = new Form { Text = "QuickShot owned unresponsive fixture", ShowInTaskbar = false };
            blocked.Show(); Application.DoEvents();
            File.WriteAllText(args[1], blocked.Handle.ToString());
            Thread.Sleep(10000);
            return 0;
        }
        if (args.Length == 3 && args[0] == "--activation-bound")
        {
            var activationMethods = Assembly.Load("QuickShot").GetType("QuickShot.App.NativeMethods", true)!;
            var watch = Stopwatch.StartNew();
            var activationResult = (bool)activationMethods.GetMethod("TryActivateWindow", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [(nint)long.Parse(args[1])])!;
            File.WriteAllText(args[2], JsonSerializer.Serialize(new { activated = activationResult, elapsedMilliseconds = watch.ElapsedMilliseconds }));
            return watch.ElapsedMilliseconds < 800 ? 0 : 1;
        }
        if (args.Length == 2 && args[0] == "--installer-ui-check")
        {
            ApplicationConfiguration.Initialize();
            return CheckInstallerUi(args[1]);
        }
        if (args.Length is < 2 or > 3 || !args[0].StartsWith("[QuickShot:", StringComparison.Ordinal)) return 2;
        ApplicationConfiguration.Initialize();
        using var dispatcher = new Control(); _ = dispatcher.Handle;
        Application.DoEvents();
        var methods = Assembly.Load("QuickShot").GetType("QuickShot.App.NativeMethods", throwOnError: true)!;
        var activate = methods.GetMethod("TryActivateEdgeWindow", BindingFlags.Static | BindingFlags.NonPublic)!;
        var foreground = methods.GetMethod("IsExpectedEdgeForeground", BindingFlags.Static | BindingFlags.NonPublic)!;
        var visibility = Assembly.Load("QuickShot").GetType("QuickShot.App.ForegroundVisibility", true)!;
        using var activation = args.Length == 3 ? AcquireFromHotkey(int.Parse(args[2])) : null;
        var activated = false;
        var contentDeadline = DateTime.UtcNow.AddSeconds(4);
        while (!activated && DateTime.UtcNow < contentDeadline)
        {
            activated = (bool)activate.Invoke(null, [args[0]])!;
            if (!activated) { Application.DoEvents(); Thread.Sleep(100); }
        }
        if (activated) activation?.Hide();
        var contentFocus = Assembly.Load("QuickShot").GetType("QuickShot.App.BrowserFocus", throwOnError: true)!.GetMethod("TryFocusContent", BindingFlags.Static | BindingFlags.NonPublic)!;
        var expected = (bool)foreground.Invoke(null, [args[0]])!;
        var targetWindow = GetForegroundWindow();
        var children = new List<object>();
        EnumChildWindows(GetForegroundWindow(), (window, _) => { children.Add(new { name = Class(window), visible = IsWindowVisible(window) }); return true; }, 0);
        var gui = new GuiInfo { Size = (uint)Marshal.SizeOf<GuiInfo>() }; GetGUIThreadInfo(0, ref gui);
        var unobscured = (bool)visibility.GetMethod("IsUnobscured", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [GetForegroundWindow()])!;
        GetWindowRect(targetWindow, out var targetBounds);
        var centerRoot = GetAncestor(WindowFromPoint(new Point(targetBounds.Left + (targetBounds.Right - targetBounds.Left) / 2, targetBounds.Top + (targetBounds.Bottom - targetBounds.Top) / 2)), 2);
        GetWindowThreadProcessId(centerRoot, out var centerProcess);
        GetWindowThreadProcessId(targetWindow, out var targetProcess);
        var centerTitle = new StringBuilder(2048);
        if (centerProcess == targetProcess) GetWindowText(centerRoot, centerTitle, centerTitle.Capacity);
        File.WriteAllText(args[1], JsonSerializer.Serialize(new
        {
            activated, expected, unobscured, children, focusedClass = Class(gui.Focus), gui.Flags,
            targetWindow = targetWindow.ToString(), centerRoot = centerRoot.ToString(), centerProcess,
            centerClass = Class(centerRoot), helperProcess = Environment.ProcessId,
            targetProcess, centerTitle = centerTitle.ToString(), centerOwner = GetAncestor(centerRoot, 3).ToString(), targetOwner = GetAncestor(targetWindow, 3).ToString(),
            topmost = (GetWindowLong(targetWindow, -20) & 8) != 0,
            leaseWindow = visibility.GetField("_window", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!.ToString(),
            leaseHook = visibility.GetField("_hook", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!.ToString()
        }));
        var deadline = DateTime.UtcNow.AddSeconds(10);
        var probed = false;
        var focusProbed = false;
        var activationProbed = false;
        var restoreProbed = false;
        while (!File.Exists(args[1] + ".release") && DateTime.UtcNow < deadline)
        {
            if (!restoreProbed && File.Exists(args[1] + ".restore-check"))
            {
                restoreProbed = true;
                var restoreDeadline = DateTime.UtcNow.AddSeconds(2);
                while ((GetWindowLong(targetWindow, -20) & 8) != 0 && DateTime.UtcNow < restoreDeadline) { Application.DoEvents(); Thread.Sleep(20); }
                File.WriteAllText(args[1] + ".restore-result", JsonSerializer.Serialize(new
                {
                    restored = (GetWindowLong(targetWindow, -20) & 8) == 0,
                    hookReleased = (nint)visibility.GetField("_hook", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)! == 0
                }));
            }
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
                GetWindowThreadProcessId(GetForegroundWindow(), out var finalForegroundProcess);
                File.WriteAllText(args[1] + ".focus-result", JsonSerializer.Serialize(new
                {
                    contentFocused, expected = stillExpected, focusCompleted, productionMilliseconds, attempts,
                    elapsedMilliseconds = watch.ElapsedMilliseconds, errors = errors.ToArray(), uiAutomation, finalForegroundProcess
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
        visibility.GetMethod("Restore", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null);
        return activated && expected ? 0 : 1;
    }

    private static Form AcquireFromHotkey(int coverProcess)
    {
        var type = Assembly.Load("QuickShot").GetType("QuickShot.App.DeliveryActivation", true)!;
        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var form = (Form)Activator.CreateInstance(type, flags, null, null, null)!;
        try
        {
            var shortcut = Shortcut.Parse("Ctrl+Shift+F10");
            type.GetMethod("UpdateShortcuts", flags)!.Invoke(form, [new[] { shortcut }]);
            if (!(bool)type.GetMethod("ExpectHotkey", flags)!.Invoke(form, [shortcut])!) throw new InvalidOperationException("Owned test hotkey registration failed.");
            var received = false;
            type.GetField("Requested", flags)!.SetValue(form, new Action<string>(_ =>
            {
                type.GetMethod("UpdateShortcuts", flags)!.Invoke(form, [Array.Empty<Shortcut>()]);
                type.GetMethod("BeginDelivery", flags)!.Invoke(form, ["隔离测试网页", false]);
                received = true;
            }));
            GetWindowThreadProcessId(GetForegroundWindow(), out var process);
            var foregroundClass = Class(GetForegroundWindow());
            var desktop = foregroundClass is "Progman" or "WorkerW";
            if (process != coverProcess && !desktop) throw new InvalidOperationException($"Refusing test input outside the owned foreground fixture or empty desktop: actual={process}; approved={coverProcess}; class={foregroundClass}.");
            INPUT Key(ushort key, bool up = false) => new() { Type = 1, Data = new InputUnion { Keyboard = new KEYBDINPUT { Key = key, Flags = up ? 2u : 0u } } };
            INPUT[] input = [Key(0x11), Key(0x10), Key(0x79), Key(0x79, true), Key(0x10, true), Key(0x11, true)];
            if (SendInput((uint)input.Length, input, Marshal.SizeOf<INPUT>()) != input.Length)
            {
                INPUT[] release = [Key(0x79, true), Key(0x10, true), Key(0x11, true)];
                SendInput((uint)release.Length, release, Marshal.SizeOf<INPUT>());
                throw new InvalidOperationException("Owned test hotkey input failed.");
            }
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (!received && DateTime.UtcNow < deadline) { Application.DoEvents(); Thread.Sleep(10); }
            if (!received || GetForegroundWindow() != form.Handle)
            {
                GetWindowThreadProcessId(GetForegroundWindow(), out var activeProcess);
                throw new InvalidOperationException($"System hotkey did not acquire foreground for QuickShot: received={received}; foregroundProcess={activeProcess}; helperProcess={Environment.ProcessId}; coverProcess={coverProcess}.");
            }
            var wait = Stopwatch.StartNew();
            while (wait.ElapsedMilliseconds < 600) { Application.DoEvents(); Thread.Sleep(20); }
            return form;
        }
        catch { form.Dispose(); throw; }
    }

    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll")] private static extern int GetWindowLong(nint window, int index);
    [StructLayout(LayoutKind.Sequential)] private struct WindowRect { internal int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out WindowRect rect);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(Point point);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] private static extern uint SendInput(uint count, INPUT[] input, int size);
    [StructLayout(LayoutKind.Sequential)] private struct INPUT { internal uint Type; internal InputUnion Data; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion
    {
        [FieldOffset(0)] internal KEYBDINPUT Keyboard;
        [FieldOffset(0)] internal MOUSEINPUT Mouse;
    }
    [StructLayout(LayoutKind.Sequential)] private struct KEYBDINPUT { internal ushort Key, Scan; internal uint Flags, Time; internal nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct MOUSEINPUT { internal int X, Y; internal uint Data, Flags, Time; internal nuint Extra; }

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

    private static int LaunchOwnedEdge(string configuration)
    {
        var testRoot = Path.Combine(FindWorkspaceRoot(), "artifacts", "window-placement");
        if (!IsWithinDirectory(configuration, testRoot)) return 2;
        using var document = JsonDocument.Parse(File.ReadAllText(configuration));
        var settings = document.RootElement;
        var profile = settings.GetProperty("profile").GetString()!;
        var extension = settings.GetProperty("extension").GetString()!;
        var output = settings.GetProperty("output").GetString()!;
        if (!IsWithinDirectory(profile, testRoot) || !IsWithinDirectory(extension, testRoot) || !IsWithinDirectory(output, testRoot)) return 2;
        var url = settings.GetProperty("url").GetString()!;
        var info = (ProcessStartInfo)Assembly.Load("QuickShot").GetType("QuickShot.App.DesktopUi", true)!
            .GetMethod("CreateEdgeStartInfo", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [url])!;
        if (settings.GetProperty("backgroundOnly").GetBoolean()) { info.ArgumentList.Clear(); info.ArgumentList.Add("--no-startup-window"); }
        var isolation = new[] { "--no-first-run", "--no-default-browser-check", "--disable-background-networking",
            "--disable-features=msImplicitSignin,msEdgeSignin,msEdgeSync", "--keep-alive-for-test", "--remote-debugging-port=0",
            "--user-data-dir=" + profile, "--load-extension=" + extension, "--disable-extensions-except=" + extension };
        for (var index = 0; index < isolation.Length; index++) info.ArgumentList.Insert(index, isolation[index]);
        using var process = Process.Start(info)!;
        File.WriteAllText(output, JsonSerializer.Serialize(new { processId = process.Id, arguments = info.ArgumentList }));
        return 0;
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
