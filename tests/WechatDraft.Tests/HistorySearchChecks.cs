using WechatDraft.Core;

internal static class HistorySearchChecks
{
    public static IEnumerable<(string Name, Func<Task> Run)> Cases(string root, ISecretProtector protector)
    {
        (LocalRepository Repository, CountingProtector Protector) Create()
        {
            var counting = new CountingProtector(protector);
            return (new(Path.Combine(root, "history-" + Guid.NewGuid().ToString("N")), counting), counting);
        }
        void Seed(LocalRepository repository, int count, string scope = "default")
        {
            var started = DateTimeOffset.UtcNow;
            for (var index = 0; index < count; index++) repository.SaveTask(new()
            {
                Id = scope + "-" + index.ToString("D4"), AccountScope = scope, StartedAt = started.AddSeconds(-index),
                CompletedAt = started, DisplayName = "合成联系人 " + index, Scene = "生日", Greeting = "Cached greeting " + index,
                FillStatus = "已预填"
            });
        }
        yield return ("Initial history is bounded and repeated searches reuse decrypted records", async () =>
        {
            var (repository, counting) = Create(); Seed(repository, 160);
            var session = new HistorySearchSession(repository, "default");
            var first = await session.SearchAsync("", 0, 40);
            Equal(40, first.Records.Count); Equal(true, first.HasMore);
            True(counting.Reads < 100, "initial page decrypted the entire history");
            var reads = counting.Reads;
            var searched = await session.SearchAsync("  CACHED  ", 0, 40);
            Equal(40, searched.Records.Count); Equal(reads, counting.Reads);
            Equal(first.Records[0].Id, searched.Records[0].Id);
        });
        yield return ("History search finds matches older than the original hundred-row limit and isolates scopes", async () =>
        {
            var (repository, _) = Create(); Seed(repository, 160);
            var old = repository.History("default", 160).Last(); old.Greeting = "older-needle"; repository.SaveTask(old);
            Seed(repository, 1, "other");
            var other = repository.History("other").Single(); other.Greeting = "older-needle"; repository.SaveTask(other);
            var result = await new HistorySearchSession(repository, "default").SearchAsync("OLDER-NEEDLE", 0, 40);
            Equal(1, result.Records.Count); Equal(old.Id, result.Records[0].Id);
            Equal(1, result.TotalMatches); Equal(false, result.HasMore);
        });
        yield return ("History keyset pagination is stable when all creation times are identical", () =>
        {
            var (repository, _) = Create(); var started = DateTimeOffset.UtcNow;
            for (var index = 0; index < 19; index++) repository.SaveTask(new()
            { Id = index.ToString("D3"), AccountScope = "same", StartedAt = started, CompletedAt = started });
            HistoryCursor? cursor = null; var ids = new List<string>();
            while (true)
            {
                var batch = repository.ReadHistoryBatch("same", cursor, 4);
                ids.AddRange(batch.Records.Select(x => x.Id)); cursor = batch.Cursor;
                if (!batch.HasMore) break;
            }
            Equal(19, ids.Count); Equal(19, ids.Distinct().Count());
            True(ids.SequenceEqual(Enumerable.Range(0, 19).Reverse().Select(x => x.ToString("D3"))), "unstable equal-time order");
            return Task.CompletedTask;
        });
        yield return ("History loading more appends the next matching page without duplicates", async () =>
        {
            var (repository, _) = Create(); Seed(repository, 91);
            var session = new HistorySearchSession(repository, "default"); var ids = new List<string>();
            var more = true;
            while (more)
            {
                var page = await session.SearchAsync("生日", ids.Count, 40);
                ids.AddRange(page.Records.Select(x => x.Id)); more = page.HasMore;
            }
            Equal(91, ids.Count); Equal(91, ids.Distinct().Count());
        });
        yield return ("History read and decrypt work runs outside the calling UI thread", () =>
        {
            var (repository, counting) = Create(); Seed(repository, 3);
            var caller = Environment.CurrentManagedThreadId;
            var result = new HistorySearchSession(repository, "default").SearchAsync("", 0, 40).GetAwaiter().GetResult();
            Equal(3, result.Records.Count); True(counting.LastReadThread != caller, "decryption blocked the calling UI thread");
            return Task.CompletedTask;
        });
        yield return ("Cancelling a superseded history query does not skip records in the next query", async () =>
        {
            var (repository, counting) = Create(); Seed(repository, 80);
            using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
            counting.OnRead = () => { entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(5))) throw new Exception("read not released"); };
            var session = new HistorySearchSession(repository, "default"); using var cancellation = new CancellationTokenSource();
            var first = session.SearchAsync("old query", 0, 40, cancellation.Token);
            try
            {
                True(entered.Wait(TimeSpan.FromSeconds(5)), "background read did not start");
                cancellation.Cancel();
            }
            finally { release.Set(); }
            try { await first; throw new Exception("superseded query was not cancelled"); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            counting.OnRead = null;
            var current = await session.SearchAsync("Cached", 0, 40);
            Equal(40, current.Records.Count); Equal("default-0000", current.Records[0].Id);
        });
        yield return ("A fresh history session reflects deletions and clearing instead of reviving cached rows", async () =>
        {
            var (repository, _) = Create(); Seed(repository, 3);
            var first = await new HistorySearchSession(repository, "default").SearchAsync("", 0, 40);
            repository.DeleteTask(first.Records[0].Id);
            var remaining = await new HistorySearchSession(repository, "default").SearchAsync("", 0, 40);
            Equal(2, remaining.Records.Count); True(remaining.Records.All(x => x.Id != first.Records[0].Id), "deleted row reappeared");
            repository.ClearHistory();
            var empty = await new HistorySearchSession(repository, "default").SearchAsync("", 0, 40);
            Equal(0, empty.Records.Count); Equal(false, empty.HasMore); Equal(0, empty.TotalMatches);
        });
        yield return ("Cancelled history requests decrypt nothing and parameterized scope cannot read another account", async () =>
        {
            var (repository, counting) = Create(); Seed(repository, 2);
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            try
            {
                await new HistorySearchSession(repository, "default").SearchAsync("", 0, 40, cancellation.Token);
                throw new Exception("cancelled page was loaded");
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            Equal(0, counting.Reads);
            var result = await new HistorySearchSession(repository, "' OR 1=1 --").SearchAsync("", 0, 40);
            Equal(0, result.Records.Count); Equal(0, counting.Reads);
        });
    }

    private sealed class CountingProtector(ISecretProtector inner) : ISecretProtector
    {
        private int _reads;
        public int Reads => Volatile.Read(ref _reads);
        public int LastReadThread { get; private set; }
        public Action? OnRead { get; set; }
        public string Protect(string value) => inner.Protect(value);
        public string Unprotect(string value)
        {
            OnRead?.Invoke(); LastReadThread = Environment.CurrentManagedThreadId;
            Interlocked.Increment(ref _reads); return inner.Unprotect(value);
        }
    }

    private static void Equal<T>(T expected, T actual)
    { if (!Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}."); }
    private static void True(bool condition, string message) { if (!condition) throw new Exception(message); }
}
