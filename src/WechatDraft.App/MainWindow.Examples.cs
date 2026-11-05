using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WechatDraft.Core;
using Windows.Storage.Pickers;

namespace WechatDraft.App;

public sealed partial class MainWindow
{
    private static string BundledExamplesPath => Path.Combine(AppContext.BaseDirectory, "config", "blessing_examples.json");
    private string ConfiguredExamplesPath => string.IsNullOrWhiteSpace(_settings.ExamplesPath) ? BundledExamplesPath : _settings.ExamplesPath;

    private void ShowExamples() => ShowExamples(null);

    private void ShowExamples(string? message, bool success = true)
    {
        SelectNavigation(nameof(ShowExamples));
        _homeVisible = false;
        var status = new InfoBar { IsOpen = message is not null, Message = message ?? "",
            Severity = success ? InfoBarSeverity.Success : InfoBarSeverity.Error };
        var search = Ui.Input("搜索范例", "", "按正文、ID、场景、关系或语气查找");
        var enabledFilter = Ui.Combo("状态", ["全部", "已启用", "已停用"], "全部");
        var count = Ui.Text("", 12, "859189");
        var cards = Ui.Stack(12);

        void Render()
        {
            cards.Children.Clear();
            var keyword = search.Text.Trim();
            var filter = (string)enabledFilter.SelectedItem;
            var matches = _examples.Library.Examples.Where(example =>
                (filter == "全部" || (filter == "已启用") == example.Enabled) &&
                (keyword.Length == 0 || string.Join('\n', example.Id, example.Text, string.Join('、', example.Scenes),
                    string.Join('、', example.RelationshipTypes.Select(RelationshipLabel)), ToneLabel(example.Tone))
                    .Contains(keyword, StringComparison.OrdinalIgnoreCase))).ToList();
            count.Text = $"显示 {matches.Count} / {_examples.Library.Examples.Count} 条范例";
            if (matches.Count == 0)
            {
                cards.Children.Add(Ui.Card(Ui.Stack(8,
                    Ui.Text(_examples.Library.Examples.Count == 0 ? "范例库为空" : "没有匹配的范例", 18, bold: true),
                    Ui.Text(_examples.Library.Examples.Count == 0 ? "点击“新增范例”创建第一条，或导入 JSON 文件。" : "请更换搜索词或状态筛选。", 13, "839086"))));
                return;
            }
            foreach (var example in matches)
            {
                var text = Ui.Text(example.Text, 16); text.IsTextSelectionEnabled = true;
                var card = Ui.Stack(12,
                    Ui.Row(10, Ui.Badge(string.Join(" / ", example.Scenes)),
                        Ui.Badge(example.Enabled ? "已启用" : "已停用", example.Enabled ? "197861" : "778278", example.Enabled ? "E7F3ED" : "F0F3F0"),
                        Ui.Text(ToneLabel(example.Tone), 12, "829087")),
                    text,
                    Ui.Text(string.Join("、", example.RelationshipTypes.Select(RelationshipLabel)) + " · " + example.Id, 11, "89958D"),
                    Ui.Row(8,
                        Ui.Button("编辑", () => _ = EditExampleAsync(example)),
                        Ui.Button("删除", async () =>
                        {
                            if (!await ConfirmAsync("删除这条范例？", "删除后会立即保存当前范例文件，已有生成历史不会改变。")) return;
                            try
                            {
                                var path = PrepareExampleSavePath();
                                _examples.Delete(path, example.Id);
                                PersistExamplesPath();
                                ShowExamples("已删除范例 " + example.Id + "。");
                            }
                            catch (Exception e) { ShowExamples("删除失败：" + e.Message, false); }
                        })));
                cards.Children.Add(Ui.Card(card));
            }
        }

        var source = Ui.Card(Ui.Stack(12,
            Ui.Text($"{_examples.Library.Examples.Count} 条范例 · {_examples.Library.LibraryVersion}", 17, bold: true),
            Ui.Text("当前文件：" + _examples.LoadedPath, 12, "7D8A82"),
            Ui.Text("保存位置：" + (PathsEqual(_examples.LoadedPath, BundledExamplesPath)
                ? "首次修改时创建数据目录副本，保留内置原文件"
                : "直接保存到当前自定义 JSON 文件"), 12, "7D8A82"),
            Ui.Row(8,
                Ui.Button("选择 JSON 文件", () => _ = PickExamplesAsync()),
                Ui.Button("校验并重新加载", () =>
                {
                    var reloaded = _examples.TryReload(ConfiguredExamplesPath);
                    ShowExamples(reloaded ? "范例已重新加载。" : null);
                }),
                Ui.Button("打开文件位置", () =>
                {
                    if (!File.Exists(_examples.LoadedPath)) { ShowExamples("当前范例文件不存在。", false); return; }
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "/select,\"" + _examples.LoadedPath + "\"") { UseShellExecute = true });
                }))));
        var content = Ui.Stack(18, Ui.Text("祝福范例", 28, bold: true),
            Ui.Text("范例为表达提供参考。支持搜索、新增、编辑、删除和启停；每次按场景和关系选取最多三条启用范例。", 13, "7C8B81"),
            source, status);
        if (!PathsEqual(ConfiguredExamplesPath, _examples.LoadedPath))
            content.Children.Add(Ui.Text("配置路径与当前有效文件不同，当前显示最近一次成功加载的版本：" + ConfiguredExamplesPath, 12, "8D7954"));
        if (_examples.LastWarning.Length > 0)
            content.Children.Add(new InfoBar { IsOpen = true, Severity = InfoBarSeverity.Warning, Message = _examples.LastWarning });
        content.Children.Add(Ui.Button("＋ 新增范例", () => _ = EditExampleAsync(null), true));
        content.Children.Add(search); content.Children.Add(enabledFilter);
        content.Children.Add(count); content.Children.Add(cards);
        search.TextChanged += (_, _) => Render();
        enabledFilter.SelectionChanged += (_, _) => Render();
        Render(); Present(content);
    }

    private async Task EditExampleAsync(BlessingExample? example)
    {
        var adding = example is null;
        var id = Ui.Input("范例 ID（保存后保持不变）", example?.Id ?? "custom_" + Guid.NewGuid().ToString("N"));
        id.IsReadOnly = true;
        var scenes = Ui.Input("适用场景（多个场景用顿号或逗号分隔）", string.Join("、", example?.Scenes ?? ["通用问候"]), "例如：春节、元旦");
        var tone = Ui.Combo("表达参考", ["自然温和", "正式得体", "亲切轻松"], ToneLabel(example?.Tone ?? "natural"));
        var text = Ui.Input("范例正文", example?.Text ?? "", "可使用 {称呼} 占位符；最多 300 个可见字符");
        text.AcceptsReturn = true; text.Height = 130; text.TextWrapping = TextWrapping.Wrap;
        var enabled = new CheckBox { Content = "启用这条范例（停用后不参与生成）", IsChecked = example?.Enabled ?? true };
        var relationships = DraftRules.Relationships.Select(relationship => new CheckBox
        { Content = RelationshipLabel(relationship), Tag = relationship, IsChecked = example?.RelationshipTypes.Contains(relationship) == true }).ToArray();
        if (adding) relationships.Single(x => (string)x.Tag == "unknown").IsChecked = true;
        var error = new InfoBar { IsOpen = false, Severity = InfoBarSeverity.Error };
        var form = Ui.Stack(14, id, scenes,
            Ui.Text("适用关系（至少选择一项）", 13, bold: true),
            Ui.Row(8, relationships.Take(4).Cast<UIElement>().ToArray()),
            Ui.Row(8, relationships.Skip(4).Cast<UIElement>().ToArray()),
            tone, text, enabled,
            Ui.Text("语气标签只用于阅读和检索，不会覆盖聊天内容与当次补充要求。正文只允许 {称呼} 占位符。", 12, "8B978F"), error);
        var dialog = new ContentDialog
        {
            Title = adding ? "新增祝福范例" : "编辑祝福范例",
            Content = new ScrollViewer { Content = form, MaxHeight = 570, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            PrimaryButtonText = "保存", CloseButtonText = "取消", XamlRoot = _root.XamlRoot
        };
        BlessingExample? candidate = null;
        dialog.PrimaryButtonClick += (_, args) =>
        {
            try
            {
                var selectedScenes = scenes.Text.Split([',', '，', '、', ';', '；', '\r', '\n'],
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToArray();
                var selectedRelationships = relationships.Where(x => x.IsChecked == true).Select(x => (string)x.Tag).ToArray();
                var selectedTone = (string)tone.SelectedItem == "正式得体" ? "formal" : (string)tone.SelectedItem == "亲切轻松" ? "warm" : "natural";
                candidate = new BlessingExample
                {
                    Id = id.Text.Trim(), Scenes = selectedScenes, RelationshipTypes = selectedRelationships,
                    Tone = selectedTone, Text = text.Text.Trim(), Enabled = enabled.IsChecked == true
                };
                _examples.ValidateChange(candidate, example?.Id);
            }
            catch (Exception e) { candidate = null; error.Message = e.Message; error.IsOpen = true; args.Cancel = true; }
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || candidate is null) return;
        try
        {
            var path = PrepareExampleSavePath();
            _examples.Upsert(path, candidate, example?.Id);
            PersistExamplesPath();
            ShowExamples(adding ? "已新增范例 " + candidate.Id + "。" : "已保存范例 " + candidate.Id + "。");
        }
        catch (Exception e) { ShowExamples("保存失败：" + e.Message, false); }
    }

    private string PrepareExampleSavePath()
    {
        if (!PathsEqual(_examples.LoadedPath, BundledExamplesPath)) return _examples.LoadedPath;
        var editable = Path.Combine(_dataDirectory, "examples", "blessing_examples.json");
        _examples.Save(editable, _examples.Library);
        PersistExamplesPath();
        return editable;
    }

    private void PersistExamplesPath()
    {
        if (string.Equals(_settings.ExamplesPath, _examples.LoadedPath, StringComparison.OrdinalIgnoreCase)) return;
        _settings.ExamplesPath = _examples.LoadedPath;
        _repository.SaveSettings(_settings);
    }

    private async Task PickExamplesAsync()
    {
        var picker = new FileOpenPicker(); picker.FileTypeFilter.Add(".json");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var file = await picker.PickSingleFileAsync(); if (file is null) return;
        if (_examples.TryReload(file.Path))
        {
            try { PersistExamplesPath(); }
            catch (Exception e) { ShowExamples("范例已加载，但保存文件路径失败：" + e.Message, false); return; }
        }
        ShowExamples();
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
}
