using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

// Uses invisible fake mirrors, never ADB or the user's device.
internal static class KeyboardRestartTests
{
    private static readonly BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Static;
    private static Type toolbarType;
    private static Form toolbar;

    [STAThread]
    private static int Main(string[] args)
    {
        if (Path.GetFileNameWithoutExtension(Application.ExecutablePath) == "scrcpy")
        {
            Console.WriteLine("fake mirror " + string.Join(" ", args));
            Application.Run(new FakeMirror());
            return 0;
        }
        string testRoot = Path.Combine(Path.GetTempPath(), "ToolbarKeyboardTest-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(testRoot);
            File.Copy(Application.ExecutablePath, Path.Combine(testRoot, "scrcpy.exe"));
            File.WriteAllText(Path.Combine(testRoot, "adb.exe"), "not executed");
            Assembly assembly = Assembly.LoadFile(Path.GetFullPath(args[0]));
            toolbarType = assembly.GetType("Toolbar");
            Type native = assembly.GetType("Native");
            IntPtr otherWindow = (IntPtr)native.GetMethod("GetForegroundWindow", Fields).Invoke(null, null);
            object[] otherArgs = new object[] { otherWindow, 0u };
            uint otherThread = (uint)native.GetMethod("GetWindowThreadProcessId", Fields).Invoke(null, otherArgs);
            IntPtr otherLayout = (IntPtr)native.GetMethod("GetKeyboardLayout", Fields).Invoke(null, new object[] { otherThread });
            TestDesktopInput(assembly);
            toolbar = (Form)Activator.CreateInstance(toolbarType);
            toolbar.Opacity = 0;
            Set("root", testRoot);
            Set("settingsPath", Path.Combine(testRoot, "keyboard-mode.txt"));
            Set("logPath", Path.Combine(testRoot, "mirror.log"));
            Set("keyboardMode", "uhid");
            Set("directShift", true);
            toolbar.Show();
            PumpUntil(delegate { return (bool)Get("ready"); });
            int firstPid = ((Process)Get("mirror")).Id;
            Assert((bool)Get("keyboardModeKnown"), "mode known on new launch");
            Assert(Arguments().Contains("--keyboard=uhid"), "default UHID argument");
            Assert(File.ReadAllText(Path.Combine(testRoot, "keyboard-mode.txt")) == "uhid", "default mode saved");
            TestDirectKeyboard(native, otherThread, otherLayout);

            Call("ApplyKeyboardMode", "sdk");
            PumpUntil(delegate { return (bool)Get("ready") && ((Process)Get("mirror")).Id != firstPid; });
            int secondPid = ((Process)Get("mirror")).Id;
            Assert(!toolbar.IsDisposed, "toolbar survives owner close");
            Assert(Arguments().Contains("--keyboard=sdk"), "SDK selected");
            Assert(Arguments().Contains("--video-bit-rate=20M"), "keyboard switch retains quality");
            Assert(File.ReadAllText(Path.Combine(testRoot, "keyboard-mode.txt")) == "sdk", "selection saved");

            Array presets = (Array)toolbarType.GetField("QualityPresets", Fields).GetValue(null);
            Call("ApplyQuality", presets.GetValue(3));
            PumpUntil(delegate { return (bool)Get("ready") && ((Process)Get("mirror")).Id != secondPid; });
            Assert(Arguments().Contains("--video-bit-rate=40M"), "quality switch applied");
            Assert(Arguments().Contains("--keyboard=sdk"), "quality switch retains keyboard");

            // Attaching cannot claim to know the current mode. Selecting the
            // saved mode must still restart instead of silently doing nothing.
            Set("keyboardModeKnown", false);
            int thirdPid = ((Process)Get("mirror")).Id;
            Call("ApplyKeyboardMode", "sdk");
            PumpUntil(delegate { return (bool)Get("ready") && ((Process)Get("mirror")).Id != thirdPid; });

            Set("fullscreen", true);
            Set("restartWindowArguments", " --fullscreen");
            Assert(Arguments().Contains("--fullscreen"), "fullscreen launch argument");
            Call("ReadAttachedCommandLine", "scrcpy.exe --video-bit-rate=35M --max-fps=45 --keyboard=sdk --serial=test-device");
            Assert(Arguments().Contains("--video-bit-rate=35M") && Arguments().Contains("--max-fps=45"), "attached quality retained");
            Assert(Arguments().Contains("--serial=test-device"), "attached device retained");
            Console.WriteLine("PASS: Mirror-only English layout, original layout restoration, PID guard, unrelated foreground layout unchanged; desktop input draft/composition; UHID/SDK restart and parameter retention.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally
        {
            if (toolbar != null && !toolbar.IsDisposed)
            {
                // Detach before disposing either form.
                Type native = toolbarType.Assembly.GetType("Native");
                native.GetMethod("SetOwner", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null,
                    new object[] { toolbar.Handle, IntPtr.Zero });
                Process mirror = Get("mirror") as Process;
                toolbar.Close();
                if (mirror != null)
                {
                    try { if (!mirror.HasExited) { mirror.Kill(); mirror.WaitForExit(3000); } } catch { }
                }
            }
            foreach (Process process in Process.GetProcessesByName("scrcpy"))
            {
                try { if (process.MainModule.FileName == Path.Combine(testRoot, "scrcpy.exe")) process.Kill(); }
                catch { }
                process.Dispose();
            }
        }
    }

    private static object Get(string field) { return toolbarType.GetField(field, Fields).GetValue(toolbar); }
    private static void Set(string field, object value) { toolbarType.GetField(field, Fields).SetValue(toolbar, value); }
    private static object Call(string method, params object[] args) { return toolbarType.GetMethod(method, Fields).Invoke(toolbar, args); }
    private static string Arguments() { return (string)Call("BuildMirrorArguments"); }
    private static void TestDirectKeyboard(Type native, uint otherThread, IntPtr otherLayout)
    {
        object layout = Get("keyboardLayout");
        Type layoutType = layout.GetType();
        uint mirrorThread = (uint)layoutType.GetField("threadId", Fields).GetValue(layout);
        IntPtr original = (IntPtr)layoutType.GetField("original", Fields).GetValue(layout);
        MethodInfo getLayout = native.GetMethod("GetKeyboardLayout", Fields);
        Func<IntPtr> current = delegate { return (IntPtr)getLayout.Invoke(null, new object[] { mirrorThread }); };
        PumpUntil(delegate { return current() == new IntPtr(0x04090409); });
        Assert((IntPtr)getLayout.Invoke(null, new object[] { otherThread }) == otherLayout, "other app keyboard layout unchanged after enabling direct input");

        Set("directShift", false);
        Call("UpdateDirectKeyboard");
        PumpUntil(delegate { return current() == original; });
        Assert((IntPtr)getLayout.Invoke(null, new object[] { otherThread }) == otherLayout, "other app keyboard layout unchanged after restoration");
        layoutType.GetMethod("Update", Fields).Invoke(layout,
            new object[] { Get("target"), (uint)((Process)Get("mirror")).Id + 1u, true });
        Application.DoEvents();
        Assert(current() == original, "wrong process identity cannot change layout");
        layoutType.GetField("lastRequest", Fields).SetValue(layout, DateTime.MinValue);
        Set("directShift", true);
        Call("UpdateDirectKeyboard");
        Set("directShift", false);
        Call("UpdateDirectKeyboard");
        // A fake-mirror acknowledgement fences all preceding posted layout
        // messages, including an enable that was cancelled before processing.
        string fence = Path.Combine((string)Get("root"), "layout-fence.txt");
        native.GetMethod("PostMessage", Fields).Invoke(null, new object[] { Get("target"), 0x8001u, IntPtr.Zero, IntPtr.Zero });
        PumpUntil(delegate { return File.Exists(fence); });
        Assert(current() == original, "rapid disable restores layout after pending enable");
        Set("directShift", true);
        Call("UpdateDirectKeyboard");
        PumpUntil(delegate { return current() == new IntPtr(0x04090409); });
    }
    private static void TestDesktopInput(Assembly assembly)
    {
        Type inputType = assembly.GetType("DesktopInputForm");
        int calls = 0;
        string received = null;
        string failure = null;
        Func<string, string> stub = delegate(string value) { calls++; received = value; return failure; };
        using (Form input = (Form)Activator.CreateInstance(inputType, Fields | BindingFlags.Public,
            null, new object[] { stub }, null))
        {
            TextBox editor = (TextBox)inputType.GetField("editor", Fields).GetValue(input);
            Label feedback = (Label)inputType.GetField("feedback", Fields).GetValue(input);
            MethodInfo request = inputType.GetMethod("RequestFill", Fields);
            request.Invoke(input, null);
            Assert(calls == 0, "empty input is not filled");
            string mixed = "\u4e2d\u6587 English 123\r\n\ud83d\ude00";
            editor.Text = mixed;
            PropertyInfo composing = editor.GetType().GetProperty("IsComposing", Fields);
            composing.GetSetMethod(true).Invoke(editor, new object[] { true });
            request.Invoke(input, null);
            Assert(calls == 0 && editor.Text == mixed, "unfinished IME composition is not filled");
            composing.GetSetMethod(true).Invoke(editor, new object[] { false });
            request.Invoke(input, null);
            Assert(calls == 1 && received == mixed && editor.Text == mixed, "Unicode, English, digits, newlines passed unchanged and draft retained");
            failure = "test paste failure";
            request.Invoke(input, null);
            Assert(calls == 2 && feedback.Text == failure && editor.Text == mixed, "paste failure displayed without losing draft");
            CreateParams cp = (CreateParams)typeof(Control).GetProperty("CreateParams", Fields).GetValue(input, null);
            Assert((cp.ExStyle & 0x08000000) == 0, "desktop input permits normal activation and Windows IME");
            IntPtr handle = input.Handle;
            input.Close();
            Assert(!input.IsDisposed && editor.Text == mixed, "close hides input and retains draft");
        }
    }
    private static void Assert(bool condition, string label) { if (!condition) throw new Exception("FAIL: " + label); }
    private static void PumpUntil(Func<bool> condition)
    {
        Stopwatch timeout = Stopwatch.StartNew();
        while (!condition())
        {
            if (timeout.Elapsed.TotalSeconds > 12) throw new Exception("Timed out waiting for fake mirror restart.");
            Application.DoEvents();
            Thread.Sleep(10);
        }
    }

    private sealed class FakeMirror : Form
    {
        internal FakeMirror()
        {
            Opacity = 0;
            Text = "Toolbar test mirror";
            StartPosition = FormStartPosition.Manual;
            Bounds = new Rectangle(50, 50, 400, 300);
        }
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x8001)
            {
                File.WriteAllText(Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "layout-fence.txt"), "done");
                return;
            }
            base.WndProc(ref m);
        }
        protected override CreateParams CreateParams
        {
            get { CreateParams cp = base.CreateParams; cp.ExStyle |= 0x80; return cp; }
        }
    }
}
