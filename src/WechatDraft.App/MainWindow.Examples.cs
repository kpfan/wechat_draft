using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WechatDraft.Core.ThirdVersion;

namespace WechatDraft.App;

public sealed partial class MainWindow
{
    private static string PromptExamplesDirectory => Path.Combine(AppContext.BaseDirectory, "config", "third-version");

    private void ShowExamples() => ShowExamples(null);

    private void ShowExamples(string? message, bool success = true)
    {
        SelectNavigation(nameof(ShowExamples));
        _homeVisible = false;
        var status = new InfoBar { IsOpen = message is not null, Message = message ?? "",
            Severity = success ? InfoBarSeverity.Success : InfoBarSeverity.Warning };
        PromptExampleLibrary library;
        try { library = ThirdVersionExampleLibrary.Load(PromptExamplesDirectory); }
        catch (Exception e)
        {
            Present(Ui.Stack(18, Ui.Text("提示词示例", 28, bold: true),
                Ui.Text("加载失败：" + e.Message, 14), Ui.Button("重新加载", ShowExamples),
                Ui.Button("打开提示词目录", () => _ = OpenThirdPromptsAsync())));
            return;
        }
        var search = Ui.Input("搜索示例", "", "按输入、输出或 ID 查找");
        var filter = Ui.Combo("状态", ["全部", "已启用", "已停用"], "全部");
        var count = Ui.Text("", 12, "859189");
        var cards = Ui.Stack(12);
        void SaveItems(IReadOnlyList<PromptExample> items, string notice)
        {
            if (IsTaskBusy) { status.Message = "当前任务正在进行，请结束后修改示例。"; status.IsOpen = true; status.Severity = InfoBarSeverity.Warning; return; }
            try
            {
                ThirdVersionExampleLibrary.Save(PromptExamplesDirectory, items, library.Version);
                ShowExamples(notice);
            }
            catch (Exception e) { status.Message = "保存失败：" + e.Message; status.IsOpen = true; status.Severity = InfoBarSeverity.Warning; }
        }
        void Render()
        {
            cards.Children.Clear();
            var keyword = search.Text.Trim();
            var matches = library.Examples.Where(x =>
                ((string)filter.SelectedItem == "全部" || ((string)filter.SelectedItem == "已启用") == x.Enabled) &&
                (keyword.Length == 0 || (x.Input + "\n" + x.Output + "\n" + x.Id).Contains(keyword, StringComparison.OrdinalIgnoreCase))).ToArray();
            count.Text = $"显示 {matches.Length} / {library.Examples.Count} 条 · 全部已启用 {library.Examples.Count(x => x.Enabled)} 条参与生成";
            foreach (var example in matches)
            {
                var input = Ui.Text("输入：" + example.Input, 13, "7C8B81"); input.IsTextSelectionEnabled = true;
                var output = Ui.Text(example.Output, 16); output.IsTextSelectionEnabled = true;
                cards.Children.Add(Ui.Card(Ui.Stack(12,
                    Ui.Row(8, Ui.Badge(example.Enabled ? "已启用" : "已停用"), Ui.Text(example.Id, 11, "89958D")), input, output,
                    Ui.Row(8,
                        Ui.Button("编辑", () => _ = EditPromptExampleAsync(library, example)),
                        Ui.Button(example.Enabled ? "停用" : "启用", () => SaveItems(library.Examples.Select(x => x.Id == example.Id ? x with { Enabled = !x.Enabled } : x).ToArray(), "已更新示例状态，下次生成生效。")),
                        Ui.Button("删除", async () =>
                        {
                            if (await ConfirmAsync("删除这条提示词示例？", "只删除当前这一条。保存前会备份旧列表，也可使用停用保留它。"))
                                SaveItems(library.Examples.Where(x => x.Id != example.Id).ToArray(), "已删除当前示例，旧列表已备份。");
                        })))));
            }
            if (matches.Length == 0) cards.Children.Add(Ui.Text("没有匹配的示例，可新增或更换筛选。", 14, "7C8B81"));
        }
        var content = Ui.Stack(18,
            Ui.Text("提示词示例", 28, bold: true),
            Ui.Text("逐条新增、编辑、删除和启停。首次生成与重新生成时，全部已启用示例会附在第三版提示词后作为风格参考，不按场景或关系筛选。", 13, "7C8B81"),
            Ui.Card(Ui.Stack(10,
                Ui.Text("每次保存仅修改选中的示例，保存前自动备份旧列表。已有候选不变；多轮修改仍只使用该候选及其修改历史。示例中的人物、经历和节日不可直接套用。", 13, "7D8A82"),
                Ui.Text("列表文件：" + Path.Combine(PromptExamplesDirectory, ThirdVersionExampleLibrary.FileName), 12, "7D8A82"),
                Ui.Text("备份位于同目录 example-backups 文件夹；列表独立于内置文本，不会被常规发布覆盖。演示模式仍使用原内置范例。", 12, "7D8A82"),
                Ui.Row(8, Ui.Button("重新加载", ShowExamples), Ui.Button("打开提示词目录", () => _ = OpenThirdPromptsAsync())))),
            status, Ui.Button("＋ 新增示例", () => _ = EditPromptExampleAsync(library, null), true),
            search, filter, count, cards);
        search.TextChanged += (_, _) => Render(); filter.SelectionChanged += (_, _) => Render();
        Render(); Present(content);
    }

