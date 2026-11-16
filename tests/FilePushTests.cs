using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;

internal static class FilePushTests
{
    private static Type transfer;
    private const BindingFlags Methods = BindingFlags.Static | BindingFlags.NonPublic;

    private static int Main(string[] args)
    {
        string self = Process.GetCurrentProcess().MainModule.FileName;
        if (Path.GetFileNameWithoutExtension(self) == "adb")
        {
            File.WriteAllLines(Path.Combine(Path.GetDirectoryName(self), "arguments.txt"), args, Encoding.UTF8);
            if (args[1] == "offline")
            {
                Console.OutputEncoding = Encoding.UTF8;
                Console.Error.WriteLine("device offline");
                return 1;
            }
            return 0;
        }
        try
        {
            transfer = Assembly.LoadFile(Path.GetFullPath(args[0])).GetType("FilePush");
            string folder = Path.Combine(Path.GetTempPath(), "Toolbar-transfer-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            string source = Path.Combine(folder, "中文 with spaces.docx");
            File.WriteAllText(source, "test");
            string adb = Path.Combine(folder, "adb.exe");
            File.Copy(self, adb);
            object[] parsed = { "ERROR: Failed to push " + source + " to /sdcard/Download/", null, null };
            Assert((bool)Call("TryReadFailure", parsed), "failed push recognized");
            string destination = "/sdcard/Download/中文 with spaces.docx";
            Assert((string)parsed[1] == source && (string)parsed[2] == destination, "Unicode name preserved with explicit destination");
            Assert(!(bool)Call("TryReadFailure", new object[] { "INFO: File pushed to /sdcard/Download/", null, null }), "successful push is never retried");
            Call("Send", adb, "serial:1234", source, destination);
            string[] actual = File.ReadAllLines(Path.Combine(folder, "arguments.txt"), Encoding.UTF8);
            Assert(actual.Length == 5 && actual[0] == "-s" && actual[1] == "serial:1234" && actual[2] == "push" &&
                actual[3] == source && actual[4] == destination, "real child process receives exact Unicode and spaced arguments and selected serial");
            ExpectFailure<IOException>(delegate { Call("Send", adb, "offline", source, destination); }, "device offline");
            ExpectFailure<InvalidOperationException>(delegate { Call("Send", adb, "", source, destination); }, "投屏设备");
            ExpectFailure<FileNotFoundException>(delegate { Call("Send", adb, "serial", source + ".missing", destination); }, "找不到");
            // Quoting must also work for arbitrary Windows argument values.
            string unusual = "embedded\"quote\\";
            string arguments = (string)Call("BuildArguments", "serial", unusual, "/target/file");
            using (var process = Process.Start(new ProcessStartInfo(adb, arguments) { UseShellExecute = false, CreateNoWindow = true }))
                process.WaitForExit();
            actual = File.ReadAllLines(Path.Combine(folder, "arguments.txt"), Encoding.UTF8);
            Assert(actual[3] == unusual, "embedded quotes and trailing backslashes round trip");
            Console.WriteLine("PASS: failed-push detection, explicit Unicode filename, device targeting, actual child-process argument quoting, offline and missing-file failures.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static object Call(string name, params object[] values)
    {
        try { return transfer.GetMethod(name, Methods).Invoke(null, values); }
        catch (TargetInvocationException ex) { throw ex.InnerException; }
    }
    private static void ExpectFailure<T>(Action action, string text) where T : Exception
    {
        try { action(); }
        catch (T ex) { Assert(ex.Message.Contains(text), "actionable error retained"); return; }
        throw new Exception("Expected " + typeof(T).Name);
    }
    private static void Assert(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL: " + name);
    }
}
