# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Overview

Windows x64 desktop prototype (C# + WinUI 3, .NET 10) that drafts Chinese greeting messages for WeChat: with a one-on-one chat open, the user triggers generation (button or default hotkey `Ctrl+Alt+B`), the app captures chat screenshots, calls a multimodal chat-completions model, and — only when auto-append is enabled and every safety check passes — appends the greeting to the chat input box. **Sending is always manual; the app never presses Enter or clicks Send.**

`AGENTS.md` holds the full contribution rules. Project docs and all user-facing strings are Chinese (e.g. task status values like `成功`/`写入中`); keep them that way.

## Commands

Builds prefer the repo-local SDK `.tools\dotnet\dotnet.exe` and fall back to system `dotnet`. NuGet packages cache into the repo `.nuget\` directory; if `.tools\feed` exists, `build.ps1` uses it as an offline source automatically.

```powershell
.\build.ps1                             # Restore + build Release x64
.\build.ps1 -Test -Publish             # + full test suite, publish to artifacts/app
.\build.ps1 -Test -Publish -Production # Production build -> artifacts/production-app (debug capture compile-time disabled)
.\run.ps1                               # Launch portable client (publishes first if artifacts/app missing)
.\.tools\dotnet\dotnet.exe run --project tests/WechatDraft.Tests -c Release                        # Tests only
.\.tools\dotnet\dotnet.exe run --project tests/WechatDraft.Tests -c Release -- --native-clipboard  # + real Windows clipboard round-trip
```

There is **no single-test filter**: tests are a custom console runner, not xUnit. `tests/WechatDraft.Tests/Program.cs` registers every check with `Test("behavior name", async () => ...)` plus `Equal`/`True`/`Throws` helpers, prints PASS/FAIL lines, and reports via exit code. The whole suite runs on each invocation; all checks must pass.

`tools/WechatDraft.Integration` is a separate dev-diagnostics console (provider switch, salutation stats, debug toggles, clipboard format probe). Credentials go only via stdin; it exposes no mouse/keyboard/chat commands.

## Architecture

Three source projects. Dependency direction: **App → Core + Windows, Windows → Core**. Orchestration and protocol live in Core (plain `net10.0`); all Win32/UIA/GDI/DPI code lives in Windows; App is the WinUI 3 shell.

- `src/WechatDraft.Core/TaskCoordinator.cs` — orchestrates one task behind a `SemaphoreSlim` mutex with a shared cancellation token and time budget: session checks → screenshots → model calls → optional scroll-up (max 3 scrolls / 4 captures) → output validation → contact-profile reuse → persist write intent → single append → read-back verification.
- `src/WechatDraft.Core/Models.cs` — the abstraction seams: `IConversationAdapter` (`BeginAsync`/`CaptureAsync`/`ScrollUpAsync`/`IsUnchangedAsync`/`AppendAsync`/`RestoreAsync` plus capability flags `CanAutoAppend`, `CanCollectMoreContext`, `AutomationMethod`), `IGreetingModel`, `ISecretProtector`.
- Adapter implementations behind `IConversationAdapter`:
  - `WechatAdapter` (Windows) — UIA path; reads/writes the Qt input field via text patterns, no Enter injection.
  - `WechatVisionAdapter` (Windows) — vision path; whole-window screenshot + model layout recognition + limited keyboard/mouse + clipboard. `WechatTemplateLocator` does DPI-scaled toolbar template matching as anchor hints.
  - `FallbackConversationAdapter` (Core) — UIA→vision fallback, triggered **only** by `UiAutomationUnavailableException` (missing controls or initial 8s enumeration timeout), never by ambiguous candidates, known group chats, cancellation, or target changes.
  - `DemoServices` (App) — demo mode: fake chat + local model, reusing the same orchestration interfaces.
- Model access: `CompatibleModelClient` (chat completions with images). Vision layout requests return `VisionLayout` JSON with 0–1000 normalized coordinates that are **validated locally** (`VisionLayout.cs`) before any click; clicks always avoid the send region (`VisionRules.ClickRegion`).
- Persistence: `LocalRepository` → SQLite `drafts.db` with `settings`/`contacts`/`tasks`/`sent_greetings` tables; every business payload and API key is DPAPI-encrypted per current Windows user (`DpapiProtector`). Data dir is EXE-side `data/` (or `WECHAT_DRAFT_DATA`). `AccountScope` groups local data; contact matching is exact, case-sensitive remark/display-name matching within a scope — chat titles are not unique identities.
- App shell: `Program.Main` (custom entry point via `DISABLE_XAML_GENERATED_MAIN`) takes the single-instance mutex `Local\WechatDraft.Desktop`. `MainWindow` is split into partials (`.Pages`, `.Examples`, `.ApiProfiles`, `.WindowSelection`, `.WindowLayout`, `.SentGreetings`). Most pages are built in code with shared builders in `Ui.cs`; `HistoryPageView.xaml` uses a bounded, virtualized ListView and compiled bindings to `HistoryItem`. `HistorySearchSession` performs cancellable background paging/decryption with a page-lifetime cache; do not wrap this list in another ScrollViewer. There is no general MVVM layer yet.
- Task state is **not** an enum: `TaskRecord.GenerationStatus` / `FillStatus` are Chinese string values; changing them means updating UI, startup recovery (`RecoverInterrupted`), and tests together.

## Safety invariants (non-negotiable)

- Never send messages automatically: no Enter injection, no clicking the send region.
- Preserve the user's existing draft: append-only via `DraftRules.Append` (separator newline, whitespace untouched); re-verify target/focus/full original text before and after writing; stop whenever target, title, or text changes; never retry or replay an uncertain write.
- Persist the write intent (`AppendAttemptId` + `FillStatus=写入中`) **before** performing any write; startup recovery only marks records (`中断` / `预填结果待确认`), it never re-executes writes.
- Clipboard work must back up and restore all formats with sequence-number checks (`SafeClipboard` / `VisionClipboard` / `ClipboardSession`); refuse the operation when a safe backup is impossible.
- No model keys in source. Secrets are DPAPI-protected only. Screenshots stay in memory unless dev debug mode is explicitly enabled (`DebugSession`); `-Production` builds define `WECHAT_DRAFT_PRODUCTION` and disable debug capture at compile time.
- Exclude personal `data/`, databases, logs, and build caches from commits and distribution. UIA accessibility does not establish Tencent authorization.

## Documentation sync

When architecture, core flows, state models, or safety constraints change, update the affected docs in the same change: `docs/implementation.md` (current implementation), `docs/requirements.md` (design), `docs/real-mode-verification.md` (what was actually tested against real WeChat). Never present untested WeChat versions or UIA success as evidence for the vision path — keep design goals, implementation, and verification evidence distinct.

## Testing guidelines

- Behavior-named checks like `Test("User modification prevents input...", ...)`; cover preservation, cancellation, concurrency, parsing, credentials, and uncertain writes.
- Use fakes (`FakeAdapter`, `FakeModel`, `StubHandler`) and isolated temp data directories; never touch personal `data/`.
- DPAPI-dependent checks require Windows. Live tests need user-authorized chats and model destinations; prefer demo mode. Record tested WeChat versions in the verification doc.
