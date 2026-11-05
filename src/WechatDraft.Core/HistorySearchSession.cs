namespace WechatDraft.Core;

public sealed record HistoryCursor(string Created, string Id);
public sealed record HistoryBatch(IReadOnlyList<TaskRecord> Records, HistoryCursor? Cursor, bool HasMore);
public sealed record HistorySearchResult(IReadOnlyList<TaskRecord> Records, bool HasMore, int? TotalMatches);

// Decrypted history belongs only to this page's lifetime. No plaintext search index is persisted.
public sealed class HistorySearchSession(LocalRepository repository, string scope)
{
    private sealed record Entry(TaskRecord Record, string SearchText);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<Entry> _cache = [];
    private HistoryCursor? _cursor;
    private bool _exhausted;

    public Task<HistorySearchResult> SearchAsync(string keyword, int offset, int pageSize, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        var query = keyword.Trim();
        pageSize = Math.Clamp(pageSize, 1, 200);
        return Task.Run(async () =>
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                bool Matches(Entry entry) => query.Length == 0 || entry.SearchText.Contains(query, StringComparison.OrdinalIgnoreCase);
                var matches = new List<TaskRecord>();
                foreach (var entry in _cache)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (Matches(entry)) matches.Add(entry.Record);
                }
                var needed = (long)offset + pageSize;
                while (matches.Count <= needed && !_exhausted)
                {
                    var batch = repository.ReadHistoryBatch(scope, _cursor, 64, cancellationToken);
                    // Advance only after the entire batch is read, so cancellation cannot skip records.
                    foreach (var record in batch.Records)
                    {
                        var entry = new Entry(record, string.Join('\n', record.DisplayName, record.Scene, record.Greeting, record.FillStatus));
                        _cache.Add(entry);
                        if (Matches(entry)) matches.Add(record);
                    }
                    _cursor = batch.Cursor; _exhausted = !batch.HasMore;
                    cancellationToken.ThrowIfCancellationRequested();
                }
                return new HistorySearchResult(matches.Skip(offset).Take(pageSize).ToArray(), matches.Count > needed,
                    _exhausted ? matches.Count : null);
            }
            finally { _gate.Release(); }
        }, cancellationToken);
    }
}
