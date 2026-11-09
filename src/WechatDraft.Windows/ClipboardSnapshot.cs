using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace WechatDraft.Windows;

internal enum ClipboardStorage { Global, Bitmap, Palette, EnhancedMetafile, MetafilePicture }

internal sealed class ClipboardEntry(uint format, ClipboardStorage storage, byte[]? bytes, nint handle, Native.MetafilePicture picture, long size) : IDisposable
{
    public uint Format { get; } = format;
    public ClipboardStorage Storage { get; } = storage;
    public byte[]? Bytes { get; } = bytes;
    public nint Handle { get; private set; } = handle;
    public Native.MetafilePicture Picture { get; } = picture;
    public long Size { get; } = size;
    public void Dispose()
    {
        if (Bytes is not null) Array.Clear(Bytes);
        if (Handle != 0)
        {
            if (Storage is ClipboardStorage.Bitmap or ClipboardStorage.Palette) Native.DeleteObject(Handle);
            else if (Storage == ClipboardStorage.EnhancedMetafile) Native.DeleteEnhMetaFile(Handle);
            else if (Storage == ClipboardStorage.MetafilePicture) Native.DeleteMetaFile(Handle);
            Handle = 0;
        }
    }
}

internal sealed class NativeClipboardSnapshot : IClipboardSnapshot
{
    internal const long MaximumBytes = 128L * 1024 * 1024;
    internal const int MaximumItemBytes = 64 * 1024 * 1024;
    internal const int MaximumFormats = 128;
    internal List<ClipboardEntry> Entries { get; } = new();

    public static NativeClipboardSnapshot Capture()
    {
        var snapshot = new NativeClipboardSnapshot(); long total = 0;
        try
        {
            // Materialization can add synthesized formats. Re-enumerate until the list is stable.
            var seen = new HashSet<uint>();
            while (true)
            {
                var formats = new List<uint>(); uint format = 0;
                while ((format = Native.EnumClipboardFormats(format)) != 0)
                {
                    if (formats.Count >= MaximumFormats) throw new InvalidOperationException("剪贴板格式过多，未清空或修改原内容。");
                    formats.Add(format);
                }
                if (Marshal.GetLastPInvokeError() != 0) throw new InvalidOperationException("无法完整枚举剪贴板格式，未修改原内容。");
                var pending = formats.Where(f => !seen.Contains(f)).ToArray();
                if (pending.Length == 0) break;
                foreach (var next in pending)
                {
                    if (seen.Count >= MaximumFormats) throw new InvalidOperationException("剪贴板格式持续变化，未修改原内容。");
                    seen.Add(next);
                    if (next == 0x80 || next is >= 0x200 and <= 0x3ff)
                        throw new InvalidOperationException("剪贴板包含无法独立恢复的应用专用对象，未修改原内容。");
                    var name = new StringBuilder(128); Native.GetClipboardFormatName(next, name, name.Capacity);
                    // OLE control channels are tied to the original owner, not user payload.
                    // Native formats are fully materialized; stale COM owner pointers must not be republished.
                    if (name.ToString() is "DataObject" or "Ole Private Data" or "OleClipboardPersistOnFlush") continue;
                    if (name.ToString() is "FileContents" or "FileGroupDescriptor" or "FileGroupDescriptorW")
                        throw new InvalidOperationException("剪贴板含虚拟文件或流式对象，暂不支持完整恢复，未修改原内容。");
                    var entry = Read(next, MaximumBytes - total);
                    snapshot.Entries.Add(entry); total += entry.Size;
                }
            }
            if (seen.Count > 0 && snapshot.Entries.Count == 0)
                throw new InvalidOperationException("剪贴板只有应用专用对象，未修改原内容。");
            return snapshot;
        }
        catch { snapshot.Dispose(); throw; }
    }

