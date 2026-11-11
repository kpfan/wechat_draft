using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace WechatDraft.Windows;

internal sealed record FileClipboardCopy(uint Sequence, IReadOnlySet<uint> OleFormats)
{
    internal const string Unsupported = "剪贴板含虚拟文件或流式对象，暂不支持完整恢复，未修改原内容。";
    private const uint FileDrop = 15;

    internal static bool IsDescriptor(string name) => name is "FileGroupDescriptor" or "FileGroupDescriptorW";
    internal static bool IsOleControl(string name) => name is "DataObject" or "Ole Private Data" or "OleClipboardPersistOnFlush";

    // OLE may expose indexed FileContents streams that cannot be inspected under OpenClipboard.
    // Tie this preflight to the sequence that the caller will check again under the native lock.
    internal static FileClipboardCopy? Inspect()
    {
        var sequence = Native.GetClipboardSequenceNumber();
        if (!Native.IsClipboardFormatAvailable(FileDrop) ||
            !Native.IsClipboardFormatAvailable(Native.RegisterClipboardFormat("FileGroupDescriptorW")) &&
            !Native.IsClipboardFormatAvailable(Native.RegisterClipboardFormat("FileGroupDescriptor")))
            return null;
        if (sequence == 0 || Native.IsClipboardFormatAvailable(Native.RegisterClipboardFormat("FileContents")))
            throw new ClipboardBackupUnavailableException(Unsupported);

        if (OleInitialize(0) < 0) throw new ClipboardBackupUnavailableException(Unsupported);
        try
        {
            var formats = ReadOleFormats();
            if (Native.GetClipboardSequenceNumber() != sequence)
                throw new InvalidOperationException("核实文件剪贴板期间内容已变化，未修改原内容。");
            return new(sequence, formats);
        }
        catch (COMException e) { throw new ClipboardBackupUnavailableException(Unsupported, e); }
        finally { OleUninitialize(); }
    }

    private static HashSet<uint> ReadOleFormats()
    {
        if (OleGetClipboard(out var data) < 0 || data is null) throw new ClipboardBackupUnavailableException(Unsupported);
        try
        {
            var enumerator = data.EnumFormatEtc(DATADIR.DATADIR_GET);
            if (enumerator is null) throw new ClipboardBackupUnavailableException(Unsupported);
            try
            {
                var formats = new HashSet<uint>();
                var entry = new FORMATETC[1];
                var fetched = new int[1];
                var name = new StringBuilder(128);
                for (var i = 0; i <= NativeClipboardSnapshot.MaximumFormats; i++)
                {
                    var result = enumerator.Next(1, entry, fetched);
                    if (result == 1 && fetched[0] == 0) return formats;
                    if (result != 0 || fetched[0] != 1) throw new ClipboardBackupUnavailableException(Unsupported);
                    var item = entry[0];
                    if (item.ptd != 0) Marshal.FreeCoTaskMem(item.ptd);
                    var format = (uint)(ushort)item.cfFormat;
                    name.Clear();
                    Native.GetClipboardFormatName(format, name, name.Capacity);
                    if (IsOleControl(name.ToString())) continue;
                    if (name.ToString() == "FileContents" || item.lindex != -1 || item.ptd != 0 ||
                        item.dwAspect != DVASPECT.DVASPECT_CONTENT || (item.tymed & TYMED.TYMED_HGLOBAL) == 0)
                        throw new ClipboardBackupUnavailableException(Unsupported);
                    formats.Add(format);
                }
                throw new ClipboardBackupUnavailableException(Unsupported);
            }
            finally { if (Marshal.IsComObject(enumerator)) Marshal.ReleaseComObject(enumerator); }
        }
        finally { if (Marshal.IsComObject(data)) Marshal.ReleaseComObject(data); }
    }

    internal void Validate(IReadOnlyList<ClipboardEntry> entries)
    {
        var captured = entries.ToDictionary(entry => entry.Format);
        if (!OleFormats.Contains(FileDrop) || OleFormats.Any(format => !captured.ContainsKey(format)) ||
            !captured.TryGetValue(FileDrop, out var drop) || drop.Bytes is null)
            throw new ClipboardBackupUnavailableException(Unsupported);

        var count = CountPaths(drop.Bytes);
        ValidateDescriptor(captured, "FileGroupDescriptor", count, 332);
        ValidateDescriptor(captured, "FileGroupDescriptorW", count, 592);
        if (captured.TryGetValue(Native.RegisterClipboardFormat("Preferred DropEffect"), out var effect) &&
            (effect.Bytes is not { Length: >= 4 } bytes || (BinaryPrimitives.ReadUInt32LittleEndian(bytes) & 2) != 0))
            throw new ClipboardBackupUnavailableException("剪贴板含剪切或无法核实的文件操作，未修改原内容。");
    }

    private static void ValidateDescriptor(Dictionary<uint, ClipboardEntry> captured, string name, int count, int itemSize)
    {
        if (captured.TryGetValue(Native.RegisterClipboardFormat(name), out var entry) &&
            (entry.Bytes is not { Length: >= 4 } bytes || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != count ||
             bytes.Length < 4L + (long)count * itemSize))
            throw new ClipboardBackupUnavailableException(Unsupported);
    }

    private static int CountPaths(ReadOnlySpan<byte> bytes)
    {
        const int headerSize = 20; // DROPFILES
        if (bytes.Length < headerSize) throw new ClipboardBackupUnavailableException(Unsupported);
        var start = BinaryPrimitives.ReadInt32LittleEndian(bytes);
        var wide = BinaryPrimitives.ReadInt32LittleEndian(bytes[16..]) != 0;
        var width = wide ? 2 : 1;
        if (start < headerSize || start > bytes.Length - width || start % width != 0)
            throw new ClipboardBackupUnavailableException(Unsupported);

        var count = 0;
        for (var end = start; end <= bytes.Length - width; end += width)
        {
            if (bytes[end] != 0 || wide && bytes[end + 1] != 0) continue;
            if (end == start && count > 0) return count;
            var path = (wide ? Encoding.Unicode : Encoding.Latin1).GetString(bytes[start..end]);
            if (!Path.IsPathFullyQualified(path)) throw new ClipboardBackupUnavailableException(Unsupported);
            count++;
            start = end + width;
        }
        throw new ClipboardBackupUnavailableException(Unsupported);
    }

    [DllImport("ole32.dll")] private static extern int OleInitialize(nint reserved);
    [DllImport("ole32.dll")] private static extern void OleUninitialize();
    [DllImport("ole32.dll")] private static extern int OleGetClipboard([MarshalAs(UnmanagedType.Interface)] out IDataObject? data);
}
