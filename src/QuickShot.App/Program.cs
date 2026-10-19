using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Security.Principal;
using System.Text.Json.Nodes;
using QuickShot.Core;

namespace QuickShot.App;

internal static class Program
{
    private static readonly string UserSid = ReadUserSid();
    private static string ReadUserSid() { using var identity = WindowsIdentity.GetCurrent(); return identity.User!.Value; }
    internal static string PipeName => "ClipboardSender." + UserSid
        + (Environment.GetEnvironmentVariable("QUICKSHOT_INSTANCE_ID") is { Length: > 0 } instance ? "." + instance : "");

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Any(a => a.StartsWith("chrome-extension://", StringComparison.Ordinal) || a == "--native-host"))
            return NativeBridge.RunAsync(args).GetAwaiter().GetResult();
        return RunDesktop(args);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RunDesktop(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if (args.Length is 2 or 3 && args[0] is "--render-preview" or "--render-hint-preview")
        {
            using Form preview = args[0] == "--render-hint-preview" ? new HintForm() : new DashboardForm(preview: true);
            preview.Opacity = 0; preview.ShowInTaskbar = false;
            if (preview is DashboardForm dashboard) dashboard.RefreshState(true,
                [new TargetBinding("preview-c", "C", "DeepSeek 工作聊天", "https://chat.deepseek.com/"),
                 new TargetBinding("preview-d", "Ctrl+Alt+D", "另一个聊天", "https://example.com/chat")],
                "复制图片后 10 秒内按目标快捷键投递。已有草稿会保留，消息由你手动发送。");
            if (preview is HintForm hint)
            {
                var count = args.Length == 3 && int.TryParse(args[2], out var requested) ? Math.Clamp(requested, 1, 26) : 4;
                hint.UpdateHint(Enumerable.Range(0, count).Select(i => new TargetBinding("preview-" + i, i == 0 ? "C" : "Ctrl+Alt+" + (char)('A' + i), i == 0 ? "DeepSeek 工作聊天" : "接收目标 " + (i + 1), i == 0 ? "https://chat.deepseek.com/" : "https://example.com/chat")).ToArray(), 10);
            }
            else preview.Show();
            Application.DoEvents();
            preview.PerformLayout();
            using var bitmap = new Bitmap(preview.Width, preview.Height);
            preview.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
            bitmap.Save(Path.GetFullPath(args[1]), System.Drawing.Imaging.ImageFormat.Png);
            preview.Hide();
            return 0;
        }
        using var mutex = new Mutex(true, @"Local\" + PipeName, out var firstInstance);
        if (!firstInstance) return 0;
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => LocalLog.Write("ui", e.Exception);
        var registrationError = AppPaths.RegisterConnection();
        using var context = new TrayApplication(registrationError);
        Application.Run(context);
        return 0;
    }
}

internal static class NativeBridge
{
    internal static async Task<int> RunAsync(string[] args)
    {
        var origin = args.FirstOrDefault(a => a.StartsWith("chrome-extension://", StringComparison.Ordinal));
        if (origin != null && origin != $"chrome-extension://{Protocol.ExtensionId}/") return 2;
        try
        {
            var input = Console.OpenStandardInput();
            var output = Console.OpenStandardOutput();
            using var firstMessageTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var hello = await Protocol.ReadAsync(input, firstMessageTimeout.Token);
            if (hello == null || hello["type"]?.GetValue<string>() != "hello") return 2;
            if (hello["version"]?.GetValue<int>() != Protocol.Version)
            {
                await Protocol.WriteAsync(output, new JsonObject { ["type"] = "rejected", ["message"] = "程序与扩展版本不一致。请更新发布包，并在 Edge 重新加载扩展。" }, CancellationToken.None);
                return 2;
            }
            using var pipe = new NamedPipeClientStream(".", Program.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            try { await pipe.ConnectAsync(400); }
            catch (TimeoutException)
            {
                if (hello["intent"]?.GetValue<string>() != "launch")
                {
                    await Protocol.WriteAsync(output, new JsonObject { ["type"] = "appStopped", ["message"] = "QuickShot 尚未运行。请手动打开程序，或点击“启动 / 连接 QuickShot”。" }, CancellationToken.None);
                    return 0;
                }
                var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
                start.ArgumentList.Add("--background");
                Process.Start(start);
                await pipe.ConnectAsync(10000);
            }
            await Protocol.WriteAsync(pipe, hello, CancellationToken.None);
            using var cancellation = new CancellationTokenSource();
            async Task Relay(Stream source, Stream destination)
            {
                while (!cancellation.IsCancellationRequested)
                {
                    var message = await Protocol.ReadAsync(source, cancellation.Token);
                    if (message == null) break;
                    await Protocol.WriteAsync(destination, message, cancellation.Token);
                }
            }
            var incoming = Relay(input, pipe);
            var outgoing = Relay(pipe, output);
            var finished = await Task.WhenAny(incoming, outgoing);
            await finished;
            await cancellation.CancelAsync();
            return 0;
        }
        catch (Exception e) when (e is IOException or TimeoutException or OperationCanceledException or InvalidDataException or InvalidOperationException or FormatException or System.Text.Json.JsonException)
        {
            LocalLog.Write("bridge", e);
            return 1;
        }
    }
}

internal static class LocalLog
{
    internal static string DataDirectory => AppPaths.DataDirectory;
    internal static void Write(string area, Exception exception) => Event(area, $"{exception.GetType().Name}: {exception.Message}");
    internal static void Event(string area, string detail)
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
            var path = Path.Combine(DataDirectory, "diagnostics.log");
            if (File.Exists(path) && new FileInfo(path).Length > 256_000) File.WriteAllText(path, "");
            File.AppendAllText(path, $"{DateTimeOffset.Now:O} [{area}] {detail}{Environment.NewLine}");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

