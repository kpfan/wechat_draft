using WechatDraft.Core;

internal static class ExampleChecks
{
    public static IEnumerable<(string Name, Func<Task> Run)> Cases(string sourcePath)
    {
        yield return ("Example editor adds, persists and selects a new example", () =>
        {
            var (service, target) = Load(sourcePath);
            var original = File.ReadAllBytes(sourcePath);
            var example = new BlessingExample
            {
                Id = "custom_qixi_friend", Scenes = ["七夕"], RelationshipTypes = ["friend"],
                Tone = "warm", Text = "{称呼}，七夕快乐！愿你常有轻松欢喜的日常。", Enabled = true
            };
            service.ValidateChange(example);
            service.Upsert(target, example);
            var reloaded = new ExampleService(); reloaded.Load(target);
            Equal(example.Id, reloaded.Library.Examples.Single(x => x.Id == example.Id).Id);
            True(reloaded.Select("七夕", "friend").Any(x => x.Id == example.Id), "new example was not selected");
            True(original.SequenceEqual(File.ReadAllBytes(sourcePath)), "bundled source was modified");
            return Task.CompletedTask;
        });
        yield return ("Example editor preserves identity and excludes disabled examples", () =>
        {
            var (service, target) = Load(sourcePath);
            var before = service.Library.Examples[0];
            var count = service.Library.Examples.Count;
            var updated = before with { Text = "这是编辑后的合成测试范例。", Enabled = false };
            service.Upsert(target, updated, before.Id);
            Equal(count, service.Library.Examples.Count);
            var reloaded = new ExampleService(); reloaded.Load(target);
            var saved = reloaded.Library.Examples.Single(x => x.Id == before.Id);
            Equal(updated.Text, saved.Text); Equal(false, saved.Enabled);
            True(!reloaded.Select(before.Scenes[0], before.RelationshipTypes[0]).Any(x => x.Id == before.Id), "disabled example was selected");
            return Task.CompletedTask;
        });
        yield return ("Example editor deletes records and reloads an empty library", () =>
        {
            var (service, target) = Load(sourcePath);
            service.Save(target, service.Library);
            foreach (var id in service.Library.Examples.Select(x => x.Id).ToArray()) service.Delete(target, id);
            Equal(0, service.Library.Examples.Count);
            var reloaded = new ExampleService(); reloaded.Load(target);
            Equal(0, reloaded.Library.Examples.Count); Equal(0, reloaded.Select("春节", "friend").Count);
            return Task.CompletedTask;
        });
        yield return ("Invalid example changes retain the active library and saved file", () =>
        {
            var (service, target) = Load(sourcePath);
            service.Save(target, service.Library);
            var version = service.Version; var bytes = File.ReadAllBytes(target); var count = service.Library.Examples.Count;
            var existing = service.Library.Examples[0];
            ExpectInvalid(() => service.ValidateChange(existing with { Text = "非法 {未知} 占位符" }, existing.Id));
            ExpectInvalid(() => service.Upsert(target, existing with { Text = "重复 ID" }));
            ExpectInvalid(() => service.Upsert(target, existing with { Text = "非法 {未知} 占位符" }, existing.Id));
            Equal(version, service.Version); Equal(count, service.Library.Examples.Count);
            True(bytes.SequenceEqual(File.ReadAllBytes(target)), "invalid mutation changed the saved file");
            return Task.CompletedTask;
        });
    }

    private static (ExampleService Service, string Target) Load(string sourcePath)
    {
        var service = new ExampleService(); service.Load(sourcePath);
        var folder = Path.Combine(Path.GetTempPath(), "WechatDraft-examples-" + Guid.NewGuid().ToString("N"));
        return (service, Path.Combine(folder, "blessing_examples.json"));
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!Equals(expected, actual)) throw new Exception($"Expected {expected}; actual {actual}");
    }

    private static void True(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static void ExpectInvalid(Action action)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new Exception("Expected example validation exception");
    }
}
