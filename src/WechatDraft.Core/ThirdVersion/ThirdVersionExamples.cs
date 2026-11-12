using System.Text;

namespace WechatDraft.Core.ThirdVersion;

/// <summary>提示词示例编辑页与真实生成共用的 UTF-8 文本文件。</summary>
public static class ThirdVersionExamples
{
    private static readonly UTF8Encoding Encoding = new(false, true);
    public const int MaximumBytes = 128 * 1024;

    public static string Load(string directory) => File.Exists(Path.Combine(directory, ThirdVersionExampleLibrary.FileName))
        ? ThirdVersionExampleLibrary.Render(ThirdVersionExampleLibrary.Load(directory).Examples) : LoadText(directory);

    internal static string LoadText(string directory)
    {
        var path = Path.Combine(directory, "examples.txt");
        if (new FileInfo(path).Length > MaximumBytes) throw new InvalidDataException("提示词示例不能超过 128 KiB。");
        var text = File.ReadAllText(path, Encoding);
        Validate(text);
        return text;
    }

    public static void Save(string directory, string text, string? expectedText)
    {
        Validate(text);
        var path = Path.Combine(directory, "examples.txt");
        void CheckUnchanged()
        {
            if (File.Exists(path) ? expectedText is null || File.ReadAllText(path, Encoding) != expectedText : expectedText is not null)
                throw new InvalidOperationException("示例文件已被其他操作修改，请先保留当前编辑，再重新加载后保存。");
        }
        CheckUnchanged();
        Directory.CreateDirectory(directory);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, text, Encoding);
            CheckUnchanged();
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void Validate(string text)
    {
        if (text.Trim().Length < 20 || text.Contains('\0'))
            throw new InvalidDataException("提示词示例至少需要 20 个字符，且不能包含空字符。");
        if (Encoding.GetByteCount(text) > MaximumBytes) throw new InvalidDataException("提示词示例不能超过 128 KiB。");
    }
}
