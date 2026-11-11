using System.Text.Json;
using System.Text.Json.Serialization;

namespace WechatDraft.Core;

[JsonConverter(typeof(VisionBoxConverter))]
public sealed record VisionBox
{
    public double X { get; init; }
    public double Y { get; init; }
    public double Width { get; init; }
    public double Height { get; init; }
    [JsonIgnore] public double Right => X + Width;
    [JsonIgnore] public double Bottom => Y + Height;
    public bool Contains(VisionBox other) => other.X >= X && other.Y >= Y && other.Right <= Right && other.Bottom <= Bottom;
    public bool Overlaps(VisionBox other) => X < other.Right && Right > other.X && Y < other.Bottom && Bottom > other.Y;
}

public sealed class VisionBoxConverter : JsonConverter<VisionBox>
{
    public override VisionBox Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var box = document.RootElement;
        if (box.ValueKind != JsonValueKind.Object || !box.TryGetProperty("left", out var left) || !box.TryGetProperty("top", out var top) ||
            !box.TryGetProperty("right", out var right) || !box.TryGetProperty("bottom", out var bottom))
            throw new JsonException("布局矩形必须提供 left/top/right/bottom 四个归一化坐标。");
        if (new[] { left, top, right, bottom }.Any(value => value.ValueKind != JsonValueKind.Number))
            throw new JsonException("布局边界坐标必须是数字。");
        return new() { X = left.GetDouble(), Y = top.GetDouble(), Width = right.GetDouble() - left.GetDouble(), Height = bottom.GetDouble() - top.GetDouble() };
    }
    public override void Write(Utf8JsonWriter writer, VisionBox value, JsonSerializerOptions options)
    {
        writer.WriteStartObject(); writer.WriteNumber("left", value.X); writer.WriteNumber("top", value.Y);
        writer.WriteNumber("right", value.Right); writer.WriteNumber("bottom", value.Bottom); writer.WriteEndObject();
    }
}

public sealed record VisionLayout
{
    public string SchemaVersion { get; init; } = "";
    public string Title { get; init; } = "";
    public string ConversationType { get; init; } = "unknown";
    public string Evidence { get; init; } = "";
    public bool Blocked { get; init; } = true;
    public bool Uncertain { get; init; } = true;
    public string DraftKind { get; init; } = "unknown";
    public bool SelectionInInput { get; init; }
    public VisionBox? Chat { get; init; }
    public VisionBox? Header { get; init; }
    public VisionBox? Messages { get; init; }
    public VisionBox? Input { get; init; }
    public VisionBox? Editor { get; init; }
    public VisionBox? Send { get; init; }
    // Local-only evidence, never accepted from the model's JSON.
    [JsonIgnore] public VisionBox? InputToolbar { get; init; }
}

public sealed record VisionAnchorHints
{
    public VisionBox TopToolbar { get; init; } = new();
    public VisionBox BottomToolbar { get; init; } = new();
    public double TopScore { get; init; }
    public double BottomScore { get; init; }
    public double Scale { get; init; }
}

public interface IWindowVisionModel
{
    Task<VisionLayout> LocateAsync(AppSettings settings, string apiKey, Capture capture, bool checkingSelection, CancellationToken cancellationToken,
        VisionLayout? reference = null, VisionAnchorHints? anchors = null);
}

public static class VisionRules
{
    public static VisionLayout Parse(string text)
    {
        text = text.Trim();
        if (text.StartsWith("```", StringComparison.Ordinal) && text.EndsWith("```", StringComparison.Ordinal) && text.IndexOf('\n') is var line && line >= 0)
            text = text[(line + 1)..^3].Trim();
        var layout = JsonSerializer.Deserialize<VisionLayout>(text, JsonDefaults.Options) ?? throw new InvalidDataException("视觉定位结果为空。");
        ValidateBoxes(layout);
        // Restrict the context crop to the space between independently located title/input.
        // Preserve the model's canvas and send boxes; compute a separate inner click region.
        if (layout.Messages is { } messages && layout.Input is { } input && layout.Header is { } header)
        {
            if (messages.Bottom > input.Y - 2)
                messages = messages with { Height = input.Y - messages.Y - 2 };
            if (header.Bottom + 2 > messages.Y)
                messages = messages with { Y = header.Bottom + 2, Height = messages.Bottom - header.Bottom - 2 };
            layout = layout with { Messages = messages };
        }
        Validate(layout);
        return layout;
    }

