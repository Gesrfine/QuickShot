using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

[assembly: AssemblyTitle("快投 QuickShot 安装程序")]
[assembly: AssemblyDescription("快投 Windows x64 安装准备程序")]
[assembly: AssemblyProduct("快投 QuickShot")]
[assembly: AssemblyCompany("QuickShot")]
[assembly: AssemblyVersion("1.2.0.0")]
[assembly: AssemblyFileVersion("1.2.0.0")]

namespace QuickShot.Setup
{
    internal static class Bootstrap
    {
        [STAThread]
        private static int Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            string user;
            using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                user = identity.User == null ? Environment.UserName : identity.User.Value;
            bool firstInstance;
            using (Mutex mutex = new Mutex(true, @"Local\QuickShot.Setup." + user, out firstInstance))
            {
                if (!firstInstance)
                {
                    MessageBox.Show("快投安装程序已经打开，请在现有安装窗口中继续。", "快投安装程序", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return 2;
                }
                using (PreparationForm form = new PreparationForm())
                {
                    Application.Run(form);
                    return form.ExitCode;
                }
            }
        }
    }

    internal sealed class PreparationForm : Form
    {
        private readonly Label status;
        private readonly ProgressBar progress;
        private bool allowClose;
        internal int ExitCode { get; private set; }

        internal PreparationForm()
        {
            Text = "快投 QuickShot 安装程序";
            Font = new Font("Microsoft YaHei UI", 10);
            ClientSize = new Size(520, 190);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(248, 249, 253);
            Label heading = new Label { Text = "正在准备安装", AutoSize = true, Location = new Point(26, 24), Font = new Font("Microsoft YaHei UI", 19, FontStyle.Bold) };
            status = new Label { Text = "正在检查安装文件，请稍候…", AutoSize = false, Location = new Point(28, 77), Size = new Size(464, 46) };
            progress = new ProgressBar { Location = new Point(28, 136), Size = new Size(464, 17), Style = ProgressBarStyle.Marquee };
            Controls.Add(heading);
            Controls.Add(status);
            Controls.Add(progress);
            Shown += Prepare;
            FormClosing += delegate(object sender, FormClosingEventArgs args)
            {
                if (!allowClose)
                {
                    args.Cancel = true;
                    status.Text = "正在准备安装文件，请稍候。";
                }
            };
        }

