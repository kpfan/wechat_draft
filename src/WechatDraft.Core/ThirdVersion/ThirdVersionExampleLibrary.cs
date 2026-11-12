using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WechatDraft.Core.ThirdVersion;

public sealed record PromptExample(string Id, string Input, string Output, bool Enabled = true);
public sealed record PromptExampleLibrary(IReadOnlyList<PromptExample> Examples, string? Version);

/// <summary>逐条管理，生成使用全部启用示例；用户列表不属于发布内容。</summary>
public static class ThirdVersionExampleLibrary
{
    public const string FileName = "examples-library.json";
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private const string Header = "风格示例仅用于表达参考，示例姓名、身份、生肖和节日不得套用到当前输入；实际仍须生成三个不同候选。";

    public static PromptExampleLibrary Load(string directory)
    {
        var path = Path.Combine(directory, FileName);
        if (!File.Exists(path))
        {
            var text = ThirdVersionExamples.LoadText(directory);
            var matches = Regex.Matches(text, @"(?ms)^输入[：:]\s*(.*?)\r?\n输出[：:]\s*(.*?)(?=\r?\n\s*输入[：:]|\z)");
            var items = matches.Count > 0
                ? matches.Select((m, i) => new PromptExample("import_" + i, m.Groups[1].Value.Trim(), m.Groups[2].Value.Trim())).ToArray()
                : [new PromptExample("import_text", "自由风格参考", text.Trim())];
            Validate(items);
            return new(items, "text:" + Convert.ToHexString(SHA256.HashData(Utf8.GetBytes(text))));
        }
        if (new FileInfo(path).Length > ThirdVersionExamples.MaximumBytes) throw new InvalidDataException("提示词示例列表不能超过 128 KiB。");
        var raw = File.ReadAllText(path, Utf8);
        var examples = JsonSerializer.Deserialize<PromptExample[]>(raw, Options) ?? throw new InvalidDataException("提示词示例列表无效。");
        Validate(examples);
        return new(examples, raw);
    }

    public static string Render(IReadOnlyList<PromptExample> examples)
    {
        Validate(examples);
        var enabled = examples.Where(x => x.Enabled).ToArray();
        return Header + (enabled.Length == 0 ? "\n当前没有启用的风格示例，请按生成规则独立撰写。" : "\n" + string.Join("\n\n", enabled.Select(x => $"输入：{x.Input}\n输出：{x.Output}")));
    }

    public static PromptExampleLibrary Save(string directory, IReadOnlyList<PromptExample> examples, string? expectedVersion)
    {
        Validate(examples);
        var raw = JsonSerializer.Serialize(examples, Options);
        if (Utf8.GetByteCount(raw) > ThirdVersionExamples.MaximumBytes) throw new InvalidDataException("提示词示例列表不能超过 128 KiB。");
        var path = Path.Combine(directory, FileName);
        void Check()
        {
            var current = File.Exists(path) ? File.ReadAllText(path, Utf8) :
                "text:" + Convert.ToHexString(SHA256.HashData(Utf8.GetBytes(ThirdVersionExamples.LoadText(directory))));
            if (current != expectedVersion)
                throw new InvalidOperationException("示例列表已被其他操作修改，请重新加载后再编辑。");
        }
        Directory.CreateDirectory(directory);
        Check();
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, raw, Utf8);
            Check();
            var backupDirectory = Path.Combine(directory, "example-backups");
            Directory.CreateDirectory(backupDirectory);
            var source = File.Exists(path) ? path : Path.Combine(directory, "examples.txt");
            if (File.Exists(source)) File.Copy(source, Path.Combine(backupDirectory, DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff") + "-" + Guid.NewGuid().ToString("N") + Path.GetExtension(source)));
            Check();
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
        return new(examples.ToArray(), raw);
    }

    private static void Validate(IReadOnlyList<PromptExample> examples)
    {
        if (examples.Count > 200) throw new InvalidDataException("最多管理 200 条提示词示例。");
        var ids = new HashSet<string>();
        foreach (var example in examples)
            if (example is null || string.IsNullOrWhiteSpace(example.Id) || !ids.Add(example.Id) || string.IsNullOrWhiteSpace(example.Input) ||
                string.IsNullOrWhiteSpace(example.Output) || example.Input.Length > 2000 || example.Output.Length > 8000 ||
                example.Input.Contains('\0') || example.Output.Contains('\0'))
                throw new InvalidDataException("示例无效：输入与输出不能为空，输入最多 2000 字、输出最多 8000 字，ID 不可重复。");
    }
}
