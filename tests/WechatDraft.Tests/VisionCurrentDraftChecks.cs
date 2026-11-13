using WechatDraft.Core;
using WechatDraft.Windows;

internal static class VisionCurrentDraftChecks
{
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases(string root)
    {
        yield return ("Visual direct append confirms the exact suffix only from a fresh copy", () =>
        {
            const string greeting = "祝福";
            if (!new ClipboardDraftRead(true, "原文 " + greeting).ConfirmsSuffix(greeting) ||
                !new ClipboardDraftRead(true, greeting).ConfirmsSuffix(greeting) ||
                new ClipboardDraftRead(false, greeting).ConfirmsSuffix(greeting) ||
                new ClipboardDraftRead(true, greeting + "其他内容").ConfirmsSuffix(greeting))
                throw new Exception("Direct append needs the exact greeting at the end of a fresh copy.");
            return Task.CompletedTask;
        });
        yield return ("Background visual differences retain task diagnostics without an ambient debug session", async () =>
        {
            var folder = Path.Combine(root, "input-change-debug-" + Guid.NewGuid().ToString("N"));
            using var debug = DebugSession.Begin(true, folder, "input-change-test");
            var state = new VisionInputChangeLog(Frame(32), 192, debug);
            Task observe;
            using (ExecutionContext.SuppressFlow())
                observe = Task.Run(() =>
                {
                    if (DebugSession.Current is not null) throw new Exception("The timer simulation inherited an ambient session.");
                    state.Observe(Frame(64));
                    state.Observe(Frame(96));
                });
            await observe;
            if (DebugSession.IsSupported)
            {
                var files = Directory.GetFiles(folder, "*", SearchOption.AllDirectories);
                if (files.Count(x => x.EndsWith("input-change.json")) != 1 ||
                    !files.Any(x => x.EndsWith("input-baseline.png")) || !files.Any(x => x.EndsWith("input-changed.png")))
                    throw new Exception("The background comparison lost or duplicated its task artifacts.");
            }
            else if (Directory.Exists(folder)) throw new Exception("Production retained intermediate screenshots.");
        });
    }

    private static VisionRegionSnapshot Frame(byte value) => new(8, 8, Enumerable.Repeat(value, 8 * 8 * 4).ToArray());
}
