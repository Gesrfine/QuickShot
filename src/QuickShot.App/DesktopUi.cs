using Microsoft.Win32;
using QuickShot.Core;
using System.Diagnostics;
using System.Drawing.Drawing2D;

namespace QuickShot.App;

internal sealed class ClipboardWindow : NativeWindow, IDisposable
{
    internal event Action? Changed;
    internal ClipboardWindow()
    {
        CreateHandle(new CreateParams { Caption = "Clipboard Sender listener", Parent = new nint(-3) });
        if (!NativeMethods.AddClipboardFormatListener(Handle)) throw new System.ComponentModel.Win32Exception();
    }
    protected override void WndProc(ref Message message)
    {
        if (message.Msg == NativeMethods.WmClipboardUpdate) Changed?.Invoke();
        base.WndProc(ref message);
    }
    public void Dispose() { NativeMethods.RemoveClipboardFormatListener(Handle); DestroyHandle(); }
}

internal static class DesktopUi
{
    internal static readonly Color Accent = Color.FromArgb(103, 80, 230);
    internal static Icon CreateIcon()
    {
        using var image = new Bitmap(32, 32);
        using var graphics = Graphics.FromImage(image);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(Color.Transparent);
        using var brush = new SolidBrush(Accent);
        graphics.FillRoundedRectangle(brush, new RectangleF(1, 1, 30, 30), new SizeF(9, 9));
        using var pen = new Pen(Color.White, 2.2f);
        graphics.DrawLine(pen, 9, 14, 9, 9); graphics.DrawLine(pen, 9, 9, 14, 9);
        graphics.DrawLine(pen, 23, 18, 23, 23); graphics.DrawLine(pen, 23, 23, 18, 23);
        graphics.DrawLine(pen, 12, 20, 21, 11); graphics.DrawLine(pen, 15, 11, 21, 11); graphics.DrawLine(pen, 21, 11, 21, 17);
        var handle = image.GetHicon();
        try { return (Icon)Icon.FromHandle(handle).Clone(); }
        finally { DestroyIcon(handle); }
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool DestroyIcon(nint icon);

    internal static void OpenEdge(string url)
    {
        using var process = Process.Start(CreateEdgeStartInfo(url));
    }

    internal static ProcessStartInfo CreateEdgeStartInfo(string url)
    {
        var path = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\App Paths\msedge.exe", "", null) as string
            ?? Registry.GetValue(@"HKEY_LOCAL_MACHINE\Software\Microsoft\Windows\CurrentVersion\App Paths\msedge.exe", "", null) as string
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "Edge", "Application", "msedge.exe");
        var info = new ProcessStartInfo(path) { UseShellExecute = false };
        info.ArgumentList.Add(url);
        return info;
    }

    private static string StartupCommand => $"\"{Environment.ProcessPath}\" --background";
    internal static bool StartupEnabled => Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run", "ClipboardSender", null) as string == StartupCommand;
    internal static void SetStartup(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (enabled) key.SetValue("ClipboardSender", StartupCommand);
        else if (key.GetValue("ClipboardSender") as string == StartupCommand) key.DeleteValue("ClipboardSender", false);
    }
}