    public static void Validate(VisionLayout layout)
    {
        if (layout.SchemaVersion != "1.0" || layout.Blocked || layout.Uncertain || layout.ConversationType != "single")
            throw new InvalidDataException("视觉识别未能确认无遮挡的单聊，已停止操作。");
        if (layout.DraftKind == "rich")
            throw new InvalidDataException("微信输入框含图片、文件或其他非文字草稿，当前只支持普通文字，未继续操作。请自行处理附件后重试。");
        if (string.IsNullOrWhiteSpace(layout.Title) || layout.Title.Length > 80 || layout.Title.Any(char.IsControl) ||
            string.IsNullOrWhiteSpace(layout.Evidence) || layout.Evidence.Length > 300 || layout.DraftKind is not ("empty" or "text"))
            throw new InvalidDataException("视觉标题、单聊依据或纯文本草稿状态不明确，已停止操作。");
        ValidateBoxes(layout);
        var chat = layout.Chat!; var header = layout.Header!; var messages = layout.Messages!;
        var input = layout.Input!; var editor = layout.Editor!; var send = layout.Send!;
        var errors = new List<string>();
        if (!chat.Contains(header)) errors.Add("标题不在聊天面板内");
        if (!chat.Contains(messages)) errors.Add("消息区不在聊天面板内");
        if (!chat.Contains(input)) errors.Add("输入面板不在聊天面板内");
        if (!input.Contains(editor)) errors.Add("编辑区不在输入面板内");
        if (!input.Contains(send)) errors.Add("发送按钮不在输入面板内");
        if (header.Bottom > messages.Y) errors.Add("标题与消息区重叠");
        if (messages.Bottom > input.Y) errors.Add($"消息区与输入面板重叠（消息底={messages.Bottom:0.##}，输入顶={input.Y:0.##}）");
        // A one/two-character remark can legitimately be narrower than 40 normalized units.
        // Header still meets the common minimum box size, containment and pixel-change guards.
        if (messages.Width < 150 || messages.Height < 80)
            errors.Add($"消息区域过小（宽={messages.Width:0.##}，高={messages.Height:0.##}）");
        if (editor.Width < 100 || editor.Height < 25)
            errors.Add($"编辑区域过小（宽={editor.Width:0.##}，高={editor.Height:0.##}）");
        if (layout.InputToolbar is { } toolbar && !Expanded(input, 8).Contains(toolbar))
            errors.Add("本地识别的工具栏不在输入面板内");
        if (errors.Count == 0 && FindClickRegion(editor, send, layout.InputToolbar) is null)
            errors.Add("无法在编辑区内找到避开工具栏和发送禁区的点击区域");
        if (errors.Count > 0) throw new InvalidDataException("视觉定位区域关系不合理：" + string.Join("；", errors) + "。未操作微信。");
    }

    private static void ValidateBoxes(VisionLayout layout)
    {
        foreach (var (name, box) in new[] { ("chat", layout.Chat), ("header", layout.Header), ("messages", layout.Messages),
            ("input", layout.Input), ("editor", layout.Editor), ("send", layout.Send) })
            if (box is null || !double.IsFinite(box.X) || !double.IsFinite(box.Y) || !double.IsFinite(box.Width) || !double.IsFinite(box.Height) ||
                box.X < 0 || box.Y < 0 || box.Width < 8 || box.Height < 8 || box.Right > 1000 || box.Bottom > 1000)
                throw new InvalidDataException($"视觉定位 {name} 坐标无效或超出截图（x={box?.X}, y={box?.Y}, width={box?.Width}, height={box?.Height}），未操作微信。");
        if (layout.InputToolbar is { } toolbar && !ValidBox(toolbar))
            throw new InvalidDataException("本地工具栏坐标无效，未操作微信。");
    }

    private static bool ValidBox(VisionBox box) => double.IsFinite(box.X) && double.IsFinite(box.Y) &&
        double.IsFinite(box.Width) && double.IsFinite(box.Height) && box.X >= 0 && box.Y >= 0 &&
        box.Width >= 8 && box.Height >= 8 && box.Right <= 1000 && box.Bottom <= 1000;

    private static VisionBox Expanded(VisionBox box, double margin) => new()
    { X = box.X - margin, Y = box.Y - margin, Width = box.Width + margin * 2, Height = box.Height + margin * 2 };

    public static void ValidateAnchors(VisionAnchorHints anchors)
    {
        if (!ValidBox(anchors.TopToolbar) || !ValidBox(anchors.BottomToolbar) ||
            !double.IsFinite(anchors.TopScore) || !double.IsFinite(anchors.BottomScore) ||
            anchors.TopScore is < 0.80 or > 1.001 || anchors.BottomScore is < 0.80 or > 1.001 ||
            !double.IsFinite(anchors.Scale) || anchors.Scale is < 0.25 or > 4 ||
            anchors.TopToolbar.Bottom >= anchors.BottomToolbar.Y)
            throw new InvalidDataException("本地工具栏匹配结果不可靠，未操作微信。");
    }