    private static ClipboardEntry Read(uint format, long remaining)
    {
        var handle = Native.GetClipboardData(format);
        if (handle == 0 && format != 2) throw new InvalidOperationException("剪贴板有尚未提供的数据，未修改原内容。");
        var kind = format switch { 2 or 0x82 => ClipboardStorage.Bitmap, 9 => ClipboardStorage.Palette,
            14 or 0x8e => ClipboardStorage.EnhancedMetafile, 3 or 0x83 => ClipboardStorage.MetafilePicture, _ => ClipboardStorage.Global };
        long size; byte[]? bytes = null; nint copy = 0; var picture = default(Native.MetafilePicture);
        switch (kind)
        {
            case ClipboardStorage.Bitmap:
                if (Native.GetBitmapObject(handle, Marshal.SizeOf<Native.BitmapObject>(), out var bitmap) == 0 || bitmap.Width <= 0 || bitmap.Height == 0)
                {
                    if (format != 2) throw new InvalidOperationException("无法读取剪贴板图片尺寸。");
                    var dibFormat = Native.IsClipboardFormatAvailable(8) ? 8u : Native.IsClipboardFormatAvailable(17) ? 17u : 0;
                    if (dibFormat == 0) throw new InvalidOperationException("无法读取剪贴板图片尺寸，且没有独立图片数据可备份。");
                    using var dib = Read(dibFormat, remaining);
                    var prepared = ClipboardDibBitmap.Prepare(dib.Bytes!);
                    try { size = prepared.PixelBytes; CheckSize(size, remaining); copy = ClipboardDibBitmap.Create(prepared.Bmp); }
                    finally { Array.Clear(prepared.Bmp); }
                    WechatDraft.Core.DebugSession.Current?.Event("clipboard_bitmap_rebuilt", new { fromFormat = dibFormat, bytes = size });
                    break;
                }
                size = checked(Math.Abs((long)bitmap.Height) * Math.Abs((long)bitmap.WidthBytes));
                CheckSize(size, remaining);
                copy = Native.CopyImage(handle, 0, 0, 0, 0x2000);
                break;
            case ClipboardStorage.Palette:
                var count = Native.GetPaletteEntries(handle, 0, 0, null);
                if (count is 0 or > 65535) throw new InvalidOperationException("无法读取图片调色板。");
                size = 4L + count * 4; CheckSize(size, remaining);
                var colors = new byte[count * 4];
                if (Native.GetPaletteEntries(handle, 0, count, colors) != count) throw new InvalidOperationException("图片调色板读取不完整。");
                bytes = new byte[(int)size]; bytes[1] = 3; BitConverter.GetBytes((ushort)count).CopyTo(bytes, 2); colors.CopyTo(bytes, 4);
                Array.Clear(colors);
                break;
            case ClipboardStorage.EnhancedMetafile:
                size = Native.GetEnhMetaFileBits(handle, 0, null); CheckSize(size, remaining);
                copy = Native.CopyEnhMetaFile(handle, null);
                break;
            case ClipboardStorage.MetafilePicture:
                if (Native.GlobalSize(handle) < (nuint)Marshal.SizeOf<Native.MetafilePicture>()) throw new InvalidOperationException("矢量图片结构不完整。");
                var pointer = Native.GlobalLock(handle);
                if (pointer == 0) throw new InvalidOperationException("无法读取矢量图片结构。");
                try { picture = Marshal.PtrToStructure<Native.MetafilePicture>(pointer); }
                finally { Native.GlobalUnlock(handle); }
                size = Native.GetMetaFileBitsEx(picture.Metafile, 0, null) + Marshal.SizeOf<Native.MetafilePicture>();
                CheckSize(size, remaining); copy = Native.CopyMetaFile(picture.Metafile, null);
                picture.Metafile = 0;
                break;
            default:
                var length = Native.GlobalSize(handle);
                if (length > MaximumItemBytes || length > (nuint)Math.Max(0, remaining) || Native.GlobalFlags(handle) == 0x8000)
                    throw new InvalidOperationException("剪贴板数据过大或无法独立备份，未修改原内容。");
                size = (long)length; bytes = new byte[(int)length];
                if (length > 0)
                {
                    var data = Native.GlobalLock(handle);
                    if (data == 0) throw new InvalidOperationException("剪贴板数据无法读取，未修改原内容。");
                    try { Marshal.Copy(data, bytes, 0, bytes.Length); }
                    finally { Native.GlobalUnlock(handle); }
                }
                break;
        }
        if (kind is ClipboardStorage.Bitmap or ClipboardStorage.EnhancedMetafile or ClipboardStorage.MetafilePicture && copy == 0)
            throw new InvalidOperationException("无法独立复制剪贴板图片，未修改原内容。");
        return new(format, kind, bytes, copy, picture, size);
    }

    private static void CheckSize(long size, long remaining)
    {
        if (size <= 0 || size > MaximumItemBytes || size > remaining)
            throw new InvalidOperationException("剪贴板内容超过安全备份上限（单格式64MiB、总计128MiB），未修改原内容。");
    }
    public IClipboardWrite PrepareRestore() => NativeClipboardWrite.FromEntries(Entries);
    public void Dispose() { foreach (var entry in Entries) entry.Dispose(); Entries.Clear(); }
}

internal sealed class NativeClipboardWrite : IClipboardWrite
{
    private sealed class Transfer(uint format, ClipboardStorage kind, nint handle, nint inner = 0) : IDisposable
    {
        public readonly uint Format = format;
        public nint Handle = handle;
        public nint Inner = inner;
        public void Dispose()
        {
            if (Handle == 0) { if (Inner != 0) Native.DeleteMetaFile(Inner); Inner = 0; return; }
            if (kind is ClipboardStorage.Bitmap or ClipboardStorage.Palette) Native.DeleteObject(Handle);
            else if (kind == ClipboardStorage.EnhancedMetafile) Native.DeleteEnhMetaFile(Handle);
            else { if (Inner != 0) Native.DeleteMetaFile(Inner); Native.GlobalFree(Handle); }
            Handle = Inner = 0;
        }
    }
    private readonly List<Transfer> _items = new();