        private async void Prepare(object sender, EventArgs args)
        {
            string payloadRoot = null;
            Exception failure = null;
            string cleanupError = null;
            try
            {
                payloadRoot = TemporaryPayload.Create();
                Progress<PreparationProgress> updates = new Progress<PreparationProgress>(delegate(PreparationProgress update)
                {
                    if (IsDisposed) return;
                    status.Text = update.Message;
                    if (update.Percent >= 0)
                    {
                        progress.Style = ProgressBarStyle.Continuous;
                        progress.Value = Math.Max(0, Math.Min(100, update.Percent));
                    }
                });
                await Task.Run(delegate
                {
                    PayloadArchive.ExtractTo(Assembly.GetExecutingAssembly(), payloadRoot, delegate(int percent, string message)
                    {
                        ((IProgress<PreparationProgress>)updates).Report(new PreparationProgress(percent, message));
                    });
                });
                status.Text = "正在打开安装窗口…";
                ProcessStartInfo start = new ProcessStartInfo(Path.Combine(payloadRoot, "app", "QuickShot.exe"), "--install-ui")
                {
                    UseShellExecute = false,
                    WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows)
                };
                using (Process installer = Process.Start(start))
                {
                    if (installer == null) throw new IOException("无法打开安装窗口。");
                    Hide();
                    await Task.Run(delegate { installer.WaitForExit(); });
                    if (installer.ExitCode != 0) throw new IOException("安装窗口异常退出（代码 " + installer.ExitCode + "）。请重新运行安装程序。");
                }
            }
            catch (Exception error)
            {
                ExitCode = 1;
                failure = error;
            }
            if (payloadRoot != null)
                cleanupError = await Task.Run(delegate { return TemporaryPayload.Cleanup(payloadRoot); });
            if (failure != null)
                MessageBox.Show("安装准备未完成：\n" + failure.Message + "\n\n请确认有足够的磁盘空间，并重新运行安装程序。", "快投安装程序", MessageBoxButtons.OK, MessageBoxIcon.Error);
            if (cleanupError != null)
                MessageBox.Show("安装窗口已关闭，临时文件暂时无法清理。可稍后手动删除此文件夹：\n" + payloadRoot + "\n\n" + cleanupError, "快投安装程序", MessageBoxButtons.OK, MessageBoxIcon.Information);
            allowClose = true;
            Close();
        }
    }

    internal sealed class PreparationProgress
    {
        internal int Percent { get; private set; }
        internal string Message { get; private set; }
        internal PreparationProgress(int percent, string message) { Percent = percent; Message = message; }
    }

    internal static class PayloadArchive
    {
        internal const string ArchiveResource = "QuickShot.Setup.Payload.zip";
        internal const string HashResource = "QuickShot.Setup.Payload.sha256";
        private const long MaximumUnpackedBytes = 1024L * 1024 * 1024;

        internal static void ExtractTo(Assembly assembly, string destination, Action<int, string> report)
        {
            if (!Directory.Exists(destination) || Directory.GetFileSystemEntries(destination).Length != 0)
                throw new IOException("安装临时目录必须为空。");
            string expected;
            using (Stream hashStream = assembly.GetManifestResourceStream(HashResource))
            {
                if (hashStream == null) throw new InvalidDataException("安装文件缺少校验信息。");
                using (StreamReader reader = new StreamReader(hashStream, Encoding.ASCII)) expected = reader.ReadToEnd().Trim();
            }
            if (expected.Length != 64) throw new InvalidDataException("安装文件校验信息无效。");
            if (report != null) report(-1, "正在检查安装文件完整性…");
            using (Stream archiveStream = assembly.GetManifestResourceStream(ArchiveResource))
            {
                if (archiveStream == null) throw new InvalidDataException("安装文件缺少程序内容。");
                string actual;
                using (SHA256 hash = SHA256.Create()) actual = BitConverter.ToString(hash.ComputeHash(archiveStream)).Replace("-", "").ToLowerInvariant();
                if (!String.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("安装文件校验失败，请重新下载安装程序。");
            }
            using (Stream archiveStream = assembly.GetManifestResourceStream(ArchiveResource))
            using (ZipArchive archive = new ZipArchive(archiveStream, ZipArchiveMode.Read, false))
            {
                long total = 0;
                HashSet<string> paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (archive.Entries.Count > 5000) throw new InvalidDataException("安装文件数量异常。");
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    string path = GetSafeEntryPath(destination, entry.FullName);
                    if (!paths.Add(path)) throw new InvalidDataException("安装文件包含重复路径。");
                    EnsureRegularEntry(entry);
                    total = checked(total + entry.Length);
                    if (total > MaximumUnpackedBytes) throw new InvalidDataException("安装文件大小异常。");
                }
                long completed = 0;
                byte[] buffer = new byte[81920];
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    string path = GetSafeEntryPath(destination, entry.FullName);
                    if (entry.FullName.EndsWith("/", StringComparison.Ordinal) || entry.FullName.EndsWith("\\", StringComparison.Ordinal))
                    {
                        Directory.CreateDirectory(path);
                        continue;
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    using (Stream source = entry.Open())
                    using (FileStream target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        int read;
                        long written = 0;
                        while ((read = source.Read(buffer, 0, buffer.Length)) != 0)
                        {
                            written = checked(written + read);
                            if (written > entry.Length) throw new InvalidDataException("安装文件解压大小与记录不符。");
                            target.Write(buffer, 0, read);
                            completed += read;
                            if (report != null) report(total == 0 ? 100 : (int)(completed * 100 / total), "正在展开安装文件… " + (total == 0 ? 100 : completed * 100 / total) + "%");
                        }
                        if (written != entry.Length) throw new InvalidDataException("安装文件内容不完整。");
                    }
                }
            }
            foreach (string required in new[] { "Install.ps1", "Uninstall.ps1", "Uninstall.cmd", "extension-id.txt", "app/QuickShot.exe", "extension/manifest.json" })
                if (!File.Exists(GetSafeEntryPath(destination, required))) throw new InvalidDataException("安装文件缺少必要内容：" + required);
            if (report != null) report(100, "安装文件准备完成。");
        }

        internal static string GetSafeEntryPath(string destination, string entryName)
        {
            if (String.IsNullOrEmpty(entryName)) throw new InvalidDataException("安装文件包含空路径。");
            string relative = entryName.Replace('\\', '/');
            if (relative.StartsWith("/", StringComparison.Ordinal) || relative.IndexOf(':') >= 0)
                throw new InvalidDataException("安装文件包含非法绝对路径。");
            relative = relative.TrimEnd('/');
            foreach (string part in relative.Split('/'))
            {
                if (part.Length == 0 || part == "." || part == ".." || part.EndsWith(".", StringComparison.Ordinal) || Char.IsWhiteSpace(part[part.Length - 1]))
                    throw new InvalidDataException("安装文件包含非法相对路径。");
                if (part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new InvalidDataException("安装文件名无效。");
                string device = part.Split('.')[0].ToUpperInvariant();
                if (device == "CON" || device == "PRN" || device == "AUX" || device == "NUL" ||
                    (device.Length == 4 && (device.StartsWith("COM", StringComparison.Ordinal) || device.StartsWith("LPT", StringComparison.Ordinal)) && device[3] >= '1' && device[3] <= '9'))
                    throw new InvalidDataException("安装文件包含保留设备名称。");
            }
            string root = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string result = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!result.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("安装文件路径超出临时目录。");
            return result;
        }

        private static void EnsureRegularEntry(ZipArchiveEntry entry)
        {
            int unixKind = (entry.ExternalAttributes >> 16) & 0xf000;
            if (unixKind == 0xa000 || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("安装文件包含链接，已停止解压。");
        }
    }

    internal static class TemporaryPayload
    {
        private static string Root { get { return Path.GetFullPath(Path.Combine(Path.GetTempPath(), "QuickShot-Setup")); } }

        internal static string Create()
        {
            string root = Root;
            Directory.CreateDirectory(root);
            if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) throw new IOException("安装临时目录不能是链接。");
            string directory = Path.Combine(root, Guid.NewGuid().ToString("N"));
            if (Directory.Exists(directory) || File.Exists(directory)) throw new IOException("安装临时目录已存在，请重试。");
            Directory.CreateDirectory(directory);
            return directory;
        }

        internal static string Cleanup(string directory)
        {
            string absolute = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar);
            Guid id;
            if (!String.Equals(Path.GetDirectoryName(absolute), Root, StringComparison.OrdinalIgnoreCase) ||
                !Guid.TryParseExact(Path.GetFileName(absolute), "N", out id)) return "临时目录校验未通过，未删除文件。";
            string error = null;
            for (int attempt = 0; attempt < 8; attempt++)
            {
                try
                {
                    if (!Directory.Exists(absolute)) return null;
                    if ((File.GetAttributes(Root) & FileAttributes.ReparsePoint) != 0 || (File.GetAttributes(absolute) & FileAttributes.ReparsePoint) != 0)
                        return "临时目录已变成链接，未删除文件。";
                    Directory.Delete(absolute, true);
                    return null;
                }
                catch (IOException failure) { error = failure.Message; }
                catch (UnauthorizedAccessException failure) { error = failure.Message; }
                Thread.Sleep(250);
            }
            return error;
        }
    }
}
