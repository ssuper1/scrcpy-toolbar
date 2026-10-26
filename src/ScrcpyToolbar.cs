using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        bool first;
        using (var mutex = new Mutex(true, "Local\\ScrcpySideToolbar-v1", out first))
        {
            if (!first) return;
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try { Application.Run(new Toolbar()); }
            catch (Exception ex)
            {
                MessageBox.Show("工具栏启动失败：\n" + ex.Message, "scrcpy 工具栏",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}

internal sealed class QualityPreset
{
    internal readonly string Name;
    internal readonly string BitRate;
    internal readonly string MaxFps;

    internal QualityPreset(string name, string bitRate, string maxFps)
    {
        Name = name;
        BitRate = bitRate;
        MaxFps = maxFps;
    }
}

internal sealed class Toolbar : Form
{
    private static readonly QualityPreset[] QualityPresets = {
        new QualityPreset("流畅", "8M", "30"),
        new QualityPreset("标准", "12M", "60"),
        new QualityPreset("高清", "20M", "60"),
        new QualityPreset("超清", "40M", "60")
    };
    private readonly string root = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".."));
    private readonly System.Windows.Forms.Timer follow = new System.Windows.Forms.Timer();
    private readonly ToolTip tips = new ToolTip();
    private readonly Label status = new Label();
    private readonly List<Button> actions = new List<Button>();
    private Button fullscreenButton;
    private Button qualityButton;
    private Button keyboardButton;
    private Button foldButton;
    private Label heading;
    private int expandedHeight;
    private bool collapsed, dragging, windowFloating, fullscreenFloating;
    private Point dragMouse, dragOrigin, windowOffset, fullscreenOffset;
    private DateTime lastInteraction = DateTime.UtcNow;
    private readonly Stopwatch startup = Stopwatch.StartNew();
    private readonly object logLock = new object();
    private readonly StringBuilder recentLog = new StringBuilder();
    private Process mirror;
    private IntPtr target;
    private bool ready, closing, preferLeft, fullscreen, raisedForFullscreen, exitRequested;
    private bool restarting, mirrorQualityKnown;
    private QualityPreset quality = QualityPresets[2];
    private ContextMenuStrip qualityMenu;
    private ContextMenuStrip keyboardMenu;
    private string keyboardMode = "uhid";
    private bool keyboardModeKnown;
    private readonly string settingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ScrcpyToolbar", "keyboard-mode.txt");
    private readonly Stopwatch restartWait = new Stopwatch();
    private QualityPreset previousQuality;
    private string previousKeyboardMode;
    private string restartWindowArguments = "";
    private string deviceSerial;
    private float scale;
    private string logPath;

    public Toolbar()
    {
        try
        {
            string saved = File.ReadAllText(settingsPath).Trim();
            if (saved == "uhid" || saved == "sdk") keyboardMode = saved;
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) scale = g.DpiX / 96f;
        Text = "scrcpy 侧边工具栏";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Color.FromArgb(31, 35, 43);
        Font = new Font("Microsoft YaHei UI", 9f);
        ClientSize = new Size(S(88), S(488));
        Rectangle work = Screen.PrimaryScreen.WorkingArea;
        Location = new Point(work.Right - Width - S(20), work.Top + S(80));
        try { Icon = Icon.ExtractAssociatedIcon(Path.Combine(root, "scrcpy.exe")); } catch { }

        heading = new Label {
            Text = "工具", ForeColor = Color.FromArgb(170, 183, 201),
            Location = new Point(S(10), S(7)), Size = new Size(S(44), S(22))
        };
        Controls.Add(heading);
        heading.Cursor = Cursors.SizeAll;
        BindDrag(heading);
        BindDrag(this);
        tips.SetToolTip(heading, "按住这里拖动工具栏");
        Button close = MakeButton("×", new Rectangle(59, 3, 25, 25));
        close.Font = new Font("Segoe UI", 12f);
        close.Click += delegate { ExitMirror(); };
        tips.SetToolTip(close, "退出投屏并关闭工具栏");

        int y = 34;
        AddAction("返回", "返回上一级 · Alt+B", 0x42, false, ref y);
        AddAction("桌面", "返回设备桌面 · Alt+H", 0x48, false, ref y);
        AddAction("任务", "最近任务 · Alt+S", 0x53, false, ref y);
        y += 4;
        AddAction("关屏", "关闭设备屏幕，继续投屏 · Alt+O", 0x4F, false, ref y);
        AddAction("亮屏", "点亮设备屏幕 · Alt+Shift+O", 0x4F, true, ref y);
        y += 4;
        AddAction("音量 +", "增加音量 · Alt+↑", 0x26, false, ref y);
        AddAction("音量 −", "降低音量 · Alt+↓", 0x28, false, ref y);
        y += 4;
        qualityButton = MakeButton("画质", new Rectangle(7, y, 74, 32));
        qualityButton.Click += delegate { ShowQualityMenu(); };
        actions.Add(qualityButton);
        qualityMenu = new ContextMenuStrip();
        foreach (QualityPreset preset in QualityPresets)
        {
            ToolStripMenuItem item = new ToolStripMenuItem(
                preset.Name + "（" + preset.BitRate + " / " + preset.MaxFps + " FPS）");
            item.Tag = preset;
            item.Click += delegate(object sender, EventArgs e) {
                QualityPreset selected = (QualityPreset)((ToolStripMenuItem)sender).Tag;
                qualityMenu.Close();
                ApplyQuality(selected);
            };
            qualityMenu.Items.Add(item);
        }
        tips.SetToolTip(qualityButton, "当前画质：高清（20M / 60 FPS），点击切换；切换时会重启投屏");
        y += 36;
        fullscreenButton = AddAction("全屏", "全屏 / 退出全屏 · Alt+F", 0x46, false, ref y);
        AddAction("旋转", "旋转设备屏幕 · Alt+R", 0x52, false, ref y);
        AddAction("通知", "展开通知栏 · Alt+N", 0x4E, false, ref y);
        AddAction("粘贴", "粘贴电脑剪贴板中的文字 · Alt+V", 0x56, false, ref y);
        keyboardButton = MakeButton("电脑键盘", new Rectangle(7, y, 74, 32));
        keyboardButton.Click += delegate { ShowKeyboardMenu(); };
        actions.Add(keyboardButton);
        y += 36;
        keyboardMenu = new ContextMenuStrip();
        AddKeyboardMode("实体键盘（推荐，手机输入法处理中文）", "uhid");
        AddKeyboardMode("兼容模式（英文 / 数字，中文用粘贴）", "sdk");
        keyboardMenu.Items.Add(new ToolStripSeparator());
        ToolStripMenuItem keyboardSettings = new ToolStripMenuItem("打开手机的实体键盘设置");
        keyboardSettings.Click += delegate { SendShortcut(0x4B, false); };
        keyboardMenu.Items.Add(keyboardSettings);
        ToolStripMenuItem keyboardHelp = new ToolStripMenuItem("怎么输入中文？");
        keyboardHelp.Click += delegate {
            MessageBox.Show("1. 选择“实体键盘”，等待投屏重新连接。\n" +
                "2. 在投屏里点击手机的输入框，再用电脑键盘打字。\n" +
                "3. 中文由手机输入法处理；电脑输入法先切到英文状态。\n" +
                "4. 首次使用可打开实体键盘设置，配置布局和支持实体键盘的中文输入法。\n\n" +
                "若手机输入法不支持，可在电脑上输入中文并复制，再点工具栏“粘贴”。\n" +
                "键盘只在投屏窗口获得焦点时输入，切回电脑应用后正常操作电脑。",
                "电脑键盘输入", MessageBoxButtons.OK, MessageBoxIcon.Information);
        };
        keyboardMenu.Items.Add(keyboardHelp);
        UpdateKeyboardTip();

        y += 4;
        Button exit = MakeButton("退出投屏", new Rectangle(7, y, 74, 32));
        exit.BackColor = Color.FromArgb(104, 47, 54);
        exit.FlatAppearance.MouseOverBackColor = Color.FromArgb(139, 57, 65);
        exit.Click += delegate { ExitMirror(); };
        tips.SetToolTip(exit, "关闭电脑上的投屏窗口和工具栏");
        actions.Add(exit);
        y += 36;

        Button side = MakeButton("换一侧", new Rectangle(7, y + 3, 74, 27));
        side.ForeColor = Color.FromArgb(170, 183, 201);
        side.Click += delegate {
            preferLeft = !preferLeft;
            if (fullscreen) fullscreenFloating = false; else windowFloating = false;
            Reposition();
        };
        tips.SetToolTip(side, "在投屏窗口左侧与右侧之间切换");
        y += 32;
        foldButton = MakeButton("收起工具栏", new Rectangle(7, y + 3, 74, 27));
        foldButton.Click += delegate { SetCollapsed(!collapsed); Reposition(); };
        tips.SetToolTip(foldButton, "展开 / 收起；按住顶部“工具”可拖动");
        status.Text = "正在连接";
        status.ForeColor = Color.FromArgb(115, 175, 240);
        status.TextAlign = ContentAlignment.MiddleCenter;
        status.SetBounds(S(3), S(y + 32), S(82), S(22));
        Controls.Add(status);
        ClientSize = new Size(S(88), S(y + 57));
        expandedHeight = Height;
        foreach (Control control in Controls)
        {
            control.MouseEnter += delegate { lastInteraction = DateTime.UtcNow; };
            control.MouseMove += delegate { lastInteraction = DateTime.UtcNow; };
        }
        foreach (Button b in actions) b.Enabled = false;
        follow.Interval = 120;
        follow.Tick += Poll;
        Shown += delegate { StartMirror(); follow.Start(); };
        FormClosed += delegate {
            closing = true; follow.Stop(); follow.Dispose(); qualityMenu.Dispose();
            keyboardMenu.Dispose(); tips.Dispose();
        };
    }

    protected override bool ShowWithoutActivation { get { return true; } }
    protected override CreateParams CreateParams
    {
        get { CreateParams cp = base.CreateParams; cp.ExStyle |= 0x08000080; return cp; }
    }
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x21) { m.Result = new IntPtr(3); return; }
        base.WndProc(ref m);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using (var pen = new Pen(Color.FromArgb(64, 74, 91)))
            e.Graphics.DrawRectangle(pen, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
    }
    private int S(int n) { return (int)Math.Round(n * scale); }
    private void SetCollapsed(bool value)
    {
        collapsed = value;
        foreach (Control control in Controls)
            control.Visible = !value || control == heading || control == foldButton;
        foldButton.Text = value ? "展开" : "收起工具栏";
        foldButton.SetBounds(value ? S(48) : S(7), value ? S(3) : status.Top - S(29),
            value ? S(36) : S(74), value ? S(26) : S(27));
        Height = value ? S(32) : expandedHeight;
        lastInteraction = DateTime.UtcNow;
    }
    private void BindDrag(Control control)
    {
        control.MouseDown += delegate(object sender, MouseEventArgs e) {
            if (e.Button != MouseButtons.Left) return;
            dragging = true;
            dragMouse = Cursor.Position;
            dragOrigin = Location;
            control.Capture = true;
        };
        control.MouseMove += delegate {
            if (!dragging) return;
            Rectangle work = fullscreen ? Screen.FromHandle(target).Bounds : Screen.FromHandle(target).WorkingArea;
            Point mouse = Cursor.Position;
            Location = ClampLocation(new Point(dragOrigin.X + mouse.X - dragMouse.X,
                dragOrigin.Y + mouse.Y - dragMouse.Y), work);
            RememberPosition(work);
        };
        control.MouseUp += delegate(object sender, MouseEventArgs e) {
            if (e.Button != MouseButtons.Left || !dragging) return;
            dragging = false;
            control.Capture = false;
            lastInteraction = DateTime.UtcNow;
        };
        control.MouseCaptureChanged += delegate { if (!control.Capture) dragging = false; };
    }
    private Point ClampLocation(Point point, Rectangle work)
    {
        return new Point(Math.Max(work.Left, Math.Min(point.X, work.Right - Width)),
            Math.Max(work.Top, Math.Min(point.Y, work.Bottom - Height)));
    }
    private void RememberPosition(Rectangle work)
    {
        if (fullscreen)
        {
            fullscreenFloating = true;
            fullscreenOffset = new Point(Left - work.Left, Top - work.Top);
        }
        else
        {
            Native.Rect rect;
            if (!Native.GetWindowRect(target, out rect)) return;
            windowFloating = true;
            windowOffset = new Point(Left - rect.Left, Top - rect.Top);
        }
    }
    private Button MakeButton(string text, Rectangle rect)
    {
        var b = new Button {
            Text = text, FlatStyle = FlatStyle.Flat, TabStop = false,
            ForeColor = Color.FromArgb(235, 239, 245), BackColor = Color.FromArgb(43, 49, 60),
            Cursor = Cursors.Hand, UseVisualStyleBackColor = false
        };
        b.SetBounds(S(rect.X), S(rect.Y), S(rect.Width), S(rect.Height));
        b.FlatAppearance.BorderSize = 0;
        b.FlatAppearance.MouseOverBackColor = Color.FromArgb(58, 80, 110);
        b.FlatAppearance.MouseDownBackColor = Color.FromArgb(47, 108, 179);
        Controls.Add(b);
        return b;
    }
    private Button AddAction(string text, string tip, ushort key, bool shift, ref int y)
    {
        Button b = MakeButton(text, new Rectangle(7, y, 74, 32));
        b.Click += delegate { SendShortcut(key, shift); };
        tips.SetToolTip(b, tip);
        actions.Add(b);
        y += 36;
        return b;
    }

    private void ShowQualityMenu()
    {
        if (!ready || restarting) return;
        foreach (ToolStripMenuItem item in qualityMenu.Items)
        {
            QualityPreset preset = (QualityPreset)item.Tag;
            item.Checked = mirrorQualityKnown && preset.BitRate == quality.BitRate && preset.MaxFps == quality.MaxFps;
        }
        qualityMenu.Show(qualityButton, new Point(qualityButton.Width, 0));
    }

    private void ApplyQuality(QualityPreset selected)
    {
        if (!ready || restarting || mirror == null || !Native.IsWindow(target)) return;
        if (mirrorQualityKnown && selected.BitRate == quality.BitRate && selected.MaxFps == quality.MaxFps)
        {
            tips.Show("当前已经是" + selected.Name + "（" + selected.BitRate + " / " + selected.MaxFps + " FPS）",
                this, S(5), Height - S(20), 2200);
            return;
        }
        RestartMirror(selected, keyboardMode, "正在切换画质");
    }

    private void AddKeyboardMode(string label, string mode)
    {
        ToolStripMenuItem item = new ToolStripMenuItem(label);
        item.Tag = mode;
        item.Click += delegate { keyboardMenu.Close(); ApplyKeyboardMode(mode); };
        keyboardMenu.Items.Add(item);
    }

    private void ShowKeyboardMenu()
    {
        if (!ready || restarting || exitRequested) return;
        foreach (ToolStripItem item in keyboardMenu.Items)
        {
            ToolStripMenuItem modeItem = item as ToolStripMenuItem;
            if (modeItem != null && modeItem.Tag is string)
                modeItem.Checked = keyboardModeKnown && (string)modeItem.Tag == keyboardMode;
        }
        keyboardMenu.Show(keyboardButton, new Point(keyboardButton.Width, 0));
    }

    private void ApplyKeyboardMode(string mode)
    {
        if (mode != "uhid" && mode != "sdk") return;
        if (!ready || restarting || exitRequested) return;
        if (keyboardModeKnown && keyboardMode == mode)
        {
            tips.Show("当前模式已启用，请点击手机输入框后打字。", this, S(5), Height - S(20), 2800);
            return;
        }
        RestartMirror(quality, mode, "正在切换键盘");
    }

    private void UpdateKeyboardTip()
    {
        tips.SetToolTip(keyboardButton, keyboardModeKnown
            ? (keyboardMode == "uhid" ? "实体键盘：点击手机输入框后打字，中文由手机输入法处理"
                : "兼容键盘：直接输入英文和数字，中文可用粘贴")
            : "已有投屏的键盘模式未知；点击选择模式，重连后生效");
    }

    private void RestartMirror(QualityPreset selectedQuality, string selectedKeyboard, string message)
    {
        if (!ready || restarting || exitRequested || mirror == null || !Native.IsWindow(target)) return;
        uint pid;
        Native.GetWindowThreadProcessId(target, out pid);
        if (pid != (uint)mirror.Id) return;
        previousQuality = quality;
        previousKeyboardMode = keyboardMode;
        quality = selectedQuality;
        keyboardMode = selectedKeyboard;
        restartWindowArguments = "";
        Native.Rect rect;
        if (fullscreen) restartWindowArguments = " --fullscreen";
        else if (Native.GetWindowRect(target, out rect))
            restartWindowArguments = " --window-x=" + rect.Left + " --window-y=" + rect.Top;
        restarting = true;
        restartWait.Restart();
        status.Text = message;
        foreach (Button b in actions) b.Enabled = false;
        // Windows destroys owned windows with their owner. Detach BEFORE closing
        // scrcpy so this toolbar survives long enough to start the replacement.
        SetFullscreenLayer(false);
        Native.SetOwner(Handle, IntPtr.Zero);
        if (!Native.PostMessage(target, 0x0010, IntPtr.Zero, IntPtr.Zero)) CancelRestart();
    }

    private void CancelRestart()
    {
        restarting = false;
        restartWait.Stop();
        quality = previousQuality;
        keyboardMode = previousKeyboardMode;
        if (Native.IsWindow(target)) Native.SetOwner(Handle, target);
        status.Text = "已连接";
        foreach (Button b in actions) b.Enabled = true;
        tips.Show("投屏未能关闭，设置尚未应用，请稍后重试。", this, S(5), Height - S(20), 3000);
    }

    private void StartMirror()
    {
        string exe = Path.Combine(root, "scrcpy.exe");
        if (!File.Exists(exe) || !File.Exists(Path.Combine(root, "adb.exe")))
            throw new FileNotFoundException("请将 toolbar 文件夹保留在 scrcpy 安装目录内。");
        foreach (Process p in Process.GetProcessesByName("scrcpy"))
        {
            try
            {
                if (p.MainWindowHandle != IntPtr.Zero &&
                    string.Equals(p.MainModule.FileName, exe, StringComparison.OrdinalIgnoreCase))
                { mirror = p; mirrorQualityKnown = false; break; }
            }
            catch { }
            p.Dispose();
        }
        if (mirror != null)
        {
            ReadAttachedOptions();
            UpdateKeyboardTip();
            return;
        }

        LaunchMirror();
    }

    private void ReadAttachedOptions()
    {
        // Best effort: an attached mirror may have been started with different
        // quality/device options. Only carry validated argument values forward.
        try
        {
            using (var search = new ManagementObjectSearcher(
                "SELECT CommandLine FROM Win32_Process WHERE ProcessId=" + mirror.Id))
            {
                search.Options.Timeout = TimeSpan.FromSeconds(2);
                using (ManagementObjectCollection rows = search.Get())
                    foreach (ManagementObject row in rows)
                        using (row)
                            ReadAttachedCommandLine(row["CommandLine"] as string ?? "");
            }
        }
        catch (ManagementException) { }
        catch (UnauthorizedAccessException) { }
        catch (System.Runtime.InteropServices.COMException) { }
    }

    private void ReadAttachedCommandLine(string commandLine)
    {
        Match bitrate = Regex.Match(commandLine, @"(?:^|\s)--video-bit-rate[=\s]+(\d+[KMG]?)(?=\s|$)", RegexOptions.IgnoreCase);
        Match fps = Regex.Match(commandLine, @"(?:^|\s)--max-fps[=\s]+(\d+)(?=\s|$)");
        if (bitrate.Success && fps.Success)
        {
            quality = new QualityPreset("当前画质", bitrate.Groups[1].Value, fps.Groups[1].Value);
            mirrorQualityKnown = true;
        }
        Match keyboard = Regex.Match(commandLine, @"(?:^|\s)--keyboard[=\s]+(uhid|sdk)(?=\s|$)");
        if (keyboard.Success)
        {
            keyboardMode = keyboard.Groups[1].Value;
            keyboardModeKnown = true;
        }
        Match serial = Regex.Match(commandLine, @"(?:^|\s)--serial[=\s]+([A-Za-z0-9_.:\[\]-]+)(?=\s|$)");
        if (serial.Success) deviceSerial = serial.Groups[1].Value;
    }

    private void LaunchMirror()
    {
        string exe = Path.Combine(root, "scrcpy.exe");
        logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "scrcpy.log");
        string arguments = BuildMirrorArguments();
        lock (logLock) recentLog.Clear();
        File.WriteAllText(logPath, "scrcpy 工具栏启动日志 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") +
            Environment.NewLine + "启动参数：" + arguments + Environment.NewLine, Encoding.UTF8);
        var info = new ProcessStartInfo(exe, arguments) {
            WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        info.EnvironmentVariables["ADB"] = Path.Combine(root, "adb.exe");
        mirror = new Process { StartInfo = info };
        mirror.OutputDataReceived += LogLine;
        mirror.ErrorDataReceived += LogLine;
        mirror.Start();
        mirror.BeginOutputReadLine();
        mirror.BeginErrorReadLine();
        mirrorQualityKnown = true;
        keyboardModeKnown = true;
        UpdateKeyboardTip();
    }

    private string BuildMirrorArguments()
    {
        return "--shortcut-mod=lalt --stay-awake --video-bit-rate=" + quality.BitRate +
            " --max-fps=" + quality.MaxFps + " --keyboard=" + keyboardMode +
            (string.IsNullOrEmpty(deviceSerial) ? "" : " --serial=" + deviceSerial) + restartWindowArguments;
    }
    private void LogLine(object sender, DataReceivedEventArgs e)
    {
        if (e.Data == null) return;
        lock (logLock)
        {
            recentLog.AppendLine(e.Data);
            if (recentLog.Length > 6000) recentLog.Remove(0, recentLog.Length - 6000);
            try { File.AppendAllText(logPath, e.Data + Environment.NewLine, Encoding.UTF8); } catch { }
        }
    }
    private void Poll(object sender, EventArgs e)
    {
        if (closing || mirror == null) return;
        try
        {
            mirror.Refresh();
            if (mirror.HasExited)
            {
                if (restarting)
                {
                    mirror.WaitForExit(1000);
                    mirror.Dispose();
                    mirror = null;
                    target = IntPtr.Zero;
                    ready = false;
                    exitRequested = false;
                    startup.Restart();
                    restarting = false;
                    restartWait.Stop();
                    LaunchMirror();
                    return;
                }
                follow.Stop();
                if (!ready) ShowStartupError();
                Close(); return;
            }
            if (restarting)
            {
                if (restartWait.Elapsed.TotalSeconds > 8) CancelRestart();
                return;
            }
            IntPtr hwnd = mirror.MainWindowHandle;
            if (hwnd == IntPtr.Zero)
            {
                if (startup.Elapsed.TotalSeconds > 25)
                {
                    follow.Stop();
                    ShowStartupError();
                    Close();
                }
                return;
            }
            if (hwnd != target)
            {
                target = hwnd;
                Native.SetOwner(Handle, target);
            }
            if (!ready)
            {
                ready = true;
                foreach (Button b in actions) b.Enabled = true;
                status.Text = "已连接";
                if (keyboardModeKnown)
                {
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath));
                        File.WriteAllText(settingsPath, keyboardMode, Encoding.UTF8);
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
            if (Native.IsIconic(target) || !Native.IsWindowVisible(target))
            {
                SetFullscreenLayer(false);
                if (Visible) Hide();
                return;
            }
            Reposition();
            if (!Visible) Show();
        }
        catch (InvalidOperationException) { Close(); }
    }
    private void ShowStartupError()
    {
        string details;
        lock (logLock) details = recentLog.ToString();
        if (details.Length > 2000) details = details.Substring(details.Length - 2000);
        MessageBox.Show("投屏尚未成功启动。请确认设备已连接，并已允许 USB 调试。\n\n" +
            details + "\n日志：" + logPath, "scrcpy 启动提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }
    private void Reposition()
    {
        Native.Rect rect;
        if (target == IntPtr.Zero || !Native.GetWindowRect(target, out rect)) return;
        Screen screen = Screen.FromHandle(target);
        Rectangle bounds = screen.Bounds;
        bool wasFullscreen = fullscreen;
        fullscreen = (Native.GetStyle(target) & 0x00C00000L) == 0 &&
            rect.Left <= bounds.Left + 2 && rect.Top <= bounds.Top + 2 &&
            rect.Right >= bounds.Right - 2 && rect.Bottom >= bounds.Bottom - 2;
        if (fullscreen != wasFullscreen) SetCollapsed(fullscreen);
        if (fullscreen && !collapsed && !dragging && !qualityMenu.Visible && !keyboardMenu.Visible &&
            !Bounds.Contains(Cursor.Position) &&
            (DateTime.UtcNow - lastInteraction).TotalSeconds >= 3) SetCollapsed(true);
        fullscreenButton.Text = fullscreen ? "退出全屏" : "全屏";
        // SDL may raise its fullscreen window above an ordinary owned window.
        // Raise the toolbar without activating it only while this mirror is active.
        SetFullscreenLayer(fullscreen && Native.GetForegroundWindow() == target);
        Rectangle work = fullscreen ? bounds : screen.WorkingArea;
        if (dragging) return;
        if (fullscreen && fullscreenFloating)
        {
            Location = ClampLocation(new Point(work.Left + fullscreenOffset.X, work.Top + fullscreenOffset.Y), work);
            return;
        }
        if (!fullscreen && windowFloating)
        {
            Location = ClampLocation(new Point(rect.Left + windowOffset.X, rect.Top + windowOffset.Y), work);
            return;
        }
        int gap = S(6);
        int right = rect.Right + gap;
        int left = rect.Left - Width - gap;
        int x;
        if (preferLeft) x = left >= work.Left ? left : (right + Width <= work.Right ? right : rect.Left + gap);
        else x = right + Width <= work.Right ? right : (left >= work.Left ? left : rect.Right - Width - gap);
        x = Math.Max(work.Left, Math.Min(x, work.Right - Width));
        int y = Math.Max(work.Top, Math.Min(rect.Top, work.Bottom - Height));
        if (Left != x || Top != y) Location = new Point(x, y);
    }
    private void SetFullscreenLayer(bool raise)
    {
        if (raise || raisedForFullscreen)
            Native.SetWindowPos(Handle, new IntPtr(raise ? -1 : -2), 0, 0, 0, 0, 0x0013);
        raisedForFullscreen = raise;
    }
    private void ExitMirror()
    {
        if (restarting) return;
        if (exitRequested) return;
        if (mirror == null || !ready) { Close(); return; }
        uint pid;
        Native.GetWindowThreadProcessId(target, out pid);
        if (!Native.IsWindow(target) || pid != (uint)mirror.Id) { Close(); return; }
        if (Native.PostMessage(target, 0x0010, IntPtr.Zero, IntPtr.Zero))
        {
            exitRequested = true;
            status.Text = "正在退出";
            foreach (Button b in actions) b.Enabled = false;
        }
        else tips.Show("关闭请求未成功，请点击投屏窗口后按 Alt+F4。", this, S(5), Height - S(20), 3000);
    }
    private void SendShortcut(ushort key, bool shift)
    {
        if (!ready || !Native.IsWindow(target)) return;
        uint pid;
        Native.GetWindowThreadProcessId(target, out pid);
        if (mirror == null || pid != (uint)mirror.Id) return;
        if (Native.IsIconic(target)) Native.ShowWindow(target, 9);
        Native.SetForegroundWindow(target);
        if (Native.GetForegroundWindow() != target)
        {
            tips.Show("请先点击一下投屏窗口，再试此按钮。", this, S(5), Height - S(20), 2500);
            return;
        }
        var inputs = new List<Native.Input>();
        inputs.Add(Native.Key(0xA4, false));
        if (shift) inputs.Add(Native.Key(0xA0, false));
        inputs.Add(Native.Key(key, false));
        inputs.Add(Native.Key(key, true));
        if (shift) inputs.Add(Native.Key(0xA0, true));
        inputs.Add(Native.Key(0xA4, true));
        uint sent = Native.SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf(typeof(Native.Input)));
        if (sent != inputs.Count)
            tips.Show("按键发送失败，请让投屏和工具栏使用相同权限运行。", this, S(5), Height - S(20), 2500);
    }
}