    private async Task EditPromptExampleAsync(PromptExampleLibrary library, PromptExample? example)
    {
        if (IsTaskBusy) { Notify("请等待当前任务结束后修改示例。", InfoBarSeverity.Warning); return; }
        var input = Ui.Input("示例输入", example?.Input ?? "", "例如：称呼王老师，场景春节，语气正式");
        input.AcceptsReturn = true; input.TextWrapping = TextWrapping.Wrap; input.Height = 90; input.MaxLength = 2000;
        var output = Ui.Input("示例输出", example?.Output ?? "", "填写一条完整祝福，模型只参考表达风格");
        output.AcceptsReturn = true; output.TextWrapping = TextWrapping.Wrap; output.Height = 200; output.MaxLength = 8000;
        var enabled = new CheckBox { Content = "启用这条示例（停用后不附加到生成提示词）", IsChecked = example?.Enabled ?? true };
        var error = new InfoBar { IsOpen = false, Severity = InfoBarSeverity.Error };
        var dialog = new ContentDialog
        {
            Title = example is null ? "新增提示词示例" : "编辑提示词示例",
            Content = new ScrollViewer { Content = Ui.Stack(12, input, output, enabled,
                Ui.Text("仅保存这一条，其他示例不变。保存前自动备份旧列表。", 12, "7D8A82"), error), MaxHeight = 550 },
            PrimaryButtonText = "保存", CloseButtonText = "取消", XamlRoot = _root.XamlRoot
        };
        dialog.PrimaryButtonClick += (_, args) =>
        {
            try
            {
                if (IsTaskBusy) throw new InvalidOperationException("当前有任务正在进行，请结束后保存。");
                var candidate = new PromptExample(example?.Id ?? "custom_" + Guid.NewGuid().ToString("N"), input.Text.Trim(), output.Text.Trim(), enabled.IsChecked == true);
                var items = example is null ? library.Examples.Append(candidate).ToArray() : library.Examples.Select(x => x.Id == candidate.Id ? candidate : x).ToArray();
                ThirdVersionExampleLibrary.Save(PromptExamplesDirectory, items, library.Version);
            }
            catch (Exception e) { args.Cancel = true; error.Message = "保存失败：" + e.Message; error.IsOpen = true; }
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) ShowExamples("已保存当前示例。下次生成或重新生成生效。");
    }
}
