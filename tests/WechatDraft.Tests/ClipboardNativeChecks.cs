using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using WechatDraft.Windows;

// Explicitly selected integration check: temporarily uses the real clipboard, never WeChat.
// Preserve the user's contents before fixtures; restore only while our sequence is still current.
static class ClipboardNativeChecks
{
    public static void Run()
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { Check(); } catch (Exception e) { failure = e; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure is not null) throw new Exception("Native clipboard check: " + failure.Message + "\n" + failure.StackTrace, failure);
    }

    private static void Check()
    {
        using var backend = new NativeClipboardBackend(0);
        var original = Capture(backend, out var sequence);
        using (original)
        {
            var touched = false;
            void Publish(IEnumerable<ClipboardEntry> entries)
            {
                using var write = NativeClipboardWrite.FromEntries(entries); Open(backend);
                try
                {
                    if (sequence != backend.Sequence) throw new Exception("External clipboard change; test stopped");
                    backend.Empty(); touched = true;
                    try { write.Commit(); } finally { sequence = backend.Sequence; }
                }
                finally { backend.Close(); }
            }
            void RoundTrip(string name)
            {
                var before = Capture(backend, out sequence);
                using (before)
                {
                    using (var session = new ClipboardSession(backend.OwnerWindow))
                    {
                        session.PutText("synthetic probe 原文 \r\n");
                        session.PutText("synthetic suffix 祝福");
                        sequence = backend.Sequence;
                    }
                    // If the user copied during the session, leave that new copy intact and stop.
                    if (backend.OwnerProcess != Environment.ProcessId) throw new Exception("External clipboard change; test stopped");
                    sequence = backend.Sequence;
                    using var after = Capture(backend, out sequence);
                    Same(before, after);
                    Console.WriteLine("PASS native " + name);
                }
            }
            try
            {
                using (var probe = NativeClipboardWrite.Text("synthetic sequence probe"))
                {
                    Open(backend);
                    try
                    {
                        if (backend.Sequence != sequence) throw new Exception("External clipboard change; test stopped");
                        backend.Empty(); touched = true; probe.Commit();
                        sequence = backend.Sequence;
                    }
                    finally { backend.Close(); }
                    if (sequence != backend.Sequence) throw new Exception("Text aliases changed sequence on CloseClipboard");
                }
                var rich = new[] {
                    Global(13, Encoding.Unicode.GetBytes(" 原文 \r\n\r\n\0")),
                    Global(Native.RegisterClipboardFormat("HTML Format"), Encoding.UTF8.GetBytes("Version:0.9\r\n<html><body><!--StartFragment--><b>测试</b><!--EndFragment--></body></html>\0")),
                    Global(Native.RegisterClipboardFormat("Rich Text Format"), Encoding.ASCII.GetBytes("{\\rtf1\\ansi synthetic \\b bold}\\0")),
                    Global(Native.RegisterClipboardFormat("Chromium internal source RFH token"), Enumerable.Range(0, 32).Select(x => (byte)x).ToArray()),
                    Global(Native.RegisterClipboardFormat("WechatDraft synthetic opaque"), new byte[5 * 1024 * 1024])
                };
                try { rich[4].Bytes![1234] = 91; Publish(rich); RoundTrip("HTML/RTF/Unicode/browser metadata/5MiB"); }
                finally { foreach (var entry in rich) entry.Dispose(); }

                using var bitmap = new Bitmap(2, 2, PixelFormat.Format32bppArgb);
                bitmap.SetPixel(0, 0, Color.Red); bitmap.SetPixel(1, 0, Color.Blue);
                bitmap.SetPixel(0, 1, Color.Green); bitmap.SetPixel(1, 1, Color.White);
                using var png = new MemoryStream(); bitmap.Save(png, ImageFormat.Png);
                var images = new[] {
                    new ClipboardEntry(2, ClipboardStorage.Bitmap, null, bitmap.GetHbitmap(), default, 16),
                    Global(8, Dib(false)), Global(17, Dib(true)),
                    Global(Native.RegisterClipboardFormat("PNG"), png.ToArray())
                };
                try { Publish(images); RoundTrip("PNG/DIB/DIBV5/HBITMAP"); }
                finally { foreach (var entry in images) entry.Dispose(); }

                Publish([]); RoundTrip("empty clipboard");
                void RejectWithoutChange(string name)
                {
                    try
                    {
                        using var unsupported = new ClipboardSession(backend.OwnerWindow);
                        throw new Exception("Unrestorable file clipboard was accepted");
                    }
                    catch (InvalidOperationException)
                    {
                        if (backend.Sequence != sequence) throw new Exception("Rejected file clipboard changed");
                    }
                    Console.WriteLine("PASS native " + name);
                }
                var virtualFile = Global(Native.RegisterClipboardFormat("FileGroupDescriptorW"), new byte[32]);
                try
                {
                    Publish([virtualFile]);
                    RejectWithoutChange("unsupported virtual file leaves clipboard unchanged");
                }
                finally { virtualFile.Dispose(); }
                var folder = Directory.CreateTempSubdirectory("WechatDraft-clipboard-test-");
                var paths = new[] { Path.Combine(folder.FullName, "测试甲.txt"), Path.Combine(folder.FullName, "test-b.txt") };
                try
                {
                    foreach (var path in paths) File.WriteAllText(path, "synthetic file");
                    foreach (var selection in new[] { paths[..1], paths })
                    {
                        var files = FileCopy(selection);
                        try
                        {
                            Publish(files);
                            RoundTrip("file paths and descriptors");
                            var restoredPaths = System.Windows.Forms.Clipboard.GetFileDropList().Cast<string>();
                            if (!selection.SequenceEqual(restoredPaths)) throw new Exception("Restored file list differs");
                            using var stream = Global(Native.RegisterClipboardFormat("FileContents"), [1, 2, 3]);
                            Publish([.. files, stream]);
                            RejectWithoutChange("file paths do not permit a mixed FileContents payload");
                        }
                        finally { foreach (var entry in files) entry.Dispose(); }
                    }
                }
                finally
                {
                    foreach (var path in paths) File.Delete(path);
                    folder.Delete();
                }
                // Exercise OLE delayed-data materialization using synthetic .NET rich text.
                if (backend.Sequence != sequence) throw new Exception("External clipboard change; test stopped");
                var ole = new System.Windows.Forms.DataObject();
                ole.SetText("synthetic OLE text", System.Windows.Forms.TextDataFormat.UnicodeText);
                ole.SetText("{\\rtf1 synthetic OLE}", System.Windows.Forms.TextDataFormat.Rtf);
                System.Windows.Forms.Clipboard.SetDataObject(ole, true);
                sequence = backend.Sequence;
                RoundTrip("OLE rich text payload");
            }
            finally
            {
                if (touched && backend.Sequence == sequence)
                {
                    using var restore = original.PrepareRestore(); Open(backend);
                    var restoredOriginal = false;
                    try
                    {
                        if (backend.Sequence == sequence)
                        {
                            backend.Empty(); restore.Commit();
                            restoredOriginal = true;
                        }
                    }
                    finally { backend.Close(); }
                    if (restoredOriginal)
                    {
                        using var restored = Capture(backend, out sequence);
                        Same(original, restored);
                        Console.WriteLine("PASS native original user clipboard restored (contents not logged)");
                    }
                }
            }
        }
    }

    private static ClipboardEntry Global(uint format, byte[] bytes) => new(format, ClipboardStorage.Global, bytes, 0, default, bytes.Length);

    private static ClipboardEntry[] FileCopy(string[] paths)
    {
        const int dropHeader = 20, descriptorSize = 592, nameOffset = 72;
        var pathBytes = Encoding.Unicode.GetBytes(string.Join('\0', paths) + "\0\0");
        var drop = new byte[dropHeader + pathBytes.Length];
        BitConverter.GetBytes(dropHeader).CopyTo(drop, 0);
        BitConverter.GetBytes(1).CopyTo(drop, 16);
        pathBytes.CopyTo(drop, dropHeader);
        var descriptors = new byte[4 + paths.Length * descriptorSize];
        BitConverter.GetBytes(paths.Length).CopyTo(descriptors, 0);
        for (var i = 0; i < paths.Length; i++)
            Encoding.Unicode.GetBytes(Path.GetFileName(paths[i])).CopyTo(descriptors, 4 + i * descriptorSize + nameOffset);
        return [Global(15, drop), Global(Native.RegisterClipboardFormat("FileGroupDescriptorW"), descriptors),
            Global(Native.RegisterClipboardFormat("Preferred DropEffect"), BitConverter.GetBytes(1))];
    }

    private static NativeClipboardSnapshot Capture(NativeClipboardBackend backend, out uint sequence)
    {
        backend.PrepareCapture();
        Open(backend);
        try
        {
            var snapshot = (NativeClipboardSnapshot)backend.Capture();
            sequence = backend.Sequence;
            return snapshot;
        }
        finally { backend.Close(); }
    }

    private static void Open(IClipboardBackend backend)
    {
        for (var i = 0; i < 5; i++) { if (backend.Open()) return; Thread.Sleep(20); }
        throw new Exception("Clipboard busy; native test stopped");
    }
    private static void Same(NativeClipboardSnapshot before, NativeClipboardSnapshot after)
    {
        if (!before.Entries.Select(x => x.Format).SequenceEqual(after.Entries.Select(x => x.Format))) throw new Exception("Restored formats/order differ");
        foreach (var expected in before.Entries)
        {
            var actual = after.Entries.Single(x => x.Format == expected.Format);
            if (expected.Storage != actual.Storage) throw new Exception("Restored storage differs");
            if (expected.Bytes is not null && !expected.Bytes.SequenceEqual(actual.Bytes!)) throw new Exception("Restored payload differs for format " + expected.Format);
            if (expected.Storage == ClipboardStorage.Bitmap)
            {
                using var left = Image.FromHbitmap(expected.Handle); using var right = Image.FromHbitmap(actual.Handle);
                if (left.Size != right.Size) throw new Exception("Restored image size differs");
                using var a = new Bitmap(left); using var b = new Bitmap(right);
                for (var y = 0; y < a.Height; y++) for (var x = 0; x < a.Width; x++)
                    if (a.GetPixel(x, y) != b.GetPixel(x, y)) throw new Exception("Restored image pixels differ");
            }
        }
    }
    private static byte[] Dib(bool v5)
    {
        var header = v5 ? 124 : 40; var bytes = new byte[header + 16];
        void Int(int offset, int value) => BitConverter.GetBytes(value).CopyTo(bytes, offset);
        Int(0, header); Int(4, 2); Int(8, 2); bytes[12] = 1; bytes[14] = 32; Int(20, 16);
        if (v5)
        {
            Int(16, 3); Int(40, 0x00ff0000); Int(44, 0x0000ff00); Int(48, 0x000000ff); Int(52, unchecked((int)0xff000000)); Int(56, 0x73524742);
        }
        new byte[] { 0, 128, 0, 255, 255, 255, 255, 255, 0, 0, 255, 255, 255, 0, 0, 255 }.CopyTo(bytes, header);
        return bytes;
    }
}
