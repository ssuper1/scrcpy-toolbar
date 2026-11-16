using System;
using System.Collections;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Threading;
using System.Windows.Forms;

internal static class FileTransferTests
{
    private const BindingFlags Instance = BindingFlags.NonPublic | BindingFlags.Instance;
    private const BindingFlags Static = BindingFlags.NonPublic | BindingFlags.Static;
    private static Type api;
    private static readonly byte[] Payload = Encoding.UTF8.GetBytes("hello 中文");
    private static readonly string Listing = "d\0" + "0 1700000000\0子目录\0f\0" + Payload.Length + " 1700000001\0测试 document.txt\0f\0" + "0 1700000002\0empty.txt\0";

    [STAThread]
    private static int Main(string[] args)
    {
        string self = Process.GetCurrentProcess().MainModule.FileName;
        if (Path.GetFileNameWithoutExtension(self) == "adb") return FakeAdb(args, Path.GetDirectoryName(self));
        if (Path.GetFileNameWithoutExtension(self) == "FileTransferPreview")
            args = new[] { Path.Combine(Path.GetDirectoryName(self), "toolbar", "ScrcpyToolbar.exe"), "--preview" };
        try
        {
            string root = Path.Combine(Path.GetTempPath(), "Toolbar-pull-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string adb = Path.Combine(root, "adb.exe");
            File.Copy(self, adb);
            Assembly assembly = Assembly.LoadFile(Path.GetFullPath(args[0]));
            api = assembly.GetType("AdbFiles");
            if (Array.IndexOf(args, "--preview") >= 0)
            {
                Application.EnableVisualStyles();
                var form = (Form)Activator.CreateInstance(assembly.GetType("FileTransferForm"), Instance, null, new object[] { adb, "preview-device" }, null);
                string savedFolder = Path.Combine(root, "saved");
                Directory.CreateDirectory(savedFolder);
                form.GetType().GetField("lastFolder", Instance).SetValue(form, savedFolder);
                Application.Run(form);
                return 0;
            }
            Assert((string)Call(null, "NormalizePath", "/storage/emulated/0/Download/../Pictures/") == "/sdcard/Pictures", "path alias and parent normalization");
            Assert((string)Call(null, "NormalizePath", "/sdcard/../../") == "/sdcard", "parent stays inside shared storage");
            Expect<IOException>(delegate { Call(null, "NormalizePath", "/data/data"); });
            Assert((string)Call(null, "ShellQuote", "/sdcard/a'b;$(x)") == "'/sdcard/a'\"'\"'b;$(x)'", "shell metacharacters quoted");
            Assert((string)Call(null, "LocalName", "CON.txt") == "_CON.txt", "Windows reserved basename");
            Assert((string)Call(null, "LocalName", "a:b?.txt. ") == "a_b_.txt", "invalid Windows characters normalized");
            IList parsed = (IList)Call(null, "ParseList", "/sdcard/Download", Listing);
            Assert(parsed.Count == 3 && (bool)Field(parsed[0], "IsDirectory"), "directories sorted first");
            Assert(((IList)Call(null, "ParseList", "/sdcard", "")).Count == 0, "empty directory supported");
            Expect<IOException>(delegate { Call(null, "ParseList", "/sdcard", "f\0broken"); });
            Expect<IOException>(delegate { Call(null, "ParseList", "/sdcard", "f\0" + "1 2\0../bad\0"); });
            string newline = "f\0" + "1 2\0space name\n换行\0";
            Assert(((IList)Call(null, "ParseList", "/sdcard", newline)).Count == 1, "NUL framing preserves newline names");
            string fast = "drwxr-xr-x\0" + "4096 1700000000.123456789\0目录\0-rw-rw----\0" + "5368709120 1700000001.5\0large.bin\0";
            IList fastEntries = (IList)Call(null, "ParseList", "/sdcard", fast);
            Assert(fastEntries.Count == 2 && (long)Field(fastEntries[1], "Size") == 5368709120L, "batched find modes, fractional timestamps and 64-bit sizes");
            object client = Client(adb, null);
            IList entries = (IList)Call(client, "List", "/sdcard/Download", CancellationToken.None);
            Assert(entries.Count == 3, "directory listing through actual fake process");
            string[] recorded = File.ReadAllLines(Path.Combine(root, "arguments.txt"), Encoding.UTF8);
            Assert(recorded.Length == 5 && recorded[0] == "-s" && recorded[1] == "only-device" && recorded[2] == "shell" && recorded[3] == "-T", "single connected device fixed and non-PTY shell selected");
            string script = File.ReadAllText(Path.Combine(root, "script.txt"));
            Assert(script.Contains("find . -mindepth 1 -maxdepth 1") && !script.Contains("stat -c"), "directory read batches metadata without per-file processes");
            File.WriteAllText(Path.Combine(root, "mode.txt"), "legacy-find");
            object legacy = Client(adb, "only-device");
            Assert(((IList)Call(legacy, "List", "/sdcard", CancellationToken.None)).Count == 3 && !(bool)Field(legacy, "fastListing"), "unsupported find printf falls back to legacy listing");
            Assert(File.ReadAllText(Path.Combine(root, "script.txt")).Contains("stat -c"), "legacy metadata script used after unsupported option");
            File.WriteAllText(Path.Combine(root, "mode.txt"), "normal");
            object file = null, empty = null;
            foreach (object entry in entries)
            {
                if ((string)Field(entry, "Name") == "测试 document.txt") file = entry;
                if ((string)Field(entry, "Name") == "empty.txt") empty = entry;
            }
            string output = Path.Combine(root, "output");
            Directory.CreateDirectory(output);
            string original = Path.Combine(output, "测试 document.txt");
            File.WriteAllText(original, "keep original");
            long reported = -1;
            string saved = (string)Call(client, "Pull", file, output, CancellationToken.None, (Action<long>)(bytes => reported = bytes));
            Assert(File.ReadAllText(original) == "keep original" && Path.GetFileName(saved) == "测试 document (1).txt", "existing file preserved under automatic collision rename");
            Assert(Convert.ToBase64String(File.ReadAllBytes(saved)) == Convert.ToBase64String(Payload) && reported == Payload.Length, "exact downloaded bytes and final progress");
            string zero = (string)Call(client, "Pull", empty, output, CancellationToken.None, null);
            Assert(new FileInfo(zero).Length == 0, "zero-byte file transfers");
            TestDrag(assembly, file, empty, saved, zero);
            Expect<IOException>(delegate { Call(client, "Pull", entries[0], output, CancellationToken.None, null); });
            File.WriteAllText(Path.Combine(root, "mode.txt"), "truncated");
            Expect<IOException>(delegate { Call(client, "Pull", file, output, CancellationToken.None, null); });
            Assert(Directory.GetFiles(output, "*.partial").Length == 0, "incomplete download cleaned");
            File.WriteAllText(Path.Combine(root, "mode.txt"), "slow");
            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.CancelAfter(300);
                Expect<OperationCanceledException>(delegate { Call(client, "Pull", file, output, cancellation.Token, null); });
            }
            Assert(Directory.GetFiles(output, "*.partial").Length == 0, "cancel terminates child process and cleans temporary file");
            File.WriteAllText(Path.Combine(root, "mode.txt"), "offline");
            Expect<IOException>(delegate { Call(client, "List", "/sdcard", CancellationToken.None); });
            File.WriteAllText(Path.Combine(root, "mode.txt"), "multiple");
            Expect<IOException>(delegate { Call(Client(adb, null), "List", "/sdcard", CancellationToken.None); });
            File.WriteAllText(Path.Combine(root, "mode.txt"), "normal");
            Application.EnableVisualStyles();
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            using (var form = (Form)Activator.CreateInstance(assembly.GetType("FileTransferForm"), Instance, null, new object[] { adb, "only-device" }, null))
            {
                Assert(form.Text.Contains("手机到电脑"), "file browser constructed");
                bool desktop = (bool)assembly.GetType("Native").GetMethod("IsDesktopInputWindow", Static).Invoke(null,
                    new object[] { form.Handle, 0u, (uint)Process.GetCurrentProcess().Id });
                Assert(desktop, "file browser restores desktop input layout on focus");
                form.Opacity = 0;
                form.Show();
                WaitForBrowser(form);
                VerifyVisibleLayout(form);
                File.WriteAllText(Path.Combine(root, "mode.txt"), "offline");
                Browse(form, "/sdcard/Download", false);
                WaitForBrowser(form);
                var feedback = (Label)Field(form, "feedback");
                Assert(feedback.Text.Contains("共 3 项"), "recent directory reopens from cache without device operation");
                Browse(form, "/sdcard/Download", true);
                WaitForBrowser(form);
                Assert(feedback.Text.Contains("device offline"), "explicit refresh bypasses directory cache");
                File.WriteAllText(Path.Combine(root, "mode.txt"), "slow-list");
                Browse(form, "/sdcard", false);
                var started = Stopwatch.StartNew();
                while (!(bool)form.GetType().GetProperty("IsBusy", Instance).GetValue(form, null) && started.Elapsed.TotalSeconds < 2)
                { Application.DoEvents(); Thread.Sleep(10); }
                Assert((bool)form.GetType().GetProperty("IsBusy", Instance).GetValue(form, null), "listing runs asynchronously");
                TableLayoutPanel layout = (TableLayoutPanel)form.Controls[0];
                Control shortcuts = layout.GetControlFromPosition(0, 1), pathRow = layout.GetControlFromPosition(0, 2);
                Assert(layout.GetControlFromPosition(0, 0).Bottom <= shortcuts.Top && shortcuts.Bottom <= pathRow.Top, "header rows never overlap at actual application DPI");
                foreach (Control button in shortcuts.Controls)
                    Assert(button.Bottom <= shortcuts.ClientSize.Height, "shortcut buttons fit autosized row");
                form.Close();
                var wait = Stopwatch.StartNew();
                while (!form.IsDisposed && wait.Elapsed.TotalSeconds < 5) { Application.DoEvents(); Thread.Sleep(10); }
                Assert(form.IsDisposed, "closing busy browser cancels operation before closing");
            }
            Console.WriteLine("PASS: device binding, NUL directory framing, Unicode and shell quoting, Windows filenames, downloads and progress, collision preservation, empty files, partial cleanup, cancellation and offline/multiple-device errors.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine("TEST ERROR: " + ex.GetType().FullName + ": " + ex.Message + "\n" + ex.StackTrace); return 1; }
    }
    private static int FakeAdb(string[] args, string root)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        File.WriteAllLines(Path.Combine(root, "arguments.txt"), args, Encoding.UTF8);
        string mode = File.Exists(Path.Combine(root, "mode.txt")) ? File.ReadAllText(Path.Combine(root, "mode.txt")) : "normal";
        if (args[0] == "devices")
        {
            Console.WriteLine("List of devices attached\nonly-device\tdevice");
            if (mode == "multiple") Console.WriteLine("second-device\tdevice");
            return 0;
        }
        if (mode == "offline") { Console.Error.WriteLine("device offline"); return 1; }
        if (args[2] == "shell")
        {
            using (var reader = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8))
                File.WriteAllText(Path.Combine(root, "script.txt"), reader.ReadToEnd());
            if (mode == "legacy-find" && File.ReadAllText(Path.Combine(root, "script.txt")).Contains("-printf"))
            { Console.Error.WriteLine("find: Unknown option '-printf'"); return 1; }
            if (mode == "slow-list") Thread.Sleep(10000);
            Console.Write(Listing);
            return 0;
        }
        if (args[2] == "pull")
        {
            if (mode == "slow") { File.WriteAllBytes(args[4], new byte[] { 1 }); Thread.Sleep(10000); }
            File.WriteAllBytes(args[4], mode == "truncated" ? new byte[] { 1 } : args[3].EndsWith("empty.txt") ? new byte[0] : Payload);
            return 0;
        }
        return 2;
    }
    private static object Client(string adb, string serial) { return Activator.CreateInstance(api, Instance, null, new object[] { adb, serial }, null); }
    private static void TestDrag(Assembly assembly, object file, object empty, string saved, string zero)
    {
        Type fileType = assembly.GetType("DeviceFile");
        IList selected = (IList)Activator.CreateInstance(typeof(System.Collections.Generic.List<>).MakeGenericType(fileType));
        selected.Add(file); selected.Add(empty);
        int downloads = 0;
        Func<int, string> render = delegate(int index) { downloads++; return index == 0 ? saved : zero; };
        var data = (System.Runtime.InteropServices.ComTypes.IDataObject)Activator.CreateInstance(assembly.GetType("FileDragData"), Instance,
            null, new object[] { selected, render }, null);
        IntPtr pointer = Marshal.GetComInterfaceForObject(data, typeof(System.Runtime.InteropServices.ComTypes.IDataObject));
        Assert(pointer != IntPtr.Zero, "drag object exposes native COM IDataObject");
        Marshal.Release(pointer);
        var format = new FORMATETC { cfFormat = unchecked((short)DataFormats.GetFormat("FileGroupDescriptorW").Id),
            dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = -1, tymed = TYMED.TYMED_HGLOBAL };
        Assert(data.QueryGetData(ref format) == 0 && downloads == 0, "hover queries never download files");
        STGMEDIUM medium;
        data.GetData(ref format, out medium);
        IntPtr memory = GlobalLock(medium.unionmember);
        try
        {
            Assert(Marshal.ReadInt32(memory) == 2 && Marshal.PtrToStringUni(IntPtr.Add(memory, 76)) == "测试 document.txt", "native Unicode descriptors contain count and filename at correct offsets");
            Assert(Marshal.ReadInt32(memory, 72) == Payload.Length && downloads == 0, "descriptors advertise size without downloading");
        }
        finally { GlobalUnlock(medium.unionmember); ReleaseStgMedium(ref medium); }
        format.cfFormat = unchecked((short)DataFormats.GetFormat("FileContents").Id);
        format.tymed = TYMED.TYMED_ISTREAM;
        format.lindex = 0;
        for (int request = 0; request < 2; request++)
        {
            data.GetData(ref format, out medium);
            var stream = (IStream)Marshal.GetObjectForIUnknown(medium.unionmember);
            try
            {
                byte[] bytes = new byte[Payload.Length];
                stream.Read(bytes, bytes.Length, IntPtr.Zero);
                Assert(Convert.ToBase64String(bytes) == Convert.ToBase64String(Payload), "native file stream returns exact bytes");
            }
            finally { Marshal.ReleaseComObject(stream); ReleaseStgMedium(ref medium); }
        }
        Assert(downloads == 1, "repeated stream requests reuse downloaded file");
        string dropFolder = Path.Combine(Path.GetDirectoryName(saved), "shell-drop");
        Directory.CreateDirectory(dropFolder);
        Guid shellId = typeof(IShellItem).GUID;
        IShellItem shellItem;
        Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(dropFolder, IntPtr.Zero, ref shellId, out shellItem));
        IntPtr targetPointer = IntPtr.Zero;
        IShellDropTarget target = null;
        try
        {
            Guid handler = new Guid("3981e225-f559-11d3-8e3a-00c04f6837d5"), targetId = typeof(IShellDropTarget).GUID;
            shellItem.BindToHandler(IntPtr.Zero, ref handler, ref targetId, out targetPointer);
            target = (IShellDropTarget)Marshal.GetObjectForIUnknown(targetPointer);
            uint effect = 1;
            target.DragEnter(data, 1, new ShellPoint(), ref effect);
            Assert((effect & 1) != 0, "Windows folder drop target accepts virtual files");
            effect = 1;
            target.Drop(data, 0, new ShellPoint(), ref effect);
            string dropped = Path.Combine(dropFolder, "测试 document.txt");
            var deadline = Stopwatch.StartNew();
            while ((!File.Exists(dropped) || new FileInfo(dropped).Length != Payload.Length) && deadline.Elapsed.TotalSeconds < 5)
            { Application.DoEvents(); Thread.Sleep(10); }
            Assert(File.Exists(dropped) && Convert.ToBase64String(File.ReadAllBytes(dropped)) == Convert.ToBase64String(Payload), "Windows shell copies exact Unicode file bytes");
            Assert(File.Exists(Path.Combine(dropFolder, "empty.txt")) && new FileInfo(Path.Combine(dropFolder, "empty.txt")).Length == 0, "Windows shell copies multi-selection and empty files");
        }
        finally
        {
            if (target != null) Marshal.ReleaseComObject(target);
            if (targetPointer != IntPtr.Zero) Marshal.Release(targetPointer);
            Marshal.ReleaseComObject(shellItem);
        }
        format.lindex = 2;
        Assert(data.QueryGetData(ref format) < 0, "invalid file indexes rejected");
        object source = Activator.CreateInstance(assembly.GetType("FileDropSource"), Instance, null, new object[] { CancellationToken.None }, null);
        pointer = Marshal.GetComInterfaceForObject(source, assembly.GetType("IFileDropSource"));
        Assert(pointer != IntPtr.Zero, "drag source exposes native IDropSource");
        Marshal.Release(pointer);
        Console.WriteLine("PASS: lazy native drag descriptors, Unicode filenames, COM streams, Windows shell multi-file copies and stream caching.");
    }
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr memory);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr memory);
    [DllImport("ole32.dll")] private static extern void ReleaseStgMedium(ref STGMEDIUM medium);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr context, ref Guid iid, out IShellItem item);
    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr context, ref Guid handler, ref Guid iid, out IntPtr result);
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ShellPoint { public int X, Y; }
    [ComImport, Guid("00000122-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellDropTarget
    {
        void DragEnter([MarshalAs(UnmanagedType.Interface)] System.Runtime.InteropServices.ComTypes.IDataObject data, uint keys, ShellPoint point, ref uint effect);
        void DragOver(uint keys, ShellPoint point, ref uint effect);
        void DragLeave();
        void Drop([MarshalAs(UnmanagedType.Interface)] System.Runtime.InteropServices.ComTypes.IDataObject data, uint keys, ShellPoint point, ref uint effect);
    }
    private static object Call(object instance, string name, params object[] args)
    {
        try { return api.GetMethod(name, instance == null ? Static : Instance).Invoke(instance, args); }
        catch (TargetInvocationException ex) { throw ex.InnerException; }
    }
    private static object Field(object instance, string name) { return instance.GetType().GetField(name, Instance).GetValue(instance); }
    private static void WaitForBrowser(Form form)
    {
        Application.DoEvents(); // Shown posts the initial navigation.
        var wait = Stopwatch.StartNew();
        while ((bool)form.GetType().GetProperty("IsBusy", Instance).GetValue(form, null) && wait.Elapsed.TotalSeconds < 5)
        { Application.DoEvents(); Thread.Sleep(10); }
        Assert(!(bool)form.GetType().GetProperty("IsBusy", Instance).GetValue(form, null), "browser operation completes");
    }
    private static void Browse(Form form, string path, bool refresh)
    {
        // DoEvents outside Application.Run restores the default context.
        // Simulate a real UI event before invoking the async handler again.
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        form.GetType().GetMethod("Navigate", Instance).Invoke(form, new object[] { path, refresh });
    }
    private static void VerifyVisibleLayout(Form form)
    {
        Size original = form.Size;
        foreach (Size size in new[] { original, form.MinimumSize })
        {
            form.Size = size;
            form.PerformLayout();
            var layout = (TableLayoutPanel)form.Controls[0];
            Control buttons = layout.GetControlFromPosition(0, 5), status = layout.GetControlFromPosition(0, 6);
            Assert(buttons.Bottom <= status.Top && status.Bottom <= layout.ClientSize.Height, "footer buttons and status stay inside resized window");
            foreach (Control button in buttons.Controls)
                Assert(button.Bottom <= buttons.ClientSize.Height, "footer button text has a full row");
            Assert(status.Height >= status.GetPreferredSize(new Size(status.Width, 0)).Height, "status text wraps without vertical clipping");
        }
        form.Size = original;
        var save = (Button)Field(form, "save");
        Assert(!save.Enabled, "save is disabled without file selection");
        using (var bitmap = new Bitmap(save.Width, save.Height))
        {
            save.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
            int readablePixels = 0, lightPixels = 0;
            for (int y = 3; y < bitmap.Height - 3; y++)
                for (int x = 3; x < bitmap.Width - 3; x++)
                {
                    Color pixel = bitmap.GetPixel(x, y);
                    if (pixel.R < 170 && pixel.G < 170 && pixel.B < 170) readablePixels++;
                    if (pixel.R > 200 && pixel.G > 200 && pixel.B > 200) lightPixels++;
                }
            Assert(readablePixels > 20 && lightPixels > bitmap.Width * bitmap.Height / 3, "disabled button has readable dark text on a light background");
        }
        Assert(form.BackColor.GetBrightness() > 0.8f && ((ListView)Field(form, "files")).BackColor.GetBrightness() > 0.8f, "file browser uses light window and list backgrounds");
    }
    private static void Expect<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }
    private static void Assert(bool value, string message) { if (!value) throw new Exception("FAIL: " + message); }
}
