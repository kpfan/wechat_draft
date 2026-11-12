using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WechatDraft.Core;

namespace WechatDraft.App;

public sealed partial class HistoryPageView : UserControl, IDisposable
{
    private int _pageSize = 40;
    private int _pageIndex;
    private readonly LocalRepository _repository;
    private readonly string _scope;
    private readonly string _retention;
    private readonly Func<bool> _canModify;
    private readonly Func<string, string, Task<bool>> _confirm;
    private readonly Action<string> _copy;
    private readonly ObservableCollection<HistoryItem> _items = [];
    private HistorySearchSession? _session;
    private CancellationTokenSource? _requestCancellation;
    private int _revision;
    private bool _initialized, _disposed, _loading, _taskBusy, _mutationBusy, _hasMore;
    private HistoryFilter _filter = new();
    private int? _totalMatches;

    public HistoryPageView(LocalRepository repository, string scope, int retentionDays, Func<bool> canModify,
        Func<string, string, Task<bool>> confirm, Action<string> copy)
    {
        _repository = repository; _scope = scope;
        _retention = retentionDays == 0 ? "至手动清理" : retentionDays + " 天";
        _canModify = canModify; _confirm = confirm; _copy = copy;
        _session = new(repository, scope);
        InitializeComponent();
        HistoryList.ItemsSource = _items;
        _initialized = true;
        Loaded += (_, _) => { if (!_disposed) _ = RefreshAsync(false); };
        Unloaded += (_, _) => Dispose();
        UpdateControls();
    }

    private bool CanModify => !_disposed && !_taskBusy && !_loading && !_mutationBusy && _canModify();

    public void SetTaskBusy(bool busy)
    {
        var finished = _taskBusy && !busy;
        _taskBusy = busy; UpdateControls();
        if (finished && !_disposed)
        { _session = new(_repository, _scope); _ = RefreshAsync(false); }
    }

    private void SearchChanged(object sender, TextChangedEventArgs args)
    { if (_initialized && !_disposed) _ = RefreshAsync(true); }

    private void RefreshClicked(object sender, RoutedEventArgs args)
    {
        if (_disposed) return;
        _session = new(_repository, _scope);
        _ = RefreshAsync(false);
    }

    private void FilterChanged(object sender, SelectionChangedEventArgs args)
    { if (_initialized && !_disposed) _ = RefreshAsync(false); }

    private void DateFilterChanged(CalendarDatePicker sender, CalendarDatePickerDateChangedEventArgs args)
    { if (_initialized && !_disposed) _ = RefreshAsync(false); }

