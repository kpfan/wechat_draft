using System.Drawing;
using WechatDraft.Core;
using WechatDraft.Windows;

internal static class VisionInteractionChecks
{
    public static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("Mouse movement, releases and tagged program input never interrupt vision", () =>
        {
            var state = new VisionInteractionState();
            foreach (var message in new uint[] { 0x200, 0x202, 0x205, 0x208, 0x20C }) state.Mouse(message, false);
            foreach (var message in new uint[] { 0x201, 0x20A, 0x20E }) state.Mouse(message, true);
            state.Keyboard(true);
            Equal(false, state.Changed); Equal(null, state.Reason);
        });
        yield return ("Automatic input stops for real clicks, wheel and keyboard with the first cause preserved", () =>
        {
            foreach (var (message, reason) in new (uint, string)[] { (0x201, "mouse-click"), (0x204, "mouse-click"),
                (0x207, "mouse-click"), (0x20B, "mouse-click"), (0x20A, "mouse-wheel"), (0x20E, "mouse-wheel") })
            {
                var state = new VisionInteractionState(); state.Mouse(message, false); state.Keyboard(false);
                Equal(true, state.Changed); Equal(reason, state.Reason);
            }
            var keyboard = new VisionInteractionState(); keyboard.Keyboard(false); keyboard.Mouse(0x201, false);
            Equal("keyboard-input", keyboard.Reason);
        });
        yield return ("Stale startup focus notifications are ignored but actual foreground changes stop input", () =>
        {
            var state = new VisionInteractionState();
            state.Foreground(0, 2, 1); state.Foreground(2, 1, 1); state.Foreground(1, 1, 1);
            Equal(false, state.Changed);
            state.Foreground(2, 2, 1); state.Foreground(1, 1, 1);
            Equal(true, state.Changed); Equal("foreground-change", state.Reason);
        });
        yield return ("UIA browsing permits both wheel directions while retaining user scroll intent", () =>
        {
            var state = new VisionInteractionState(stopOnWheel: false);
            state.Mouse(0x20A, true); state.Mouse(0x20E, true);
            Equal(false, state.WheelObserved);
            state.Mouse(0x20A, false); state.Mouse(0x20E, false);
            Equal(true, state.WheelObserved); Equal(false, state.Changed); Equal(null, state.Reason);
        });
        yield return ("Wheel-only relaxation still detects clicks, typing and focus changes", () =>
        {
            var click = new VisionInteractionState(stopOnWheel: false);
            click.Mouse(0x20A, false); click.Mouse(0x201, false);
            Equal(true, click.Changed); Equal("mouse-click", click.Reason);
            var typing = new VisionInteractionState(stopOnWheel: false);
            typing.Mouse(0x20E, false); typing.Keyboard(false);
            Equal(true, typing.Changed); Equal("keyboard-input", typing.Reason);
            var focus = new VisionInteractionState(stopOnWheel: false);
            focus.Mouse(0x20A, false); focus.Foreground(2, 2, 1);
            Equal(true, focus.Changed); Equal("foreground-change", focus.Reason);
        });
        yield return ("UIA raw clicks and keys track activity without invalidating an unchanged task", () =>
        {
            var state = new VisionInteractionState(stopOnWheel: false, stopOnClick: false, stopOnKeyboard: false);
            var before = state.ActivityVersion;
            foreach (var message in new uint[] { 0x201, 0x204, 0x207, 0x20B, 0x20A, 0x20E }) state.Mouse(message, false);
            state.Keyboard(false);
            Equal(false, state.Changed); Equal(null, state.Reason); Equal(true, state.ActivityVersion != before);
            var after = state.ActivityVersion;
            state.Keyboard(true); state.Mouse(0x201, true); state.Mouse(0x200, false); state.Mouse(0x202, false);
            Equal(after, state.ActivityVersion);
            state.Foreground(2, 2, 1); Equal(true, state.Changed); Equal("foreground-change", state.Reason);
        });
        yield return ("Repeated selection and text notifications do not cancel an unchanged UIA chat", () =>
        {
            var identity = new UiaConversationIdentity("header", "editor", "session-A", "page-A");
            var changes = new UiaTaskChanges("原文  \r\n", "", "同名联系人", identity);
            changes.ObserveHeader("同名联系人"); changes.ObserveDraft("原文  \r\n"); changes.ObserveControlValue("");
            changes.ObserveConversation(identity with { }); changes.ObserveConversation(identity with { SelectedSession = "" });
            Equal(false, changes.Changed); Equal(null, changes.Reason);
        });
        yield return ("Actual UIA draft edits remain invalid after the original text is restored", () =>
        {
            var changes = new UiaTaskChanges("原文", "原文", "联系人", null);
            changes.ObserveDraft("原文新输入"); changes.ObserveDraft("原文");
            Equal(true, changes.Changed); Equal("draft-changed", changes.Reason);
            var control = new UiaTaskChanges("全文", "", "联系人", null);
            control.ObserveControlValue("新输入"); control.ObserveControlValue("");
            Equal(true, control.Changed); Equal("draft-changed", control.Reason);
        });
        yield return ("Same-title chat switches and unavailable UIA state remain sticky interruptions", () =>
        {
            var identity = new UiaConversationIdentity("header", "editor", "", "page-A");
            var changes = new UiaTaskChanges("", "", "同名联系人", identity);
            changes.ObserveConversation(identity with { ChatView = "page-B" }); changes.ObserveConversation(identity);
            Equal(true, changes.Changed); Equal("chat-changed", changes.Reason);
            var title = new UiaTaskChanges("", "", "联系人A", identity);
            title.ObserveHeader("联系人B"); title.ObserveHeader("联系人A");
            Equal(true, title.Changed); Equal("chat-changed", title.Reason);
            var missing = new UiaTaskChanges("", "", "联系人", identity);
            missing.Unavailable(); missing.ObserveConversation(identity);
            Equal(true, missing.Changed); Equal("uia-state-unavailable", missing.Reason);
        });
        yield return ("Read-only context retains startup pixels after source changes and excludes the draft panel", () =>
        {
            var frame = new VisionWindowFrame(new Rectangle(200, 100, 200, 200), 192);
            var layout = new VisionLayout
            {
                Header = new() { X = 0, Y = 0, Width = 1000, Height = 100 },
                Messages = new() { X = 0, Y = 100, Width = 1000, Height = 600 }
            };
            using var source = new Bitmap(200, 200);
            using (var graphics = Graphics.FromImage(source))
            {
                graphics.Clear(Color.Red); // Draft / unrelated screen content must stay out of the capture.
                graphics.FillRectangle(Brushes.Blue, 0, 0, 200, 20);
                graphics.FillRectangle(Brushes.Green, 0, 20, 200, 120);
            }
            using var frozen = new VisionContextSnapshot(source);
            var before = frozen.Capture(frame, layout, 0);
            using (var graphics = Graphics.FromImage(source)) graphics.Clear(Color.Magenta);
            using (var retry = frozen.CopyImage()) Equal(Color.Blue.ToArgb(), retry.GetPixel(10, 10).ToArgb());
            var after = frozen.Capture(frame, layout, 1);
            if (!before.PngBytes.AsSpan().SequenceEqual(after.PngBytes)) throw new Exception("Desktop changes entered the frozen context.");
            using var stream = new MemoryStream(after.PngBytes);
            using var result = new Bitmap(stream);
            Equal(Color.Blue.ToArgb(), result.GetPixel(30, 30).ToArgb());
            Equal(Color.Green.ToArgb(), result.GetPixel(30, result.Height - 30).ToArgb());
            for (var y = 0; y < result.Height; y++)
                for (var x = 0; x < result.Width; x++)
                    if (result.GetPixel(x, y).ToArgb() == Color.Red.ToArgb()) throw new Exception("Draft panel entered greeting context.");
        });
    }

    private static void Equal<T>(T expected, T actual)
    { if (!Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}."); }
}