internal sealed class HintForm : Form
{
    private IReadOnlyList<TargetBinding> _bindings = [];
    private Font _titleFont = new("Microsoft YaHei UI", 14, FontStyle.Bold, GraphicsUnit.Pixel);
    private Font _keyFont = new("Microsoft YaHei UI", 13, FontStyle.Bold, GraphicsUnit.Pixel);
    private Font _nameFont = new("Microsoft YaHei UI", 13, FontStyle.Regular, GraphicsUnit.Pixel);
    private Font _smallFont = new("Microsoft YaHei UI", 11, FontStyle.Regular, GraphicsUnit.Pixel);
    private Rectangle _area;
    private int _seconds = 10, _columns = 1, _rows = 1, _rowHeight = 42, _header = 42, _footer = 26, _padding = 14, _keyWidth = 100;
    private bool _layoutDirty = true;
    private int _fontDpi = 96;
    private string[] _hosts = [];
    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams { get { var p = base.CreateParams; p.ExStyle |= 0x08000000 | 0x80; return p; } }
    internal HintForm()
    {
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(28, 30, 44); DoubleBuffered = true; Text = "剪切板快捷发送 · 当前截图";
    }
    internal void UpdateHint(IReadOnlyList<TargetBinding> bindings, int seconds)
    {
        if (bindings.Count == 0) { Hide(); return; }
        var bindingsChanged = !ReferenceEquals(_bindings, bindings);
        var changed = _seconds != seconds || bindingsChanged || _layoutDirty || !Visible;
        if (bindingsChanged)
        {
            _bindings = bindings;
            _hosts = bindings.Select(binding => Uri.TryCreate(binding.Url, UriKind.Absolute, out var uri) ? uri.Host : "").ToArray();
            _layoutDirty = true;
        }
        _seconds = seconds;
        if (!Visible)
        {
            _area = Screen.FromPoint(Cursor.Position).WorkingArea;
            _layoutDirty = true;
        }
        if (_layoutDirty)
        {
            _layoutDirty = false;
            Reflow();
        }
        if (!Visible)
        {
            PlaceInWorkingArea();
            Show();
            PlaceInWorkingArea();
        }
        if (changed) Invalidate();
    }

    private void Reflow()
    {
        var scale = DeviceDpi / 96f;
        if (_fontDpi != DeviceDpi)
        {
            _titleFont.Dispose(); _keyFont.Dispose(); _nameFont.Dispose(); _smallFont.Dispose();
            _titleFont = new Font("Microsoft YaHei UI", 14 * scale, FontStyle.Bold, GraphicsUnit.Pixel);
            _keyFont = new Font("Microsoft YaHei UI", 13 * scale, FontStyle.Bold, GraphicsUnit.Pixel);
            _nameFont = new Font("Microsoft YaHei UI", 13 * scale, FontStyle.Regular, GraphicsUnit.Pixel);
            _smallFont = new Font("Microsoft YaHei UI", 11 * scale, FontStyle.Regular, GraphicsUnit.Pixel);
            _fontDpi = DeviceDpi;
        }
        _padding = (int)(14 * scale); _rowHeight = (int)(42 * scale); _header = (int)(42 * scale); _footer = (int)(26 * scale);
        var availableWidth = Math.Max(1, _area.Width - (int)(32 * scale));
        var hardRows = Math.Max(1, (_area.Height - (int)(32 * scale) - _header - _footer) / _rowHeight);
        _rows = Math.Max(1, Math.Min(_bindings.Count, (int)((_area.Height * 0.65 - _header - _footer) / _rowHeight)));
        var fittingColumns = Math.Max(1, availableWidth / (int)(260 * scale));
        _rows = Math.Min(_bindings.Count, Math.Min(hardRows, Math.Max(_rows, (int)Math.Ceiling(_bindings.Count / (double)fittingColumns))));
        _columns = (int)Math.Ceiling(_bindings.Count / (double)_rows);
        _keyWidth = _bindings.Max(binding => TextRenderer.MeasureText(binding.Key, _keyFont).Width) + (int)(14 * scale);
        var columnWidth = Math.Min((int)(320 * scale), availableWidth / _columns);
        ClientSize = new Size(Math.Min(availableWidth, columnWidth * _columns), _header + _rows * _rowHeight + _footer);
        using var shape = new GraphicsPath();
        shape.AddRoundedRectangle(new RectangleF(0, 0, Width, Height), new SizeF(14 * scale, 14 * scale));
        var previous = Region; Region = new Region(shape); previous?.Dispose();
        AccessibleName = "图片已就绪";
        AccessibleDescription = string.Join("；", _bindings.Select(binding => $"{binding.Key} 投递到 {binding.Name}")) + "。其他按键或点击鼠标取消。";
    }