    private void PageSizeChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!_initialized || _disposed) return;
        _pageSize = PageSizeBox.SelectedIndex switch { 0 => 20, 2 => 80, _ => 40 };
        _ = RefreshAsync(false);
    }

    private void ResetClicked(object sender, RoutedEventArgs args)
    {
        _initialized = false;
        SearchBox.Text = ContactBox.Text = SceneBox.Text = "";
        StatusBox.SelectedIndex = 0;
        FromPicker.Date = ToPicker.Date = null;
        _initialized = true;
        _ = RefreshAsync(false);
    }

    private HistoryFilter ReadFilter() => new()
    {
        Keyword = SearchBox.Text.Trim(), Contact = ContactBox.Text.Trim(), Scene = SceneBox.Text.Trim(),
        FillStatus = StatusBox.SelectedIndex > 0 ? (string)StatusBox.SelectedItem : "",
        IsDemo = false,
        FromDate = FromPicker.Date is { } from ? DateOnly.FromDateTime(from.DateTime) : null,
        ToDate = ToPicker.Date is { } to ? DateOnly.FromDateTime(to.DateTime) : null
    };

    private Task RefreshAsync(bool debounce) => LoadPageAsync(0, ReadFilter(), debounce);

    private async Task LoadPageAsync(int pageIndex, HistoryFilter filter, bool debounce = false)
    {
        if (_disposed || _session is null) return;
        CancelRequest();
        var revision = _revision;
        var cancellation = _requestCancellation = new();
        var session = _session;

        _loading = true; Feedback.IsOpen = false; UpdateControls();
        try
        {
            if (debounce) await Task.Delay(250, cancellation.Token);
            var result = await session.SearchAsync(filter, checked(pageIndex * _pageSize), _pageSize, cancellation.Token);
            if (result.Records.Count == 0 && pageIndex > 0 && result.TotalMatches is { } total)
            {
                pageIndex = Math.Max(0, (total - 1) / _pageSize);
                result = await session.SearchAsync(filter, pageIndex * _pageSize, _pageSize, cancellation.Token);
            }
            if (_disposed || revision != _revision) return;
            _filter = filter; _pageIndex = pageIndex; _items.Clear();
            foreach (var record in result.Records) _items.Add(CreateItem(record));
            _hasMore = result.HasMore; _totalMatches = result.TotalMatches;
            if (_items.Count > 0) HistoryList.ScrollIntoView(_items[0]);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception e)
        {
            if (!_disposed && revision == _revision) { _hasMore = false; _items.Clear(); _pageIndex = 0; _totalMatches = null; ShowError("历史读取失败：" + e.Message); }
        }
        finally
        {
            if (!_disposed && revision == _revision) { _loading = false; UpdateControls(); }
        }
    }

    private void FirstClicked(object sender, RoutedEventArgs args)
    { if (!_loading && _pageIndex > 0) _ = LoadPageAsync(0, _filter); }

    private void PreviousClicked(object sender, RoutedEventArgs args)
    { if (!_loading && _pageIndex > 0) _ = LoadPageAsync(_pageIndex - 1, _filter); }

    private void NextClicked(object sender, RoutedEventArgs args)
    { if (!_loading && _hasMore) _ = LoadPageAsync(_pageIndex + 1, _filter); }

    private HistoryItem CreateItem(TaskRecord record) => new(record, () => _copy(record.Greeting),
        () => _ = DeleteAsync(record), () => OpenDebug(record), () => CanModify);

    private async Task DeleteAsync(TaskRecord record)
    {
        if (!CanModify) return;
        try
        {
            if (!await _confirm("删除这条历史？", "删除后无法在本应用中恢复。") || !CanModify) return;
            _mutationBusy = true; UpdateControls();
            CancelRequest(); _repository.DeleteTask(record.Id);
            _session = new(_repository, _scope);
            await LoadPageAsync(_pageIndex, ReadFilter());
        }
        catch (Exception e) { if (!_disposed) ShowError("删除失败：" + e.Message); }
        finally { _mutationBusy = false; if (!_disposed) UpdateControls(); }
    }

    private async void ClearClicked(object sender, RoutedEventArgs args)
    {
        if (!CanModify) return;
        try
        {
            if (!await _confirm("清空所有历史？", "这会删除演示和真实任务的全部历史，联系人档案仍可保留。") || !CanModify) return;
            _mutationBusy = true; UpdateControls();
            CancelRequest(); _repository.ClearHistory();
            _session = new(_repository, _scope);
            await RefreshAsync(false);
        }
        catch (Exception e) { if (!_disposed) ShowError("清空失败：" + e.Message); }
        finally { _mutationBusy = false; if (!_disposed) UpdateControls(); }
    }

    private void OpenDebug(TaskRecord record)
    {
        if (_disposed || record.DebugDirectory is not { } debugPath) return;
        try
        {
            var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "debug")) + Path.DirectorySeparatorChar;
            var folder = Path.GetFullPath(debugPath);
            if (!folder.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(folder))
            { ShowError("这条任务的调试目录不存在或不属于当前客户端。"); return; }
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(folder) { UseShellExecute = true });
        }
        catch (Exception e) { ShowError("无法打开调试记录：" + e.Message); }
    }

    private void UpdateControls()
    {
        if (!_initialized || _disposed) return;
        LoadingRing.IsActive = _loading;
        FirstButton.IsEnabled = PreviousButton.IsEnabled = !_loading && _pageIndex > 0;
        NextButton.IsEnabled = !_loading && _hasMore;
        PageText.Text = $"第 {_pageIndex + 1} 页" + (_totalMatches is { } count ? $" / {Math.Max(1, (count + _pageSize - 1) / _pageSize)} 页" : "");
        ClearButton.IsEnabled = CanModify;
        foreach (var item in _items) item.RefreshActions();
        CountText.Text = _loading ? "正在读取历史……" :
            $"本页 {_items.Count} 条" + (_totalMatches is { } total ? $" / 共 {total} 条" : "") + $" · 历史保留 {_retention}";
        EmptyState.Text = ReadFilter() != new HistoryFilter() ? "未找到匹配记录" : "还没有记录";
        EmptyState.Visibility = !_loading && _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowError(string message) { Feedback.Message = message; Feedback.Severity = InfoBarSeverity.Warning; Feedback.IsOpen = true; }

    private void CancelRequest()
    {
        _revision++;
        _requestCancellation?.Cancel(); _requestCancellation?.Dispose(); _requestCancellation = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; CancelRequest(); _items.Clear(); _session = null;
    }
}
