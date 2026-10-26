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
            toolbar = (Form)Activator.CreateInstance(toolbarType);
            toolbar.Opacity = 0;
            Set("root", testRoot);
            Set("settingsPath", Path.Combine(testRoot, "keyboard-mode.txt"));
            Set("logPath", Path.Combine(testRoot, "mirror.log"));
            Set("keyboardMode", "uhid");
            toolbar.Show();
            PumpUntil(delegate { return (bool)Get("ready"); });
            int firstPid = ((Process)Get("mirror")).Id;
            Assert((bool)Get("keyboardModeKnown"), "mode known on new launch");
            Assert(Arguments().Contains("--keyboard=uhid"), "default UHID argument");
            Assert(File.ReadAllText(Path.Combine(testRoot, "keyboard-mode.txt")) == "uhid", "default mode saved");

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
            Console.WriteLine("PASS: UHID/SDK restart, saved mode, quality/device retention, unknown attached mode, toolbar lifetime.");
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
        protected override CreateParams CreateParams
        {
            get { CreateParams cp = base.CreateParams; cp.ExStyle |= 0x80; return cp; }
        }
    }
}