internal static class Native
{
    [StructLayout(LayoutKind.Sequential)] internal struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] internal struct KeyboardInput
    { public ushort Vk, Scan; public uint Flags, Time; public UIntPtr ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] internal struct MouseInput
    { public int X, Y; public uint Data, Flags, Time; public UIntPtr ExtraInfo; }
    [StructLayout(LayoutKind.Explicit)] internal struct InputUnion
    { [FieldOffset(0)] public KeyboardInput Keyboard; [FieldOffset(0)] public MouseInput Mouse; }
    [StructLayout(LayoutKind.Sequential)] internal struct Input
    { public uint Type; public InputUnion Data; }
    internal static Input Key(ushort key, bool up)
    {
        return new Input { Type = 1, Data = new InputUnion {
            // SDL uses the scan code and extended flag to distinguish arrows from numpad keys.
            Keyboard = new KeyboardInput { Vk = key, Scan = (ushort)MapVirtualKey(key, 0),
                Flags = (up ? 2u : 0u) | (key >= 0x21 && key <= 0x2E ? 1u : 0u) }
        }};
    }
    [DllImport("user32.dll")] internal static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] internal static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] internal static extern uint MapVirtualKey(uint code, uint mapType);
    [DllImport("user32.dll")] internal static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong32(IntPtr hwnd, int index);
    internal static long GetStyle(IntPtr hwnd) { return (long)(uint)GetWindowLong32(hwnd, -16); }
    [DllImport("user32.dll", SetLastError = true)] internal static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr64(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] private static extern int SetWindowLong32(IntPtr hwnd, int index, int value);
    internal static void SetOwner(IntPtr toolbar, IntPtr owner)
    {
        if (IntPtr.Size == 8) SetWindowLongPtr64(toolbar, -8, owner);
        else SetWindowLong32(toolbar, -8, owner.ToInt32());
    }
}