    private void PlaceInWorkingArea()
    {
        var margin = (int)(16 * DeviceDpi / 96f);
        Location = new Point(Math.Max(_area.Left, _area.Right - Width - margin), Math.Max(_area.Top, _area.Bottom - Height - margin));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        const TextFormatFlags textFlags = TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter;
        void Text(string value, Font font, Color color, Rectangle rectangle) => TextRenderer.DrawText(g, value, font, rectangle, color, textFlags);
        var scale = DeviceDpi / 96f;
        int Px(int value) => (int)(value * scale);
        Text("图片就绪 · 按键投递", _titleFont, Color.White, new Rectangle(_padding, Px(10), Width - Px(76), Px(24)));
        Text(_seconds + "s", _titleFont, Color.FromArgb(185, 173, 255), new Rectangle(Width - Px(58), Px(10), Px(42), Px(24)));
        var columnWidth = Width / _columns;
        for (var i = 0; i < _bindings.Count; i++)
        {
            var binding = _bindings[i]; var column = i / _rows; var row = i % _rows;
            var x = column * columnWidth + _padding; var y = _header + row * _rowHeight;
            var keyRect = new Rectangle(x, y + Px(3), _keyWidth, _rowHeight - Px(8));
            using var fill = new SolidBrush(Color.FromArgb(58, 49, 97));
            g.FillRoundedRectangle(fill, keyRect, new SizeF(Px(6), Px(6)));
            TextRenderer.DrawText(g, binding.Key, _keyFont, keyRect, Color.FromArgb(222, 213, 255), textFlags | TextFormatFlags.HorizontalCenter);
            var textX = keyRect.Right + Px(10); var textWidth = Math.Max(1, (column + 1) * columnWidth - _padding - textX);
            Text(binding.Name, _nameFont, Color.White, new Rectangle(textX, y + Px(1), textWidth, Px(21)));
            Text(_hosts[i], _smallFont, Color.FromArgb(148, 155, 178), new Rectangle(textX, y + Px(21), textWidth, Px(17)));
        }
        var footerY = _header + _rows * _rowHeight;
        Text("其他按键或点击鼠标取消", _smallFont, Color.FromArgb(165, 170, 190), new Rectangle(_padding, footerY, Width - 2 * _padding, _footer - Px(4)));
        using var progress = new SolidBrush(DesktopUi.Accent);
        g.FillRectangle(progress, 0, Height - 3, Width * Math.Clamp(_seconds / 10f, 0, 1), 3);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e) { base.OnDpiChanged(e); _layoutDirty = true; }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { _titleFont.Dispose(); _keyFont.Dispose(); _nameFont.Dispose(); _smallFont.Dispose(); }
        base.Dispose(disposing);
    }
}

internal sealed class StatusNoticeForm : Form
{
    private readonly System.Windows.Forms.Timer _dismiss = new() { Interval = 5500 };
    private Font _titleFont = new("Microsoft YaHei UI", 13, FontStyle.Bold, GraphicsUnit.Pixel);
    private Font _messageFont = new("Microsoft YaHei UI", 13, FontStyle.Regular, GraphicsUnit.Pixel);
    private string _message = "";
    private Rectangle _area;
    private int _fontDpi = 96;
    private int _padding = 14, _header = 29;
    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams { get { var p = base.CreateParams; p.ExStyle |= 0x08000000 | 0x80; return p; } }

    internal StatusNoticeForm()
    {
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(28, 30, 44); DoubleBuffered = true;
        Text = "剪切板快捷发送 · 状态提示"; AccessibleRole = AccessibleRole.Alert;
        _dismiss.Tick += (_, _) => Hide();
    }

    internal void ShowMessage(string message)
    {
        _dismiss.Stop();
        _message = message; AccessibleName = "剪切板快捷发送状态"; AccessibleDescription = message;
        _area = Screen.FromPoint(Cursor.Position).WorkingArea;
        Reflow(); PlaceInWorkingArea();
        if (!Visible) Show();
        Reflow(); PlaceInWorkingArea();
        Invalidate(); _dismiss.Start();
    }

