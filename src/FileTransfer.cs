using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

internal sealed class DeviceFile
{
    internal string Name, Path;
    internal bool IsDirectory;
    internal long Size;
    internal DateTime Modified;
}

internal sealed class AdbFiles
{
    private readonly string adb;
    private string serial;
    private bool fastListing = true;

    internal AdbFiles(string adb, string serial) { this.adb = adb; this.serial = serial; }

    internal static string NormalizePath(string path)
    {
        path = (path ?? "").TrimEnd('/');
        if (path == "/storage/emulated/0" || path.StartsWith("/storage/emulated/0/", StringComparison.Ordinal))
            path = "/sdcard" + path.Substring("/storage/emulated/0".Length);
        if (path != "/sdcard" && !path.StartsWith("/sdcard/", StringComparison.Ordinal))
            throw new IOException("请输入手机内部存储路径，例如 /sdcard/Download。");
        var parts = new List<string>();
        foreach (string part in path.Substring(7).Split('/'))
        {
            if (part == "" || part == ".") continue;
            if (part == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); }
            else
            {
                if (part.IndexOf('\0') >= 0 || part.IndexOf('\r') >= 0 || part.IndexOf('\n') >= 0)
                    throw new IOException("目录路径包含不支持的字符。");
                parts.Add(part);
            }
        }
        return "/sdcard" + (parts.Count == 0 ? "" : "/" + string.Join("/", parts));
    }

    internal static string ShellQuote(string value) { return "'" + value.Replace("'", "'\"'\"'") + "'"; }

    internal static string Arguments(params string[] values)
    {
        var result = new List<string>();
        foreach (string value in values) result.Add(FilePush.Quote(value));
        return string.Join(" ", result);
    }

    private string SelectedSerial(CancellationToken cancel)
    {
        if (!string.IsNullOrEmpty(serial)) return serial;
        string devices = Run(Arguments("devices"), null, cancel, null);
        var online = new List<string>();
        foreach (string line in devices.Split('\n'))
        {
            string[] fields = line.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length == 2 && fields[1] == "device") online.Add(fields[0]);
        }
        if (online.Count != 1)
            throw new IOException("无法确认投屏设备。请只保留一台已授权设备，或使用 --serial 指定设备后重新打开文件传输。");
        serial = online[0];
        return serial;
    }

    internal List<DeviceFile> List(string directory, CancellationToken cancel)
    {
        directory = NormalizePath(directory);
        if (fastListing)
        {
            // One traversal, no per-entry stat processes. Toybox supplies NUL
            // framing and 64-bit sizes without depending on the device locale.
            string batch = "cd " + ShellQuote(directory) + " || exit 1\n" +
                "[ -r . ] && [ -x . ] || { echo 'Directory access denied' >&2; exit 1; }\n" +
                "find . -mindepth 1 -maxdepth 1 \\( -type f -o -type d \\) -printf '%M\\0%s %T@\\0%f\\0'\n";
            try
            {
                return ParseList(directory, Run(Arguments("-s", SelectedSerial(cancel), "shell", "-T", "sh"), batch, cancel, null));
            }
            catch (IOException ex)
            {
                // Older toybox builds may not implement find -printf.
                if (!ex.Message.Contains("-printf") && !ex.Message.Contains("bad arg") && !ex.Message.Contains("Unknown option")) throw;
                fastListing = false;
            }
        }
        // Names are NUL-delimited: spaces, Unicode and newlines are never split.
        // Supply the script on stdin so a filename cannot become shell syntax.
        string script = "cd " + ShellQuote(directory) + " || exit 1\n" +
            "[ -r . ] && [ -x . ] || { echo 'Directory access denied' >&2; exit 1; }\n" +
            "for f in ./* ./.[!.]* ./..?*; do\n" +
            "  [ -L \"$f\" ] && continue\n" +
            "  if [ -d \"$f\" ]; then t=d; elif [ -f \"$f\" ]; then t=f; else continue; fi\n" +
            "  m=$(stat -c '%s %Y' \"$f\") || exit 1\n" +
            "  printf '%s\\0%s\\0%s\\0' \"$t\" \"$m\" \"${f#./}\"\n" +
            "done\n";
        string output = Run(Arguments("-s", SelectedSerial(cancel), "shell", "-T", "sh"), script, cancel, null);
        return ParseList(directory, output);
    }

    internal static List<DeviceFile> ParseList(string directory, string output)
    {
        var result = new List<DeviceFile>();
        string[] fields = output.Split('\0');
        if (fields.Length % 3 != 1 || fields[fields.Length - 1] != "")
            throw new IOException("手机返回的目录信息不完整，请刷新重试。");
        for (int i = 0; i < fields.Length - 1; i += 3)
        {
            string[] meta = fields[i + 1].Split(' ');
            long size;
            decimal timestamp;
            string name = fields[i + 2];
            bool directoryEntry = fields[i] == "d" || fields[i].StartsWith("d", StringComparison.Ordinal);
            bool fileEntry = fields[i] == "f" || fields[i].StartsWith("-", StringComparison.Ordinal);
            if ((!directoryEntry && !fileEntry) || meta.Length != 2 ||
                !long.TryParse(meta[0], NumberStyles.None, CultureInfo.InvariantCulture, out size) ||
                !decimal.TryParse(meta[1], NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out timestamp) || size < 0 ||
                name.Length == 0 || name == "." || name == ".." || name.Contains("/"))
                throw new IOException("无法读取手机文件信息，请刷新重试。");
            DateTime modified;
            try { modified = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds((double)timestamp).ToLocalTime(); }
            catch (ArgumentOutOfRangeException) { modified = DateTime.MinValue; }
            result.Add(new DeviceFile { Name = name, Path = directory + "/" + name,
                IsDirectory = directoryEntry, Size = size, Modified = modified });
        }
        result.Sort(delegate(DeviceFile a, DeviceFile b) {
            int kind = b.IsDirectory.CompareTo(a.IsDirectory);
            return kind != 0 ? kind : StringComparer.CurrentCultureIgnoreCase.Compare(a.Name, b.Name);
        });
        return result;
    }

    internal static string LocalName(string name)
    {
        var result = new StringBuilder();
        foreach (char ch in name)
            result.Append(ch < 32 || Array.IndexOf(Path.GetInvalidFileNameChars(), ch) >= 0 ? '_' : ch);
        string safe = result.ToString().TrimEnd(' ', '.');
        if (safe.Length == 0) safe = "未命名文件";
        string stem = safe.Split('.')[0].ToUpperInvariant();
        if (System.Text.RegularExpressions.Regex.IsMatch(stem, @"^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])$")) safe = "_" + safe;
        return safe;
    }

    internal string Pull(DeviceFile file, string folder, CancellationToken cancel, Action<long> progress)
    {
        if (file.IsDirectory) throw new IOException("请进入文件夹，选择其中的文件。");
        string selected = SelectedSerial(cancel);
        string temporary = Path.Combine(folder, ".scrcpy-" + Guid.NewGuid().ToString("N") + ".partial");
        try
        {
            Run(Arguments("-s", selected, "pull", file.Path, temporary), null, cancel, delegate {
                if (progress != null) progress(File.Exists(temporary) ? new FileInfo(temporary).Length : 0);
            });
            cancel.ThrowIfCancellationRequested();
            if (!File.Exists(temporary) || new FileInfo(temporary).Length != file.Size)
                throw new IOException("文件大小已变化或传输不完整，请刷新列表后重试。");
            string name = LocalName(file.Name);
            string stem = Path.GetFileNameWithoutExtension(name), extension = Path.GetExtension(name);
            for (int index = 0; ; index++)
            {
                string destination = Path.Combine(folder, index == 0 ? name : stem + " (" + index + ")" + extension);
                if (File.Exists(destination) || Directory.Exists(destination)) continue;
                try { File.Move(temporary, destination); if (progress != null) progress(file.Size); return destination; }
                catch (IOException) { if (!File.Exists(destination) && !Directory.Exists(destination)) throw; }
            }
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private string Run(string arguments, string input, CancellationToken cancel, Action progress)
    {
        cancel.ThrowIfCancellationRequested();
        var info = new ProcessStartInfo(adb, arguments) {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = input != null,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        using (var process = new Process { StartInfo = info })
        {
            process.Start();
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            try
            {
                if (input != null)
                {
                    byte[] bytes = new UTF8Encoding(false).GetBytes(input);
                    process.StandardInput.BaseStream.Write(bytes, 0, bytes.Length);
                    process.StandardInput.Close();
                }
                Stopwatch elapsed = Stopwatch.StartNew();
                while (!process.WaitForExit(150))
                {
                    cancel.ThrowIfCancellationRequested();
                    if (input != null && elapsed.Elapsed.TotalSeconds > 30)
                        throw new IOException("读取目录超时，请检查手机连接后刷新。");
                    if (progress != null) progress();
                }
                cancel.ThrowIfCancellationRequested();
                if (process.ExitCode != 0)
                    throw new IOException("设备操作失败，请检查连接与目录权限。\n" + stderr.Result.Trim());
                return stdout.Result;
            }
            finally
            {
                if (!process.HasExited)
                {
                    try { process.Kill(); process.WaitForExit(); } catch (InvalidOperationException) { }
                }
            }
        }
    }
}

internal sealed class FileTransferForm : Form
{
    private readonly AdbFiles device;
    private readonly ListView files = new ListView();
    private readonly TextBox location = new TextBox();
    private readonly Label feedback = new Label();
    private readonly ProgressBar progress = new ProgressBar();
    private readonly Button save = new Button(), cancel = new Button(), openFolder = new Button();
    private readonly Button up = new Button();
    private readonly FlowLayoutPanel shortcuts = new FlowLayoutPanel();
    private readonly FlowLayoutPanel navigation = new FlowLayoutPanel();
    private CancellationTokenSource operation;
    private string currentDirectory = "/sdcard/Download", lastFolder;
    private bool busy, closeAfterCancel;
    private readonly Dictionary<string, CachedDirectory> directoryCache = new Dictionary<string, CachedDirectory>();
    internal bool IsBusy { get { return busy; } }

    internal FileTransferForm(string adb, string serial)
    {
        SuspendLayout();
        device = new AdbFiles(adb, serial);
        Text = "文件传输 · 手机到电脑 · v0.5.7";
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Microsoft YaHei UI", 10f);
        ClientSize = new Size(760, 530);
        MinimumSize = new Size(620, 430);
        BackColor = SystemColors.Control;
        ForeColor = SystemColors.ControlText;
        Padding = new Padding(14);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 7, ColumnCount = 1 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int row = 0; row < 3; row++) layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        for (int row = 0; row < 3; row++) layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var instructions = new Label { Text = "双击打开文件夹；Ctrl / Shift 多选。拖到电脑文件夹即可复制。", AutoSize = true,
            Dock = DockStyle.Fill, Margin = new Padding(3, 3, 3, 8) };
        layout.Controls.Add(instructions, 0, 0);
        shortcuts.Dock = DockStyle.Fill;
        shortcuts.AutoSize = true;
        shortcuts.WrapContents = true;
        AddButton(shortcuts, "内部存储", delegate { Navigate("/sdcard"); });
        AddButton(shortcuts, "下载", delegate { Navigate("/sdcard/Download"); });
        AddButton(shortcuts, "图片", delegate { Navigate("/sdcard/Pictures"); });
        AddButton(shortcuts, "相机", delegate { Navigate("/sdcard/DCIM/Camera"); });
        layout.Controls.Add(shortcuts, 0, 1);
        var pathRow = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 3, RowCount = 1, Margin = new Padding(3) };
        pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        pathRow.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        location.Dock = DockStyle.Fill;
        location.Text = currentDirectory;
        location.KeyDown += delegate(object sender, KeyEventArgs e) {
            if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Navigate(location.Text); }
        };
        ConfigureButton(up, "上一级");
        up.Click += delegate {
            int slash = currentDirectory.LastIndexOf('/');
            Navigate(slash <= 7 ? "/sdcard" : currentDirectory.Substring(0, slash));
        };
        pathRow.Controls.Add(up, 0, 0);
        pathRow.Controls.Add(location, 1, 0);
        navigation.Dock = DockStyle.Fill;
        navigation.AutoSize = true;
        navigation.Margin = Padding.Empty;
        navigation.WrapContents = false;
        AddButton(navigation, "刷新", delegate { Navigate(currentDirectory, true); });
        pathRow.Controls.Add(navigation, 2, 0);
        layout.Controls.Add(pathRow, 0, 2);
        files.Dock = DockStyle.Fill;
        files.View = View.Details;
        files.FullRowSelect = true;
        files.HideSelection = false;
        files.MultiSelect = true;
        files.BackColor = SystemColors.Window;
        files.ForeColor = SystemColors.WindowText;
        files.Columns.Add("名称", 340);
        files.Columns.Add("类型", 70);
        files.Columns.Add("大小", 100);
        files.Columns.Add("修改时间", 165);
        files.SelectedIndexChanged += delegate { UpdateSaveButton(); };
        files.ItemDrag += delegate { DragSelected(); };
        files.ClientSizeChanged += delegate { ResizeColumns(); };
        files.DoubleClick += delegate {
            if (files.SelectedItems.Count != 1) return;
            DeviceFile file = (DeviceFile)files.SelectedItems[0].Tag;
            if (file.IsDirectory) Navigate(file.Path);
        };
        layout.Controls.Add(files, 0, 3);
        progress.Dock = DockStyle.Fill;
        layout.Controls.Add(progress, 0, 4);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
        ConfigureButton(save, "保存到电脑");
        save.Click += delegate { SaveSelected(); };
        save.Enabled = false;
        buttons.Controls.Add(save);
        ConfigureButton(cancel, "取消");
        cancel.Enabled = false;
        cancel.Click += delegate { if (operation != null) operation.Cancel(); };
        buttons.Controls.Add(cancel);
        ConfigureButton(openFolder, "打开保存目录");
        openFolder.Enabled = false;
        openFolder.Click += delegate { if (Directory.Exists(lastFolder)) Process.Start("explorer.exe", AdbFiles.Arguments(lastFolder)); };
        buttons.Controls.Add(openFolder);
        layout.Controls.Add(buttons, 0, 5);
        feedback.Dock = DockStyle.Fill;
        feedback.AutoSize = true;
        feedback.MinimumSize = new Size(0, 42);
        feedback.ForeColor = SystemColors.ControlText;
        layout.Controls.Add(feedback, 0, 6);
        layout.SizeChanged += delegate {
            int available = Math.Max(1, layout.ClientSize.Width - 6);
            instructions.MaximumSize = new Size(available, 0);
            feedback.MaximumSize = new Size(available, 0);
        };
        Controls.Add(layout);
        Shown += delegate { Navigate(currentDirectory); };
        FormClosing += delegate(object sender, FormClosingEventArgs e) {
            if (!busy) return;
            e.Cancel = true;
            closeAfterCancel = true;
            operation.Cancel();
            feedback.Text = "正在取消，请稍候…";
        };
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96, 96);
        ResumeLayout(true);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && operation != null) operation.Cancel();
        base.Dispose(disposing);
    }

    private static void ConfigureButton(Button button, string text)
    {
        button.Text = text;
        button.AutoSize = true;
        button.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        button.MinimumSize = new Size(64, 32);
        button.Padding = new Padding(8, 3, 8, 3);
        button.ForeColor = SystemColors.ControlText;
        button.FlatStyle = FlatStyle.Standard;
        button.UseVisualStyleBackColor = true;
    }
    private static void AddButton(Control parent, string text, Action action)
    {
        var button = new Button();
        ConfigureButton(button, text);
        button.Click += delegate { action(); };
        parent.Controls.Add(button);
    }
    private void SetBusy(bool value)
    {
        busy = value;
        shortcuts.Enabled = navigation.Enabled = up.Enabled = location.Enabled = files.Enabled = !value;
        cancel.Enabled = value;
        UpdateSaveButton();
    }
    private void UpdateSaveButton()
    {
        int count = 0;
        foreach (ListViewItem item in files.SelectedItems) if (!((DeviceFile)item.Tag).IsDirectory) count++;
        save.Text = count == 0 ? "保存到电脑" : "保存到电脑（" + count + "）";
        save.Enabled = !busy && count > 0;
    }
    private void FinishOperation()
    {
        if (!IsDisposed && !Disposing)
        {
            SetBusy(false);
            progress.Style = ProgressBarStyle.Blocks;
        }
        operation.Dispose();
        operation = null;
        if (closeAfterCancel && !IsDisposed) Close();
    }
    private void ResizeColumns()
    {
        if (files.Columns.Count < 4) return;
        int type = TextRenderer.MeasureText("文件夹", files.Font).Width + 16;
        int size = TextRenderer.MeasureText("1023.9 MB", files.Font).Width + 20;
        int date = TextRenderer.MeasureText("2026-11-16 23:59", files.Font).Width + 20;
        files.Columns[1].Width = type;
        files.Columns[2].Width = size;
        files.Columns[3].Width = date;
        files.Columns[0].Width = Math.Max(150, files.ClientSize.Width - type - size - date - SystemInformation.VerticalScrollBarWidth - 6);
    }
    private sealed class CachedDirectory
    {
        internal DateTime Loaded;
        internal List<DeviceFile> Entries;
    }
    private async void Navigate(string directory, bool refresh = false)
    {
        if (busy) return;
        try { directory = AdbFiles.NormalizePath(directory); }
        catch (Exception ex) { feedback.Text = ex.Message; return; }
        operation = new CancellationTokenSource();
        CancellationToken token = operation.Token;
        SetBusy(true);
        feedback.Text = "正在读取手机文件…";
        progress.Style = ProgressBarStyle.Marquee;
        try
        {
            CachedDirectory cached;
            List<DeviceFile> entries;
            if (!refresh && directoryCache.TryGetValue(directory, out cached) && (DateTime.UtcNow - cached.Loaded).TotalSeconds < 15)
                entries = cached.Entries;
            else
            {
                entries = await Task.Run(() => device.List(directory, token));
                if (directoryCache.Count >= 32) directoryCache.Clear();
                directoryCache[directory] = new CachedDirectory { Loaded = DateTime.UtcNow, Entries = entries };
            }
            if (IsDisposed || Disposing) return;
            currentDirectory = directory;
            location.Text = directory;
            files.BeginUpdate();
            files.Items.Clear();
            foreach (DeviceFile entry in entries)
            {
                var item = new ListViewItem(entry.Name) { Tag = entry };
                item.SubItems.Add(entry.IsDirectory ? "文件夹" : "文件");
                item.SubItems.Add(entry.IsDirectory ? "" : FormatSize(entry.Size));
                item.SubItems.Add(entry.Modified == DateTime.MinValue ? "" : entry.Modified.ToString("yyyy-MM-dd HH:mm"));
                files.Items.Add(item);
            }
            files.EndUpdate();
            feedback.Text = entries.Count == 0 ? "此文件夹为空。" : "共 " + entries.Count + " 项；选中文件后点击“保存到电脑”。";
            progress.Value = 0;
        }
        catch (OperationCanceledException) { feedback.Text = "已取消读取。"; }
        catch (Exception ex) { feedback.Text = ex.Message; }
        finally { FinishOperation(); }
    }
    private async void SaveSelected()
    {
        if (busy) return;
        var selected = new List<DeviceFile>();
        foreach (ListViewItem item in files.SelectedItems)
        {
            DeviceFile file = (DeviceFile)item.Tag;
            if (!file.IsDirectory) selected.Add(file);
        }
        if (selected.Count == 0) return;
        using (var picker = new FolderBrowserDialog { Description = "选择电脑保存目录（同名文件自动另存，不覆盖）", SelectedPath = lastFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory) })
        {
            if (picker.ShowDialog(this) != DialogResult.OK) return;
            lastFolder = picker.SelectedPath;
        }
        operation = new CancellationTokenSource();
        CancellationToken token = operation.Token;
        SetBusy(true);
        progress.Value = 0;
        long total = 0, completedBytes = 0;
        foreach (DeviceFile file in selected) total += file.Size;
        int completed = 0;
        var failures = new List<string>();
        var updates = new Progress<TransferUpdate>(delegate(TransferUpdate update) {
            if (!busy || token.IsCancellationRequested || IsDisposed || operation == null || operation.Token != token) return;
            feedback.Text = update.Message;
            progress.Value = Math.Max(0, Math.Min(100, update.Percent));
        });
        try
        {
            await Task.Run(delegate {
                foreach (DeviceFile file in selected)
                {
                    token.ThrowIfCancellationRequested();
                    string message = "正在保存 " + (completed + 1) + "/" + selected.Count + "：" + file.Name;
                    ((IProgress<TransferUpdate>)updates).Report(new TransferUpdate { Message = message,
                        Percent = total == 0 ? 0 : (int)(completedBytes * 100.0 / total) });
                    try
                    {
                        device.Pull(file, lastFolder, token, delegate(long bytes) {
                            ((IProgress<TransferUpdate>)updates).Report(new TransferUpdate { Message = message,
                                Percent = total == 0 ? 0 : (int)((completedBytes + Math.Min(bytes, file.Size)) * 100.0 / total) });
                        });
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { failures.Add(file.Name + "：" + ex.Message); }
                    completed++;
                    completedBytes += file.Size;
                }
            });
            if (IsDisposed || Disposing) return;
            progress.Value = 100;
            int successes = completed - failures.Count;
            feedback.Text = "已保存 " + successes + " 个文件" + (failures.Count == 0 ? "。" : "，失败 " + failures.Count + " 个。") + "目录：" + lastFolder;
            if (failures.Count > 0) MessageBox.Show(this, string.Join("\n\n", failures), "部分文件未能保存", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (OperationCanceledException) { feedback.Text = "已取消；已保存 " + (completed - failures.Count) + " 个文件，未完成文件已清理。"; }
        catch (Exception ex) { feedback.Text = ex.Message; }
        finally { openFolder.Enabled = Directory.Exists(lastFolder); FinishOperation(); }
    }

    private void DragSelected()
    {
        if (busy) return;
        var selected = new List<DeviceFile>();
        foreach (ListViewItem item in files.SelectedItems)
        {
            DeviceFile file = (DeviceFile)item.Tag;
            if (!file.IsDirectory) selected.Add(file);
        }
        if (selected.Count == 0) { feedback.Text = "请进入文件夹后，选择要拖出的文件。"; return; }
        string staging = Path.Combine(Path.GetTempPath(), "ScrcpyToolbar-Drag-" + Guid.NewGuid().ToString("N"));
        operation = new CancellationTokenSource();
        CancellationToken token = operation.Token;
        SetBusy(true);
        feedback.Text = "拖到电脑文件夹或桌面后松开鼠标即可复制；Esc 取消。";
        progress.Value = 0;
        var completed = new HashSet<int>();
        long total = 0;
        foreach (DeviceFile file in selected) total += file.Size;
        long completedBytes = 0;
        var updates = new Progress<TransferUpdate>(delegate(TransferUpdate update) {
            if (operation == null || operation.Token != token || token.IsCancellationRequested || IsDisposed) return;
            feedback.Text = update.Message;
            progress.Value = Math.Max(0, Math.Min(100, update.Percent));
        });
        try
        {
            Directory.CreateDirectory(staging);
            bool copied = FileDragNative.Copy(selected, delegate(int index) {
                DeviceFile file = selected[index];
                string message = "正在复制 " + (completed.Count + 1) + "/" + selected.Count + "：" + file.Name;
                feedback.Text = message;
                Task<string> download = Task.Run(() => device.Pull(file, staging, token, delegate(long bytes) {
                    ((IProgress<TransferUpdate>)updates).Report(new TransferUpdate { Message = message,
                        Percent = total == 0 ? 0 : (int)((completedBytes + Math.Min(bytes, file.Size)) * 100.0 / total) });
                }));
                // OLE requests the stream synchronously. Pump the source UI
                // while its worker downloads, so cancel/close remain usable.
                while (!download.IsCompleted) { Application.DoEvents(); Thread.Sleep(15); }
                string path = download.GetAwaiter().GetResult();
                if (completed.Add(index)) completedBytes += file.Size;
                return path;
            }, token);
            token.ThrowIfCancellationRequested();
            progress.Value = copied ? 100 : 0;
            feedback.Text = copied ? "已将 " + selected.Count + " 个文件复制到拖放位置。" : "已取消拖出复制。";
        }
        catch (OperationCanceledException) { feedback.Text = "已取消拖出复制。"; }
        catch (Exception ex) { feedback.Text = "拖出复制失败：" + ex.Message; }
        finally
        {
            // Native streams have been released by a synchronous drop target.
            // If an external target still owns one, leave that file intact.
            try
            {
                if (Directory.Exists(staging))
                {
                    foreach (string path in Directory.GetFiles(staging))
                        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                    Directory.Delete(staging);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            finally { FinishOperation(); }
        }
    }
    private sealed class TransferUpdate { internal string Message; internal int Percent; }
    internal static string FormatSize(long size)
    {
        if (size < 1024) return size + " B";
        if (size < 1024 * 1024) return (size / 1024.0).ToString("0.0", CultureInfo.CurrentCulture) + " KB";
        if (size < 1024L * 1024 * 1024) return (size / (1024.0 * 1024)).ToString("0.0", CultureInfo.CurrentCulture) + " MB";
        return (size / (1024.0 * 1024 * 1024)).ToString("0.00", CultureInfo.CurrentCulture) + " GB";
    }
}
