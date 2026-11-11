using System.Text;

namespace WechatDraft.Core.ThirdVersion;

/// <summary>每次操作重新读取配置；缺失、过大或空白配置明确报错，不静默使用旧版。</summary>
internal sealed record ThirdVersionPrompts(string Extract, string Generate, string Revise, string Examples)
{
    internal static ThirdVersionPrompts Load(string directory)
    {
        string Read(string name, int minimum)
        {
            var path = Path.Combine(directory, name);
            if (!File.Exists(path)) throw new InvalidDataException($"第三版配置缺失：{name}。");
            var info = new FileInfo(path);
            if (info.Length is <= 0 or > 128 * 1024) throw new InvalidDataException($"第三版配置大小无效：{name}（上限 128 KiB）。");
            string text;
            try { text = File.ReadAllText(path, new UTF8Encoding(false, true)).Trim(); }
            catch (DecoderFallbackException) { throw new InvalidDataException($"第三版配置必须是 UTF-8：{name}。"); }
            if (text.Length < minimum || text.Contains('\0')) throw new InvalidDataException($"第三版配置内容无效或过短：{name}。");
            return text;
        }
        var result = new ThirdVersionPrompts(Read("extract.txt", 100), Read("generate.txt", 100), Read("revise.txt", 100), Read("examples.txt", 20));
        if (!result.Extract.Contains("direct") || !result.Extract.Contains("inferred") || !result.Extract.Contains("unknown"))
            throw new InvalidDataException("第三版提取配置缺少 direct / inferred / unknown 输出约定。");
        if (!result.Generate.Contains("candidates") || !result.Revise.Contains("greeting") || !result.Revise.Contains("note"))
            throw new InvalidDataException("第三版配置缺少 candidates 或 greeting / note 输出约定。");
        return result;
    }
}
