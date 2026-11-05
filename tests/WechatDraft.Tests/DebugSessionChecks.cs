using System.Text.Json;
using WechatDraft.Core;

internal static class DebugSessionChecks
{
    public static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("Disabled and production diagnostics never create intermediate files", () =>
        {
            var root = NewRoot(); var previous = DebugSession.Current;
            using (var session = DebugSession.Begin(false, root, "disabled"))
            {
                session.WriteJson("payload", new { token = "credential" }); session.Event("failure");
                session.WriteText("text", "private"); session.WriteBytes("image", Png(), ".png");
                Require(!session.Enabled && session.DirectoryPath is null, "disabled session became active");
            }
            Require(!Directory.Exists(root) && ReferenceEquals(DebugSession.Current, previous), "disabled recording performed IO or leaked ambient scope");
            if (!DebugSession.IsSupported)
            {
                using var production = DebugSession.Begin(true, root, "production");
                production.WriteText("text", "must not be saved"); production.Event("failure");
                Require(!production.Enabled && !Directory.Exists(root), "production override enabled diagnostics");
            }
        });
        yield return ("Debug bundles preserve failure evidence while removing credentials and image data URLs", () =>
        {
            if (!DebugSession.IsSupported) return;
            var root = NewRoot(); var secret = "test-credential-" + Guid.NewGuid().ToString("N"); string directory;
            using (var session = DebugSession.Begin(true, root, "generation"))
            {
                directory = session.DirectoryPath!; session.RegisterSecret(secret);
                session.WriteJson("request", new { apiKey = "other-key", ProtectedApiKey = "protected-key", max_tokens = 1536,
                    usage = new { completion_tokens = 20 }, headers = new { Authorization = "Bearer header-key" },
                    text = "echo " + secret, image = "data:image/png;base64,iVBORw0KGgo=" });
                session.WriteText("response", "{\"password\":\"private-password\",\"nested\":{\"access_token\":\"private-token\"},\"echo\":\"" + secret + "\"}");
                session.WriteText("plain", "Authorization: Bearer plain-header-key\napi_key=plain-key\n" + secret + " data:image/png;base64,iVBORw0KGgo=");
                var exception = new InvalidOperationException("synthetic failure " + secret);
                exception.Data["private-object"] = "must-not-dump-exception-data";
                session.WriteJson("exception", new { exception });
                session.WriteJson("dictionary", new Dictionary<string, string> { [secret] = "safe field" });
                session.WriteJson("nested-json", new { content = JsonSerializer.Serialize(new { password = "nested-private-password", api_key = "nested-private-key", ordinary = "keep nested detail" }) });
                var deep = "{\"api_key\":\"deep-private-key\"}";
                for (var level = 0; level < 12; level++) deep = JsonSerializer.Serialize(new { content = deep });
                session.WriteText("deep-json", deep);
                session.WriteBytes("window", Png(), ".png");
                session.Event("layout-validation-failed", new { type = "InvalidDataException", error = "overlapping boxes" });
            }
            var text = string.Join("\n", Directory.GetFiles(directory).Where(path => Path.GetExtension(path) != ".png").Select(File.ReadAllText));
            foreach (var value in new[] { secret, "other-key", "protected-key", "header-key", "private-password", "private-token", "plain-header-key", "plain-key", "iVBORw0KGgo=", "must-not-dump-exception-data", "nested-private-password", "nested-private-key", "deep-private-key" })
                Require(!text.Contains(value, StringComparison.Ordinal), "credential or encoded image leaked");
            Require(text.Contains("1536") && text.Contains("completion_tokens") && text.Contains("20"), "token usage was mistaken for credentials");
            Require(text.Contains("layout-validation-failed") && text.Contains("overlapping boxes") && text.Contains("finishedUtc"), "failure evidence or manifest is missing");
            Require(Directory.GetFiles(directory, "*.png").Length == 1, "PNG artifact missing");
        });
        yield return ("Debug ambient scopes restore nesting and serialize parallel artifact writes", () =>
        {
            if (!DebugSession.IsSupported) return;
            var root = NewRoot(); var previous = DebugSession.Current;
            using (var outer = DebugSession.Begin(true, root, "outer"))
            {
                using (var inner = DebugSession.Begin(false, root, "inner"))
                { Require(ReferenceEquals(DebugSession.Current, inner), "disabled nested scope did not suppress recording"); DebugSession.Current!.WriteText("hidden", "not recorded"); }
                Require(ReferenceEquals(DebugSession.Current, outer), "outer scope not restored");
                Task.WhenAll(Enumerable.Range(0, 20).Select(index => Task.Run(() => DebugSession.Current!.WriteJson("parallel", new { index })))).GetAwaiter().GetResult();
                Require(Directory.GetFiles(outer.DirectoryPath!, "*-parallel.json").Length == 20, "parallel writes were lost or collided");
                Require(Directory.GetFiles(outer.DirectoryPath!, "*-hidden.txt").Length == 0, "disabled inner scope wrote into outer bundle");
            }
            Require(ReferenceEquals(DebugSession.Current, previous), "ambient scope survived disposal");
        });
        yield return ("Invalid debug destinations and artifact paths never interrupt the task", () =>
        {
            if (!DebugSession.IsSupported) return;
            var root = NewRoot(); Directory.CreateDirectory(root); var blockingFile = Path.Combine(root, "file"); File.WriteAllText(blockingFile, "existing");
            using (var broken = DebugSession.Begin(true, blockingFile, "failure"))
            { broken.WriteJson("request", new { value = 1 }); broken.Event("failed"); Require(broken.Warning is not null && !broken.Enabled, "IO failure was not reported"); }
            using var valid = DebugSession.Begin(true, root, "path-check");
            valid.WriteText("../escaped", "must not escape"); valid.WriteBytes("file", Png(), "../payload.bin");
            Require(!File.Exists(Path.Combine(root, "escaped.txt")), "artifact escaped its session directory");
            Require(valid.Warning is not null, "invalid name was not reported");
            valid.WriteText("safe", "still working");
            Require(Directory.GetFiles(valid.DirectoryPath!, "*-safe.txt").Length == 1, "a rejected artifact disabled valid recording");
        });
        yield return ("Debug file limits skip oversized output and keep a readable manifest", () =>
        {
            if (!DebugSession.IsSupported) return;
            using var session = DebugSession.Begin(true, NewRoot(), "limits");
            var large = new byte[32 * 1024 * 1024 + 1]; Png().CopyTo(large, 0);
            session.WriteBytes("too-large", large, ".png");
            Require(session.Warning is not null && Directory.GetFiles(session.DirectoryPath!, "*-too-large.png").Length == 0, "oversized file was written");
            session.Event("continuing");
            using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(session.DirectoryPath!, "manifest.json")));
            Require(manifest.RootElement.GetProperty("formatVersion").GetString() == "1.0", "manifest not readable");
        });
    }

    private static string NewRoot() => Path.Combine(Path.GetTempPath(), "WechatDraft-debug-tests-" + Guid.NewGuid().ToString("N"));
    private static byte[] Png() => [137, 80, 78, 71, 13, 10, 26, 10];
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
}
