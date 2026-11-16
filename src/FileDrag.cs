using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Threading;
using System.Windows.Forms;

// Explorer requests FileContents only when it copies a dropped virtual file.
// Queries and hover previews return descriptors without touching the device.
[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
public sealed class FileDragData : System.Runtime.InteropServices.ComTypes.IDataObject
{
    private readonly IList<DeviceFile> files;
    private readonly Func<int, string> render;
    private readonly Dictionary<int, string> rendered = new Dictionary<int, string>();
    private readonly short descriptors = unchecked((short)DataFormats.GetFormat("FileGroupDescriptorW").Id);
    private readonly short contents = unchecked((short)DataFormats.GetFormat("FileContents").Id);
    private readonly short preferredEffect = unchecked((short)DataFormats.GetFormat("Preferred DropEffect").Id);
    internal Exception Error;

    internal FileDragData(IList<DeviceFile> files, Func<int, string> render)
    { this.files = files; this.render = render; }

    internal byte[] Descriptors()
    {
        using (var memory = new MemoryStream())
        using (var writer = new BinaryWriter(memory, Encoding.Unicode))
        {
            writer.Write(files.Count);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (DeviceFile file in files)
            {
                writer.Write(0x80004064u); // UNICODE, PROGRESSUI, FILESIZE, WRITESTIME, ATTRIBUTES
                writer.Write(new byte[32]); // CLSID, SIZEL, POINTL
                writer.Write(0x80u); // FILE_ATTRIBUTE_NORMAL; always a copy
                writer.Write(0L); writer.Write(0L);
                writer.Write(file.Modified == DateTime.MinValue ? 0L : file.Modified.ToUniversalTime().ToFileTimeUtc());
                writer.Write((uint)((ulong)file.Size >> 32));
                writer.Write((uint)file.Size);
                string name = AdbFiles.LocalName(file.Name);
                if (name.Length > 240) name = name.Substring(0, 240);
                string stem = Path.GetFileNameWithoutExtension(name), extension = Path.GetExtension(name);
                string unique = name;
                for (int n = 1; !names.Add(unique); n++) unique = stem + " (" + n + ")" + extension;
                byte[] encoded = Encoding.Unicode.GetBytes(unique);
                writer.Write(encoded);
                writer.Write(new byte[520 - encoded.Length]);
            }
            return memory.ToArray();
        }
    }

    public int QueryGetData(ref FORMATETC format)
    {
        if (format.dwAspect != DVASPECT.DVASPECT_CONTENT) return unchecked((int)0x8004006B);
        if (format.cfFormat == descriptors || format.cfFormat == preferredEffect)
            return (format.tymed & TYMED.TYMED_HGLOBAL) != 0 ? 0 : unchecked((int)0x80040069);
        if (format.cfFormat == contents)
        {
            if (format.lindex < -1 || format.lindex >= files.Count) return unchecked((int)0x80040068);
            return (format.tymed & TYMED.TYMED_ISTREAM) != 0 ? 0 : unchecked((int)0x80040069);
        }
        return unchecked((int)0x80040064);
    }

    public void GetData(ref FORMATETC format, out STGMEDIUM medium)
    {
        medium = new STGMEDIUM();
        Marshal.ThrowExceptionForHR(QueryGetData(ref format));
        if (format.cfFormat == descriptors || format.cfFormat == preferredEffect)
        {
            byte[] bytes = format.cfFormat == descriptors ? Descriptors() : BitConverter.GetBytes(1); // COPY
            IntPtr handle = FileDragNative.GlobalAlloc(0x42, (UIntPtr)bytes.Length);
            if (handle == IntPtr.Zero) throw new OutOfMemoryException();
            IntPtr buffer = FileDragNative.GlobalLock(handle);
            if (buffer == IntPtr.Zero) { FileDragNative.GlobalFree(handle); throw new OutOfMemoryException(); }
            try { Marshal.Copy(bytes, 0, buffer, bytes.Length); }
            finally { FileDragNative.GlobalUnlock(handle); }
            medium.tymed = TYMED.TYMED_HGLOBAL;
            medium.unionmember = handle;
            return;
        }
        if (format.lindex < 0) throw new COMException("请选择具体文件。", unchecked((int)0x80040068));
        try
        {
            string path;
            if (!rendered.TryGetValue(format.lindex, out path))
            {
                path = render(format.lindex);
                rendered[format.lindex] = path;
            }
            IStream stream;
            Marshal.ThrowExceptionForHR(FileDragNative.SHCreateStreamOnFileEx(path, 0x20, 0, false, null, out stream));
            try
            {
                medium.tymed = TYMED.TYMED_ISTREAM;
                medium.unionmember = Marshal.GetComInterfaceForObject(stream, typeof(IStream));
            }
            finally { Marshal.ReleaseComObject(stream); }
        }
        catch (Exception ex)
        {
            Error = ex;
            throw new COMException(ex.Message, ex is OperationCanceledException ? unchecked((int)0x80004004) : unchecked((int)0x80004005));
        }
    }

    public void GetDataHere(ref FORMATETC format, ref STGMEDIUM medium) { throw new COMException("Unsupported storage", unchecked((int)0x80040069)); }
    public int GetCanonicalFormatEtc(ref FORMATETC input, out FORMATETC output) { output = input; output.ptd = IntPtr.Zero; return 0x40130; }
    public void SetData(ref FORMATETC format, ref STGMEDIUM medium, bool release) { if (release) FileDragNative.ReleaseStgMedium(ref medium); }
    public IEnumFORMATETC EnumFormatEtc(DATADIR direction)
    {
        if (direction != DATADIR.DATADIR_GET) throw new COMException("Unsupported direction", unchecked((int)0x80004001));
        return new FileFormats(new[] { Format(descriptors, TYMED.TYMED_HGLOBAL), Format(contents, TYMED.TYMED_ISTREAM), Format(preferredEffect, TYMED.TYMED_HGLOBAL) });
    }
    private static FORMATETC Format(short id, TYMED medium) { return new FORMATETC { cfFormat = id, dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = -1, tymed = medium }; }
    public int DAdvise(ref FORMATETC format, ADVF flags, IAdviseSink sink, out int connection) { connection = 0; return unchecked((int)0x80040003); }
    public void DUnadvise(int connection) { throw new COMException("Advisory connections unsupported", unchecked((int)0x80040003)); }
    public int EnumDAdvise(out IEnumSTATDATA connections) { connections = null; return unchecked((int)0x80040003); }
}

[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
public sealed class FileFormats : IEnumFORMATETC
{
    private readonly FORMATETC[] values;
    private int index;
    internal FileFormats(FORMATETC[] values) { this.values = values; }
    public int Next(int count, FORMATETC[] output, int[] fetched)
    {
        int copied = 0;
        while (copied < count && index < values.Length) output[copied++] = values[index++];
        if (fetched != null && fetched.Length > 0) fetched[0] = copied;
        return copied == count ? 0 : 1;
    }
    public int Skip(int count) { int remaining = values.Length - index; index += Math.Min(remaining, count); return remaining >= count ? 0 : 1; }
    public int Reset() { index = 0; return 0; }
    public void Clone(out IEnumFORMATETC clone) { clone = new FileFormats(values) { index = index }; }
}

[ComVisible(true), Guid("00000121-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IFileDropSource
{
    [PreserveSig] int QueryContinueDrag([MarshalAs(UnmanagedType.Bool)] bool escape, uint keys);
    [PreserveSig] int GiveFeedback(uint effect);
}

[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
public sealed class FileDropSource : IFileDropSource
{
    private readonly CancellationToken cancel;
    internal FileDropSource(CancellationToken cancel) { this.cancel = cancel; }
    public int QueryContinueDrag(bool escape, uint keys)
    {
        if (escape || cancel.IsCancellationRequested) return 0x40101; // CANCEL
        return (keys & 1) == 0 ? 0x40100 : 0; // DROP when left button released
    }
    public int GiveFeedback(uint effect) { return 0x40102; }
}

internal static class FileDragNative
{
    internal static bool Copy(IList<DeviceFile> files, Func<int, string> render, CancellationToken cancel)
    {
        int initialized = OleInitialize(IntPtr.Zero);
        Marshal.ThrowExceptionForHR(initialized);
        try
        {
            var data = new FileDragData(files, render);
            uint effect;
            int result = DoDragDrop(data, new FileDropSource(cancel), 1, out effect);
            if (data.Error != null) throw data.Error;
            Marshal.ThrowExceptionForHR(result);
            return result == 0x40100 && (effect & 1) != 0;
        }
        finally { OleUninitialize(); }
    }
    [DllImport("ole32.dll")] private static extern int OleInitialize(IntPtr reserved);
    [DllImport("ole32.dll")] private static extern void OleUninitialize();
    [DllImport("ole32.dll")] private static extern int DoDragDrop([MarshalAs(UnmanagedType.Interface)] System.Runtime.InteropServices.ComTypes.IDataObject data,
        IFileDropSource source, uint allowed, out uint effect);
    [DllImport("ole32.dll")] internal static extern void ReleaseStgMedium(ref STGMEDIUM medium);
    [DllImport("kernel32.dll")] internal static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
    [DllImport("kernel32.dll")] internal static extern IntPtr GlobalLock(IntPtr handle);
    [DllImport("kernel32.dll")] internal static extern bool GlobalUnlock(IntPtr handle);
    [DllImport("kernel32.dll")] internal static extern IntPtr GlobalFree(IntPtr handle);
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)] internal static extern int SHCreateStreamOnFileEx(string file, uint mode, uint attributes,
        [MarshalAs(UnmanagedType.Bool)] bool create, IStream template, out IStream stream);
}