    public static VisionLayout ApplyAnchors(VisionLayout layout, VisionAnchorHints anchors)
    {
        ValidateAnchors(anchors); Validate(layout);
        var header = layout.Header!; var input = layout.Input!;
        var topY = anchors.TopToolbar.Y + anchors.TopToolbar.Height / 2;
        if (topY < header.Y - 30 || topY > header.Bottom + 30 ||
            !Expanded(input, 8).Contains(anchors.BottomToolbar))
            throw new InvalidDataException("本地工具栏与视觉模型识别的布局不一致，已停止操作。");
        var anchored = layout with { InputToolbar = anchors.BottomToolbar };
        Validate(anchored);
        return anchored;
    }

    public static VisionBox ClickRegion(VisionLayout layout)
    {
        Validate(layout);
        return FindClickRegion(layout.Editor!, layout.Send!, layout.InputToolbar)!;
    }

    public static (double X, double Y) InputPoint(VisionLayout layout)
    {
        var region = ClickRegion(layout);
        return (region.X + region.Width / 2, region.Y + region.Height / 2);
    }

    private static VisionBox? FindClickRegion(VisionBox editor, VisionBox send, VisionBox? toolbar)
    {
        const double margin = 8;
        var inner = new VisionBox { X = editor.X + margin, Y = editor.Y + margin,
            Width = editor.Width - margin * 2, Height = editor.Height - margin * 2 };
        var forbidden = new[] { send, toolbar }.Where(box => box is not null).Select(box => Expanded(box!, margin)).ToArray();
        var preferred = (X: editor.X + Math.Min(editor.Width / 3, 60),
            Y: editor.Y + Math.Min(editor.Height / 2, 25));
        VisionBox? Fit(VisionBox area)
        {
            if (area.Width < 16 || area.Height < 8) return null;
            var width = Math.Min(80, area.Width); var height = Math.Min(40, area.Height);
            var x = Math.Clamp(preferred.X, area.X + width / 2, area.Right - width / 2);
            var y = Math.Clamp(preferred.Y, area.Y + height / 2, area.Bottom - height / 2);
            return new() { X = x - width / 2, Y = y - height / 2, Width = width, Height = height };
        }
        var preferredRegion = Fit(inner);
        if (preferredRegion is not null && forbidden.All(box => !preferredRegion.Overlaps(box))) return preferredRegion;
        // Excluding a button never expands or moves the model's editable canvas.
        var candidates = new List<VisionBox> { inner };
        foreach (var obstacle in forbidden)
        {
            candidates = candidates.SelectMany(area => !area.Overlaps(obstacle) ? new[] { area } : new[]
            {
                area with { Width = Math.Min(area.Right, obstacle.X) - area.X },
                area with { Height = Math.Min(area.Bottom, obstacle.Y) - area.Y },
                area with { X = Math.Max(area.X, obstacle.Right), Width = area.Right - Math.Max(area.X, obstacle.Right) },
                area with { Y = Math.Max(area.Y, obstacle.Bottom), Height = area.Bottom - Math.Max(area.Y, obstacle.Bottom) }
            }).Where(area => area.Width >= 16 && area.Height >= 8).ToList();
        }
        return candidates.OrderByDescending(area => area.Width * area.Height)
            .Select(Fit).FirstOrDefault(region => region is not null && inner.Contains(region) && forbidden.All(box => !region.Overlaps(box)));
    }

    public static bool SameTarget(VisionLayout before, VisionLayout after)
    {
        try { Validate(before); Validate(after); }
        catch (InvalidDataException) { return false; }
        var point = InputPoint(before);
        var click = ClickRegion(after);
        if (before.InputToolbar is not null && after.InputToolbar is null) return false;
        // Rechecks keep the original point, even if a fresh candidate would allow another click.
        return before.Title.Trim() == after.Title.Trim() && before.Header!.Overlaps(after.Header!) &&
        point.X > click.X && point.X < click.Right && point.Y > click.Y && point.Y < click.Bottom &&
        new[] { (before.Input!, after.Input!), (before.Send!, after.Send!) }
            .All(pair => Math.Abs(pair.Item1.X - pair.Item2.X) <= 15 && Math.Abs(pair.Item1.Y - pair.Item2.Y) <= 15 &&
                Math.Abs(pair.Item1.Width - pair.Item2.Width) <= 20 && Math.Abs(pair.Item1.Height - pair.Item2.Height) <= 20);
    }

