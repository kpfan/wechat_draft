namespace WechatDraft.Core;

public sealed record HistoryCursor(string Created, string Id);
public sealed record HistoryBatch(IReadOnlyList<TaskRecord> Records, HistoryCursor? Cursor, bool HasMore);
public sealed record HistorySearchResult(IReadOnlyList<TaskRecord> Records, bool HasMore, int? TotalMatches);

public sealed record HistoryFilter
{
    public string Keyword { get; init; } = "";
    public string Contact { get; init; } = "";
    public string Scene { get; init; } = "";
    public string FillStatus { get; init; } = "";
    public DateOnly? FromDate { get; init; }
    public DateOnly? ToDate { get; init; }
    public bool? IsDemo { get; init; }

    internal bool Matches(TaskRecord record, string searchText)
    {
        static bool Contains(string text, string query) => text.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase);
        var date = DateOnly.FromDateTime(record.StartedAt.ToLocalTime().DateTime);
        return Contains(searchText, Keyword) && Contains(record.DisplayName, Contact) && Contains(record.Scene, Scene)
            && (string.IsNullOrWhiteSpace(FillStatus) || string.Equals(record.FillStatus, FillStatus.Trim(), StringComparison.Ordinal))
            && (FromDate is null || date >= FromDate.Value) && (ToDate is null || date <= ToDate.Value)
            && (IsDemo is null || record.IsDemo == IsDemo.Value);
    }
}

// Decrypted history belongs only to this page's lifetime. No plaintext search index is persisted.
public sealed class HistorySearchSession(LocalRepository repository, string scope)
{
    private sealed record Entry(TaskRecord Record, string SearchText);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<Entry> _cache = [];
    private HistoryCursor? _cursor;
    private bool _exhausted;

    public Task<HistorySearchResult> SearchAsync(string keyword, int offset, int pageSize, CancellationToken cancellationToken = default)
        => SearchAsync(new HistoryFilter { Keyword = keyword }, offset, pageSize, cancellationToken);

    public Task<HistorySearchResult> SearchAsync(HistoryFilter filter, int offset, int pageSize, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (filter.FromDate is { } from && filter.ToDate is { } to && from > to)
            throw new ArgumentException("开始日期不能晚于结束日期。", nameof(filter));
        pageSize = Math.Clamp(pageSize, 1, 200);
        return Task.Run(async () =>
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                bool Matches(Entry entry) => filter.Matches(entry.Record, entry.SearchText);
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