    internal static NativeClipboardWrite FromEntries(IEnumerable<ClipboardEntry> entries)
    {
        var result = new NativeClipboardWrite();
        try { foreach (var entry in entries) result.Add(entry); return result; }
        catch { result.Dispose(); throw; }
    }
    internal static NativeClipboardWrite Text(string text)
    {
        var entries = new[] {
            new ClipboardEntry(13, ClipboardStorage.Global, Encoding.Unicode.GetBytes(text + '\0'), 0, default, 0),
            // Windows adds missing text aliases on CloseClipboard, after the locked sequence check.
            // Supply them explicitly so our own writes cannot be mistaken for an external copy.
            new ClipboardEntry(16, ClipboardStorage.Global, BitConverter.GetBytes(Native.GetThreadLocale()), 0, default, 0),
            new ClipboardEntry(1, ClipboardStorage.Global, EncodeText(text, 0), 0, default, 0),
            new ClipboardEntry(7, ClipboardStorage.Global, EncodeText(text, 1), 0, default, 0),
            new ClipboardEntry(Native.RegisterClipboardFormat("CanIncludeInClipboardHistory"), ClipboardStorage.Global, new byte[4], 0, default, 0),
            new ClipboardEntry(Native.RegisterClipboardFormat("CanUploadToCloudClipboard"), ClipboardStorage.Global, new byte[4], 0, default, 0)
        };
        try { return FromEntries(entries); }
        finally { foreach (var entry in entries) entry.Dispose(); }
    }

    private static byte[] EncodeText(string text, uint codePage)
    {
        var terminated = text + '\0';
        var length = Native.WideCharToMultiByte(codePage, 0, terminated, terminated.Length, null, 0, 0, 0);
        if (length <= 0) throw new InvalidOperationException("无法准备剪贴板文字格式。");
        var bytes = new byte[length];
        if (Native.WideCharToMultiByte(codePage, 0, terminated, terminated.Length, bytes, bytes.Length, 0, 0) != length)
            throw new InvalidOperationException("无法准备完整的剪贴板文字格式。");
        return bytes;
    }

    private void Add(ClipboardEntry entry)
    {
        nint handle = 0, inner = 0;
        try
        {
            switch (entry.Storage)
            {
                case ClipboardStorage.Global: handle = Allocate(entry.Bytes!); break;
                case ClipboardStorage.Bitmap: handle = Native.CopyImage(entry.Handle, 0, 0, 0, 0x2000); break;
                case ClipboardStorage.Palette: handle = Native.CreatePalette(entry.Bytes!); break;
                case ClipboardStorage.EnhancedMetafile: handle = Native.CopyEnhMetaFile(entry.Handle, null); break;
                case ClipboardStorage.MetafilePicture:
                    var picture = entry.Picture;
                    inner = Native.CopyMetaFile(entry.Handle, null);
                    if (inner == 0) throw new InvalidOperationException("无法准备矢量图片恢复数据。");
                    picture.Metafile = inner;
                    handle = Native.GlobalAlloc(0x42, (nuint)Marshal.SizeOf<Native.MetafilePicture>());
                    if (handle == 0) throw new OutOfMemoryException();
                    var pointer = Native.GlobalLock(handle);
                    if (pointer == 0) throw new InvalidOperationException("无法准备图片恢复内存。");
                    try { Marshal.StructureToPtr(picture, pointer, false); }
                    finally { Native.GlobalUnlock(handle); }
                    break;
            }
            if (handle == 0) throw new InvalidOperationException("无法准备剪贴板恢复数据。");
            _items.Add(new(entry.Format, entry.Storage, handle, inner));
        }
        catch { new Transfer(entry.Format, entry.Storage, handle, inner).Dispose(); throw; }
    }

    private static nint Allocate(byte[] bytes)
    {
        var handle = Native.GlobalAlloc(0x42, (nuint)bytes.Length);
        if (handle == 0) throw new OutOfMemoryException();
        if (bytes.Length == 0) return handle;
        var pointer = Native.GlobalLock(handle);
        if (pointer == 0) { Native.GlobalFree(handle); throw new InvalidOperationException("无法准备剪贴板内存。"); }
        try { Marshal.Copy(bytes, 0, pointer, bytes.Length); }
        catch { Native.GlobalUnlock(handle); Native.GlobalFree(handle); throw; }
        Native.GlobalUnlock(handle);
        return handle;
    }
    public void Commit()
    {
        foreach (var item in _items)
        {
            if (item.Handle == 0) throw new InvalidOperationException("剪贴板写入动作不可重复。");
            if (Native.SetClipboardData(item.Format, item.Handle) == 0) throw new InvalidOperationException("无法设置剪贴板数据。");
            item.Handle = item.Inner = 0; // Ownership has transferred to Windows.
        }
    }
    public void Dispose() { foreach (var item in _items) item.Dispose(); _items.Clear(); }
}

