using WechatDraft.Windows;

static class ClipboardChecks
{
    public static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("Clipboard restores all original formats and byte content", () =>
        {
            var backend = new FakeClipboard();
            backend.Data = new() { [13] = [32, 0, 10, 0], [49313] = [0, 255, 7], [17] = [1, 2, 3, 4] };
            var original = Clone(backend.Data);
            using (var session = new ClipboardSession(backend)) { session.PutText("temporary"); session.PutText("suffix"); }
            Same(original, backend.Data);
            Require(backend.Empties == 3 && backend.Closed && backend.Disposed && backend.SnapshotDisposed, "restore or cleanup missing");
        });
        yield return ("Clipboard restores an originally empty clipboard", () =>
        {
            var backend = new FakeClipboard();
            var session = new ClipboardSession(backend); session.PutText("probe"); session.Dispose(); session.Dispose();
            Require(backend.Data.Count == 0 && backend.Empties == 2 && backend.Disposals == 1, "empty/idempotent restore");
        });
        yield return ("Failed clipboard backup and preallocation never empty the original", () =>
        {
            var failed = new FakeClipboard { CaptureFails = true }; failed.Data[13] = [42];
            Invalid(() => new ClipboardSession(failed));
            Require(failed.Empties == 0 && failed.Disposed && failed.Closed, "capture changed original");
            var backend = new FakeClipboard { PrepareFails = true }; backend.Data[13] = [42];
            using (var session = new ClipboardSession(backend)) Invalid(() => session.PutText("probe"));
            Require(backend.Empties == 0 && backend.Data[13][0] == 42, "allocation destroyed original");
        });
        yield return ("New external clipboard data blocks writes and survives disposal", () =>
        {
            var backend = new FakeClipboard();
            using (var session = new ClipboardSession(backend))
            {
                session.PutText("probe"); backend.Copy(99, "new user copy");
                Invalid(() => session.PutText("suffix"));
            }
            Require(backend.Text == "new user copy" && backend.Empties == 1, "new copy overwritten");
        });
        yield return ("Clipboard is rechecked after acquiring the restore lock", () =>
        {
            var backend = new FakeClipboard(); var session = new ClipboardSession(backend); session.PutText("probe");
            backend.OnOpen = () => backend.Copy(99, "racing copy"); session.Dispose();
            Require(backend.Text == "racing copy" && backend.Empties == 1, "restore race lost new content");
        });
        yield return ("Wrong copy source is not adopted for clipboard restoration", () =>
        {
            var backend = new FakeClipboard();
            using (var session = new ClipboardSession(backend))
            {
                session.PutText("probe"); backend.Copy(99, "other process");
                Invalid(() => session.TryReadCopied(7, out _));
            }
            Require(backend.Text == "other process" && backend.Empties == 1, "foreign copy adopted");
        });
        yield return ("Copied text preserves whitespace and delayed rendering sequence", () =>
        {
            var backend = new FakeClipboard(); backend.Data[49313] = [4, 5, 6]; var original = Clone(backend.Data);
            using (var session = new ClipboardSession(backend))
            {
                session.PutText("probe"); Require(!session.TryReadCopied(7, out _), "stale copy accepted");
                backend.Copy(7, " 原文 \r\n\r\n"); backend.RenderChangesSequence = true;
                Require(session.TryReadCopied(7, out var text) && text == " 原文 \r\n\r\n", "copy normalized");
                session.PutText("suffix");
            }
            Same(original, backend.Data);
        });
        yield return ("Partial clipboard write failure still restores the original", () =>
        {
            var backend = new FakeClipboard { CommitFails = true }; backend.Data[49313] = [8, 9]; var original = Clone(backend.Data);
            using (var session = new ClipboardSession(backend)) Invalid(() => session.PutText("probe"));
            Same(original, backend.Data); Require(backend.WriteDisposals == 2, "untransferred handles not disposed");
        });
        yield return ("Busy clipboard restoration reports failure and releases the session gate", () =>
        {
            var backend = new FakeClipboard(); var session = new ClipboardSession(backend); session.PutText("probe"); backend.Busy = true;
            Invalid(session.Dispose);
            Require(backend.Disposed && backend.SnapshotDisposed, "failed restore leaked resources");
            using var next = new ClipboardSession(new FakeClipboard());
        });
    }

    private static Dictionary<uint, byte[]> Clone(Dictionary<uint, byte[]> data) => data.ToDictionary(x => x.Key, x => x.Value.ToArray());
    private static void Same(Dictionary<uint, byte[]> expected, Dictionary<uint, byte[]> actual) =>
        Require(expected.Keys.SequenceEqual(actual.Keys) && expected.All(x => actual[x.Key].SequenceEqual(x.Value)), "format order or bytes differ");
    private static void Require(bool value, string error) { if (!value) throw new Exception(error); }
    private static void Invalid(Action action)
    {
        try { action(); } catch (InvalidOperationException) { return; }
        throw new Exception("expected guarded failure");
    }

    private sealed class FakeClipboard : IClipboardBackend
    {
        public Dictionary<uint, byte[]> Data = new();
        public string Text = "";
        public uint Sequence { get; private set; } = 1;
        public uint OwnerProcess { get; private set; } = 7;
        public bool Busy, CaptureFails, PrepareFails, CommitFails, RenderChangesSequence;
        public bool Closed = true, Disposed, SnapshotDisposed;
        public int Empties, Disposals, WriteDisposals;
        public Action? OnOpen;
        public bool Open() { if (Busy) return false; var action = OnOpen; OnOpen = null; action?.Invoke(); Closed = false; return true; }
        public void Close() => Closed = true;
        public void Copy(uint owner, string text) { OwnerProcess = owner; Text = text; Data = new() { [13] = System.Text.Encoding.Unicode.GetBytes(text + '\0') }; Sequence++; }
        public IClipboardSnapshot Capture()
        {
            if (CaptureFails) throw new InvalidOperationException("unsupported");
            return new Snapshot(this, Clone(Data));
        }
        public IClipboardWrite PrepareText(string text)
        {
            if (PrepareFails) throw new InvalidOperationException("allocation");
            return new Write(this, () => { Copy(7, text); if (CommitFails) throw new InvalidOperationException("partial commit"); });
        }
        public void Empty() { Require(!Closed, "empty without lock"); Data.Clear(); Text = ""; Sequence++; Empties++; }
        public string ReadPlainText() { if (RenderChangesSequence) Sequence++; return Text; }
        public void Dispose() { Disposed = true; Disposals++; }
        private sealed class Snapshot(FakeClipboard owner, Dictionary<uint, byte[]> data) : IClipboardSnapshot
        {
            public IClipboardWrite PrepareRestore() => new Write(owner, () => { owner.Data = Clone(data); owner.Sequence++; });
            public void Dispose() => owner.SnapshotDisposed = true;
        }
        private sealed class Write(FakeClipboard owner, Action action) : IClipboardWrite
        {
            public void Commit() => action();
            public void Dispose() => owner.WriteDisposals++;
        }
    }
}