    private void Reflow()
    {
        var scale = DeviceDpi / 96f;
        if (_fontDpi != DeviceDpi)
        {
            _titleFont.Dispose(); _messageFont.Dispose();
            _titleFont = new Font("Microsoft YaHei UI", 13 * scale, FontStyle.Bold, GraphicsUnit.Pixel);
            _messageFont = new Font("Microsoft YaHei UI", 13 * scale, FontStyle.Regular, GraphicsUnit.Pixel);
            _fontDpi = DeviceDpi;
        }
        _padding = (int)(14 * scale); _header = (int)(29 * scale);
        var width = Math.Min((int)(320 * scale), Math.Max(1, _area.Width - (int)(32 * scale)));
        var messageHeight = TextRenderer.MeasureText(_message, _messageFont,
            new Size(Math.Max(1, width - 2 * _padding), int.MaxValue),
            TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding).Height;
        ClientSize = new Size(width, Math.Min(Math.Max(1, _area.Height - (int)(32 * scale)), _header + messageHeight + _padding));
        using var shape = new GraphicsPath();
        shape.AddRoundedRectangle(new RectangleF(0, 0, Width, Height), new SizeF(12 * scale, 12 * scale));
        var previous = Region; Region = new Region(shape); previous?.Dispose();
    }

    private void PlaceInWorkingArea()
    {
        var margin = (int)(16 * DeviceDpi / 96f);
        Location = new Point(Math.Max(_area.Left, _area.Right - Width - margin), Math.Max(_area.Top, _area.Bottom - Height - margin));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        TextRenderer.DrawText(e.Graphics, "剪切板快捷发送", _titleFont, new Rectangle(_padding, _padding / 2, Width - 2 * _padding, _header - _padding / 2),
            Color.FromArgb(185, 173, 255), TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter);
        TextRenderer.DrawText(e.Graphics, _message, _messageFont, new Rectangle(_padding, _header, Width - 2 * _padding, Height - _header - _padding),
            Color.White, TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
    }

    protected override void OnMouseDown(MouseEventArgs e) { Hide(); base.OnMouseDown(e); }
    protected override void OnVisibleChanged(EventArgs e) { if (!Visible) _dismiss.Stop(); base.OnVisibleChanged(e); }
    protected override void OnDpiChanged(DpiChangedEventArgs e) { base.OnDpiChanged(e); if (_message.Length > 0) { Reflow(); PlaceInWorkingArea(); } }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { _dismiss.Dispose(); _titleFont.Dispose(); _messageFont.Dispose(); }
        base.Dispose(disposing);
    }
}