internal sealed class NativeClipboardBackend : IClipboardBackend
{
    private readonly nint _owner;
    private readonly bool _ownsWindow;
    private readonly uint _thread;
    public NativeClipboardBackend(nint owner)
    {
        _thread = Native.GetCurrentThreadId();
        Native.GetWindowThreadProcessId(owner, out var pid);
        if (owner != 0 && pid == Environment.ProcessId) _owner = owner;
        else
        {
            _owner = Native.CreateWindowEx(0, "STATIC", "WechatDraft clipboard", 0, 0, 0, 0, 0, new nint(-3), 0, Native.GetModuleHandle(null), 0);
            if (_owner == 0) throw new InvalidOperationException("无法创建剪贴板保护窗口。");
            _ownsWindow = true;
        }
    }
    internal nint OwnerWindow => _owner;
    public bool Open() => Native.OpenClipboard(_owner);
    public void Close() => Native.CloseClipboard();
    public uint Sequence => Native.GetClipboardSequenceNumber();
    public uint OwnerProcess { get { Native.GetWindowThreadProcessId(Native.GetClipboardOwner(), out var pid); return pid; } }
    public IClipboardSnapshot Capture() => NativeClipboardSnapshot.Capture();
    public IClipboardWrite PrepareText(string text) => NativeClipboardWrite.Text(text);
    public void Empty() { if (!Native.EmptyClipboard()) throw new InvalidOperationException("无法准备剪贴板，未继续操作微信。"); }
    public string ReadPlainText()
    {
        if (!Native.IsClipboardFormatAvailable(13) || new uint[] { 2, 8, 15, 17 }.Any(Native.IsClipboardFormatAvailable))
            throw new InvalidOperationException("草稿不是可完整读取的普通文本，已停止操作。");
        foreach (var name in new[] { "PNG", "JFIF", "FileContents", "FileGroupDescriptorW" })
            if (Native.IsClipboardFormatAvailable(Native.RegisterClipboardFormat(name)))
                throw new InvalidOperationException("草稿包含图片或附件，未继续填写。");
        foreach (var name in new[] { "HTML Format", "Rich Text Format" })
        {
            var format = Native.RegisterClipboardFormat(name);
            if (!Native.IsClipboardFormatAvailable(format)) continue;
            var data = Native.GetClipboardData(format); var size = Native.GlobalSize(data);
            if (data == 0 || size > 1024 * 1024) throw new InvalidOperationException("草稿附加格式无法核对，未继续填写。");
            var memory = Native.GlobalLock(data);
            if (memory == 0) throw new InvalidOperationException("无法核对草稿附加格式。");
            var bytes = new byte[(int)size];
            try
            {
                Marshal.Copy(memory, bytes, 0, bytes.Length);
                if (ContainsEmbeddedObject(Encoding.ASCII.GetString(bytes), name == "Rich Text Format"))
                    throw new InvalidOperationException("草稿包含图片或嵌入对象，未继续填写。");
            }
            finally { Array.Clear(bytes); Native.GlobalUnlock(data); }
        }
        var handle = Native.GetClipboardData(13); var length = Native.GlobalSize(handle);
        if (handle == 0 || length < 2 || length > 131074 || length % 2 != 0) throw new InvalidOperationException("草稿文本无法完整读取或过大。");
        var pointer = Native.GlobalLock(handle);
        if (pointer == 0) throw new InvalidOperationException("无法读取草稿文本。");
        try
        {
            var text = Marshal.PtrToStringUni(pointer, (int)length / 2)!.TrimEnd('\0');
            if (text.Contains('\0') || text.Contains('\uFFFC')) throw new InvalidOperationException("草稿文本含不支持的对象。");
            return text;
        }
        finally { Native.GlobalUnlock(handle); }
    }
    internal static bool ContainsEmbeddedObject(string content, bool rtf) => rtf
        ? new[] { @"\pict", @"\object", @"\objdata" }.Any(token => content.Contains(token, StringComparison.OrdinalIgnoreCase))
        : new[] { "<img", "<object", "<embed", "<svg", "<video", "<audio" }.Any(token => content.Contains(token, StringComparison.OrdinalIgnoreCase));
    public void Dispose()
    {
        if (!_ownsWindow) return;
        if (Native.GetCurrentThreadId() == _thread) Native.DestroyWindow(_owner);
        else Native.PostMessage(_owner, 0x10, 0, 0);
    }
}
