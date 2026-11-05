using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace WechatDraft.Core;

public sealed class ExampleService
{
    private static readonly JsonSerializerOptions SaveOptions = new(JsonDefaults.Options)
    { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public ExampleLibrary Library { get; private set; } = new();
    public string Version { get; private set; } = "";
    public string LastWarning { get; private set; } = "";
    public string LoadedPath { get; private set; } = "";

    public ExampleService Snapshot() => new()
    {
        Library = Library with { Examples = Library.Examples.Select(example => example with
            { Scenes = example.Scenes.ToArray(), RelationshipTypes = example.RelationshipTypes.ToArray() }).ToList() },
        Version = Version, LoadedPath = LoadedPath, LastWarning = LastWarning
    };

    public void Load(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length > 1024 * 1024) throw new InvalidDataException("范例文件不能超过 1 MiB。");
        var library = JsonSerializer.Deserialize<ExampleLibrary>(bytes, JsonDefaults.Options)
            ?? throw new InvalidDataException("范例库为空。");
        ValidateLibrary(library);
        Commit(path, library, bytes);
    }

    public void Save(string path, ExampleLibrary library)
    {
        ValidateLibrary(library);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(library, SaveOptions);
        if (bytes.Length > 1024 * 1024) throw new InvalidDataException("范例文件不能超过 1 MiB。");
        WriteAtomic(path, bytes);
        Commit(path, library, bytes);
    }

    public void ValidateChange(BlessingExample example, string? originalId = null) =>
        ValidateLibrary(ChangedLibrary(example, originalId));

    public void Upsert(string path, BlessingExample example, string? originalId = null) =>
        Save(path, ChangedLibrary(example, originalId));

    public void Delete(string path, string id)
    {
        if (Library.Examples.All(x => x.Id != id))
            throw new InvalidDataException("范例已不存在，请刷新页面。");
        Save(path, Library with { Examples = Library.Examples.Where(x => x.Id != id).ToList() });
    }

    public bool TryReload(string path)
    {
        try { Load(path); return true; }
        catch (Exception e) when (e is IOException or JsonException or InvalidDataException or UnauthorizedAccessException)
        { LastWarning = "范例重载失败，保留最近有效版本：" + e.Message; return false; }
    }

    public IReadOnlyList<BlessingExample> Select(string scene, string relationship) =>
        Library.Examples.Where(x => x.Enabled && x.Scenes.Contains(scene))
            .OrderByDescending(x => (x.RelationshipTypes.Contains(relationship) ? 4 : 0) +
                (x.RelationshipTypes.Contains("unknown") ? 1 : 0))
            .Take(3).ToArray();

    private ExampleLibrary ChangedLibrary(BlessingExample example, string? originalId)
    {
        var items = Library.Examples.ToList();
        if (originalId is null)
        {
            if (items.Any(x => x.Id == example.Id)) throw new InvalidDataException("范例 ID 已存在，请使用其他 ID。");
            items.Add(example);
        }
        else
        {
            var index = items.FindIndex(x => x.Id == originalId);
            if (index < 0) throw new InvalidDataException("范例已不存在，请刷新页面。");
            if (items.Where((_, i) => i != index).Any(x => x.Id == example.Id))
                throw new InvalidDataException("范例 ID 已存在，请使用其他 ID。");
            items[index] = example;
        }
        return Library with { Examples = items };
    }

    private static void ValidateLibrary(ExampleLibrary library)
    {
        if (library.SchemaVersion != "1.0") throw new InvalidDataException("范例库版本无效。");
        var ids = new HashSet<string>();
        foreach (var item in library.Examples)
        {
            if (string.IsNullOrWhiteSpace(item.Id) || !ids.Add(item.Id) || item.Scenes.Length == 0 ||
                item.RelationshipTypes.Length == 0 || !item.RelationshipTypes.All(DraftRules.Relationships.Contains) ||
                item.Tone is not ("natural" or "formal" or "warm") || string.IsNullOrWhiteSpace(item.Text) ||
                DraftRules.VisibleLength(item.Text) > 300 || item.Text.Replace("{称呼}", "").Contains('{'))
                throw new InvalidDataException($"范例 {item.Id} 无效，请检查 ID、标签和占位符。");
        }
    }

    private void Commit(string path, ExampleLibrary library, byte[] bytes)
    {
        Library = library;
        LoadedPath = Path.GetFullPath(path);
        Version = library.LibraryVersion + "/" + Convert.ToHexString(SHA256.HashData(bytes))[..12];
        LastWarning = "";
    }

    private static void WriteAtomic(string path, byte[] bytes)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath) ?? throw new InvalidDataException("范例保存路径无效。");
        Directory.CreateDirectory(directory);
        var temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, fullPath, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