    public static (double X, double Y) ScrollPoint(VisionLayout layout, int pixelWidth, uint dpi)
    {
        Validate(layout);
        if (pixelWidth <= 0 || dpi is < 48 or > 768) throw new InvalidDataException("无法确认聊天区滚动位置。");
        var messages = layout.Messages!;
        // The reference uses a 23 logical-pixel gutter; keep it relative to the confirmed message pane.
        var inset = 23d * dpi / 96 * 1000 / pixelWidth;
        if (messages.Width <= inset * 2) throw new InvalidDataException("聊天区过窄，未滚动。");
        return (messages.Right - inset, messages.Y + messages.Height / 2);
    }

    public static string ReadDraft(string? copiedText, VisionLayout selectionCheck)
    {
        Validate(selectionCheck);
        if (copiedText is not null)
        {
            if (copiedText.Length > 65536 || copiedText.Contains('\uFFFC') || copiedText.Contains('\0') || !selectionCheck.SelectionInInput ||
                (copiedText.Length > 0 && selectionCheck.DraftKind != "text"))
                throw new InvalidDataException("无法确认复制内容为完整的纯文本草稿，未写入。");
            return copiedText;
        }
        // A failed copy alone is never evidence of an empty input.
        if (selectionCheck.DraftKind != "empty" || !selectionCheck.SelectionInInput)
            throw new InvalidDataException("未复制到可核对的文字，也未确认输入框已聚焦并为空。请检查是否含图片、文件或输入区定位不正确；未写入。");
        return "";
    }
}

public sealed class UiAutomationUnavailableException(string message) : InvalidOperationException(message);

public sealed class FallbackConversationAdapter(IConversationAdapter primary, Func<IConversationAdapter> fallbackFactory) : IConversationAdapter, IDisposable
{
    private IConversationAdapter _active = primary;
    public string AutomationMethod => _active.AutomationMethod;
    public string FallbackReason { get; private set; } = "";
    public string CleanupWarning => _active.CleanupWarning;
    public string StateChangeReason => _active.StateChangeReason;
    public int LayoutApiCallCount => _active.LayoutApiCallCount;
    public bool CanAutoAppend => _active.CanAutoAppend;
    public string AutoAppendUnavailableReason => _active.AutoAppendUnavailableReason;
    public bool CanCollectMoreContext => _active.CanCollectMoreContext;
    public async Task<ConversationSnapshot> BeginAsync(CancellationToken ct)
    {
        try { return await _active.BeginAsync(ct); }
        catch (UiAutomationUnavailableException e)
        {
            ct.ThrowIfCancellationRequested();
            if (_active is IDisposable disposable) disposable.Dispose();
            FallbackReason = e.Message;
            _active = fallbackFactory();
            return await _active.BeginAsync(ct);
        }
    }
    public Task<Capture> CaptureAsync(ConversationSnapshot s, int i, CancellationToken ct) => _active.CaptureAsync(s, i, ct);
    public Task<bool> ScrollUpAsync(ConversationSnapshot s, CancellationToken ct) => _active.ScrollUpAsync(s, ct);
    public Task<bool> IsUnchangedAsync(ConversationSnapshot s, CancellationToken ct) => _active.IsUnchangedAsync(s, ct);
    public Task<bool> WaitForAppendReadyAsync(ConversationSnapshot s, CancellationToken ct) => _active.WaitForAppendReadyAsync(s, ct);
    public Task<FillResult> AppendAsync(ConversationSnapshot s, string text, CancellationToken ct) => _active.AppendAsync(s, text, ct);
    public Task RestoreAsync(ConversationSnapshot s) => _active.RestoreAsync(s);
    public void Dispose() { if (_active is IDisposable disposable) disposable.Dispose(); }
}

public static class VisionDraftAppender
{
    public static async Task<FillResult> AppendAsync(string original, string greeting,
        Func<CancellationToken, Task<string>> readDraft, Func<string, CancellationToken, Task> pasteSuffix, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (await readDraft(ct) != original) return new("待手动处理", "原有草稿发生变化，未输入。");
        ct.ThrowIfCancellationRequested();
        await pasteSuffix(greeting, ct);
        ct.ThrowIfCancellationRequested();
        var actual = await readDraft(ct);
        return DraftRules.NormalizeNewlines(actual) == DraftRules.NormalizeNewlines(DraftRules.Append(original, greeting))
            ? new("已预填", "纯视觉追加已读回核对，请检查后手动发送。", true)
            : new("预填结果待确认", "未读回完整预期草稿，请检查微信；未重复写入。");
    }
}