internal sealed class DashboardForm : Form
{
    internal event Action? RemoveRequested;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal Func<AppSettings, string?>? SettingsRequested { get; set; }
    private readonly bool _preview;
    protected override bool ShowWithoutActivation => _preview;
    protected override CreateParams CreateParams { get { var p = base.CreateParams; if (_preview) p.ExStyle |= 0x08000000 | 0x80; return p; } }
    private readonly Label _connection = new() { AutoSize = true, ForeColor = DesktopUi.Accent, Font = new Font("Microsoft YaHei UI", 11, FontStyle.Bold) };
    private readonly Label _status = new() { AutoSize = false, Height = 48, Dock = DockStyle.Fill, ForeColor = Color.FromArgb(95, 100, 116) };
    private readonly ListView _targets = new() { View = View.Details, FullRowSelect = true, BorderStyle = BorderStyle.FixedSingle, Dock = DockStyle.Fill, HeaderStyle = ColumnHeaderStyle.Nonclickable };
    private readonly Label _instructions = new() { AutoSize = true, ForeColor = Color.FromArgb(95, 100, 116) };
    private readonly CheckBox _showNotifications = new() { Text = "显示右下角提示（图片倒计时及投递结果）", AutoSize = true, Checked = true };
    private AppSettings _settings = new();
    private bool _refreshingSettings;
    private string _signature = "";
    internal DashboardForm(bool preview = false)
    {
        _preview = preview;
        AutoScaleDimensions = new SizeF(96, 96); AutoScaleMode = AutoScaleMode.Dpi;
        Text = "剪切板快捷发送"; Font = new Font("Microsoft YaHei UI", 10); BackColor = Color.FromArgb(248, 249, 253);
        ClientSize = new Size(720, 540); MinimumSize = new Size(680, 510); StartPosition = FormStartPosition.CenterScreen;
        using var icon = DesktopUi.CreateIcon(); Icon = (Icon)icon.Clone();
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24), ColumnCount = 1, RowCount = 8 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
        layout.Controls.Add(new Label { Text = "剪切板快捷发送 2.0", AutoSize = true, Font = new Font("Microsoft YaHei UI", 22, FontStyle.Bold), ForeColor = Color.FromArgb(28, 32, 48) });
        layout.Controls.Add(_instructions);
        layout.Controls.Add(_connection);
        _targets.Columns.Add("快捷键", 160); _targets.Columns.Add("接收目标", 170); _targets.Columns.Add("默认网址", 300);
        layout.Controls.Add(_targets);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Padding = new Padding(0, 12, 0, 0) };
        var manage = new Button { Text = "打开 Edge 绑定管理", AutoSize = true, MinimumSize = new Size(190, 36), BackColor = DesktopUi.Accent, ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
        manage.Click += (_, _) => OpenManager();
        var remove = new Button { Text = "解除浏览器连接", AutoSize = true, MinimumSize = new Size(100, 36), FlatStyle = FlatStyle.Flat };
        remove.Click += (_, _) => RemoveRequested?.Invoke();
        buttons.Controls.Add(manage); buttons.Controls.Add(remove); layout.Controls.Add(buttons);
        var startup = new CheckBox { Text = "登录 Windows 时启动（默认关闭）", AutoSize = true, Checked = DesktopUi.StartupEnabled };
        startup.CheckedChanged += (_, _) =>
        {
            try { DesktopUi.SetStartup(startup.Checked); }
            catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException) { MessageBox.Show(e.Message, "无法设置开机启动"); }
        };
        layout.Controls.Add(startup);
        _showNotifications.CheckedChanged += (_, _) =>
        {
            if (!_refreshingSettings) RequestSettings(_settings with { ShowNotifications = _showNotifications.Checked });
        };
        layout.Controls.Add(_showNotifications);
        layout.Controls.Add(_status); Controls.Add(layout);
        RefreshSettings(_settings);
    }

    private void RequestSettings(AppSettings settings)
    {
        var error = SettingsRequested?.Invoke(settings);
        if (error == null) RefreshSettings(settings);
        else
        {
            RefreshSettings(_settings);
            _status.Text = error;
        }
    }

    internal void RefreshSettings(AppSettings settings)
    {
        _settings = settings;
        _refreshingSettings = true;
        try
        {
            _showNotifications.Checked = settings.ShowNotifications;
            _instructions.Text = "复制图片 → 10 秒内按目标快捷键 → 确认附件后手动发送\n按目标键前，其他按键或鼠标点击取消；支持任意图片来源。";
        }
        finally { _refreshingSettings = false; }
    }

    internal static void OpenManager()
    {
        try { DesktopUi.OpenEdge($"chrome-extension://{Protocol.ExtensionId}/options.html"); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { MessageBox.Show("无法打开 Edge，请先安装扩展。\n" + e.Message, "剪切板快捷发送"); }
    }

    internal void RefreshState(bool connected, IReadOnlyList<TargetBinding> bindings, string status)
    {
        _connection.Text = connected ? $"● Edge 已连接 · {bindings.Count} 个接收目标" : "○ Edge 未连接 · 打开扩展管理页可连接";
        _status.Text = status;
        var signature = string.Join("|", bindings.Select(t => $"{t.Key}:{t.Name}:{t.Url}"));
        if (signature == _signature) return;
        _signature = signature; _targets.Items.Clear();
        foreach (var target in bindings) _targets.Items.Add(new ListViewItem([target.Key, target.Name, target.Url]));
    }

}
