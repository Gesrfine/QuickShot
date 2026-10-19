using System.Diagnostics;
using System.Text;

namespace QuickShot.App;

internal sealed class InstallationForm : Form
{
    private readonly bool _preview;
    private bool _installing, _completed;
    private readonly TextBox _directory = new() { Dock = DockStyle.Fill };
    private readonly Button _install = new() { Text = "安装到此目录", AutoSize = true, BackColor = DesktopUi.Accent, ForeColor = Color.White, FlatStyle = FlatStyle.Flat, Padding = new Padding(12, 6, 12, 6) };
    private readonly Label _status = new() { AutoSize = true, MaximumSize = new Size(620, 0), ForeColor = Color.FromArgb(85, 91, 111) };
    private readonly ProgressBar _progress = new() { Dock = DockStyle.Fill, Visible = false, Style = ProgressBarStyle.Marquee };
    private readonly CheckBox _launch = new() { Text = "安装完成后打开快投", Checked = true, AutoSize = true };
    private readonly Button _browse = new() { Text = "浏览…", AutoSize = true, Padding = new Padding(8, 2, 8, 2) };
    private readonly Button _cancel = new() { Text = "取消", AutoSize = true, Padding = new Padding(8, 6, 8, 6) };
    protected override bool ShowWithoutActivation => _preview;
    protected override CreateParams CreateParams { get { var p = base.CreateParams; if (_preview) p.ExStyle |= 0x08000000 | 0x80; return p; } }

    internal InstallationForm(bool preview = false)
    {
        _preview = preview;
        AutoScaleDimensions = new SizeF(96, 96); AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Microsoft YaHei UI", 10);
        Text = "安装快投 QuickShot"; ClientSize = new Size(720, 430); MinimumSize = new Size(700, 450);
        BackColor = Color.FromArgb(248, 249, 253); StartPosition = FormStartPosition.CenterScreen;
        using var icon = DesktopUi.CreateIcon(); Icon = (Icon)icon.Clone();
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(28), ColumnCount = 1, RowCount = 8 };
        for (var i = 0; i < 7; i++) layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(new Label { Text = "安装到你选择的位置", AutoSize = true, Font = new Font("Microsoft YaHei UI", 20, FontStyle.Bold), Margin = new Padding(0, 0, 0, 12) });
        layout.Controls.Add(new Label { Text = "安装程序已包含所需文件。选择安装目录后，点击下方按钮开始。", AutoSize = true, ForeColor = Color.FromArgb(85, 91, 111), Margin = new Padding(0, 0, 0, 18) });
        layout.Controls.Add(new Label { Text = "安装文件夹", AutoSize = true, Margin = new Padding(0, 6, 0, 6) });
        var pathRow = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, Margin = new Padding(0, 0, 0, 14) };
        pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _directory.Text = AppPaths.DefaultInstallationDirectory();
        _browse.Click += (_, _) =>
        {
            using var picker = new FolderBrowserDialog { Description = "选择用于安装快投的专用文件夹", UseDescriptionForTitle = true, SelectedPath = _directory.Text, ShowNewFolderButton = true };
            if (picker.ShowDialog(this) == DialogResult.OK) _directory.Text = picker.SelectedPath;
        };
        pathRow.Controls.Add(_directory, 0, 0); pathRow.Controls.Add(_browse, 1, 0); layout.Controls.Add(pathRow);
        layout.Controls.Add(_launch);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false, Padding = new Padding(0, 12, 0, 12), Margin = Padding.Empty };
        _install.Click += async (_, _) => { if (_completed) Close(); else await InstallAsync(); };
        _cancel.Click += (_, _) => Close();
        actions.Controls.Add(_install); actions.Controls.Add(_cancel); layout.Controls.Add(actions);
        layout.Controls.Add(_progress); layout.Controls.Add(_status); Controls.Add(layout);
        _status.Text = "开机自启默认关闭。安装后，在 Edge 加载所选目录内的 extension 文件夹。";
    }

    private async Task InstallAsync()
    {
        _installing = true; _install.Enabled = false; _directory.Enabled = false; _browse.Enabled = false; _cancel.Enabled = false; _launch.Enabled = false; _progress.Visible = true;
        try
        {
            var destination = Path.GetFullPath(_directory.Text.Trim());
            var script = Path.Combine(AppPaths.PackageDirectory, "Install.ps1");
            if (!File.Exists(script)) throw new IOException("安装文件不完整，请重新下载安装版 EXE 后运行。便携版无需安装。");
            _status.Text = "正在复制程序并配置当前用户的浏览器连接…";
            var start = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows)
            };
            foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, "-InstallDirectory", destination, "-InstallerProcessId", Environment.ProcessId.ToString(), "-NoLaunch" }) start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new IOException("无法启动安装脚本。");
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var output = await stdout; var error = await stderr;
            if (process.ExitCode != 0) throw new IOException(string.IsNullOrWhiteSpace(error) ? output : error);
            _completed = true; _install.Text = "完成"; _cancel.Text = "关闭";
            _status.Text = $"安装完成。请在 Edge 的扩展页面加载：\n{Path.Combine(destination, "extension")}\n开机启动可在快投主窗口中设置。";
            if (_launch.Checked)
                Process.Start(new ProcessStartInfo(Path.Combine(destination, "app", "QuickShot.exe")) { UseShellExecute = false, WorkingDirectory = destination });
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or NotSupportedException)
        { _status.Text = (_completed ? "安装已完成，但未能打开程序：" : "安装未完成：") + e.Message; }
        finally
        {
            _installing = false; _install.Enabled = true; _directory.Enabled = !_completed; _browse.Enabled = !_completed;
            _cancel.Enabled = true; _launch.Enabled = !_completed; _progress.Visible = false;
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_installing) { e.Cancel = true; _status.Text = "正在安装，请等待完成后关闭窗口。"; }
        base.OnFormClosing(e);
    }
}
