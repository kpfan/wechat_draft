# Repository Guidelines

## Project Structure & Module Organization

- `src/WechatDraft.App/`: WinUI 3 UI, navigation, tray integration, and demo services.
- `src/WechatDraft.Core/`: task coordination, model protocol, validation, examples, and SQLite persistence.
- `src/WechatDraft.Windows/`: window discovery, UIA, screenshots, clipboard protection, hotkeys, and DPAPI.
- `tests/WechatDraft.Tests/`: executable regression suite with fake adapters and HTTP handlers.
- `config/blessing_examples.json`: blessing examples; preserve schema and unique IDs.
- `tools/WechatDraft.Integration/`: model connectivity diagnostics.
- `docs/`: design, implementation, and verification documents. Generated binaries belong in `artifacts/app/`.

## Build, Test, and Development Commands

Use Windows x64 and the .NET 10 SDK in `global.json`. Scripts prefer `.tools/dotnet/dotnet.exe`, then system `dotnet`.

```powershell
.\build.ps1                 # Restore dependencies and build Release x64
.\build.ps1 -Test -Publish  # Also run tests and publish artifacts/app
.\run.ps1                   # Launch the portable client; publish if absent
.\.tools\dotnet\dotnet.exe run --project tests/WechatDraft.Tests -c Release
```

Publishing includes Windows App SDK; running requires .NET 10 Desktop Runtime. Builds prefer offline dependencies.

## Coding Style & Naming Conventions

Use four-space indentation, file-scoped namespaces, PascalCase types/methods, camelCase locals, `_camelCase` fields, and `Async` suffixes. Nullable references and implicit usings are enabled. Keep platform code in Windows; orchestration in Core. No formatter/linter is configured.

## Testing Guidelines

Use the custom console runner and behavior names: `Test("User modification prevents input...", ...)`. All checks must pass; no coverage threshold exists. Cover preservation, cancellation, concurrency, parsing, credentials, and uncertain writes. DPAPI needs Windows. Prefer fakes/demo mode; live tests require user-authorized chats and model destinations. Record tested WeChat versions.

## Commit & Pull Request Guidelines

Use the `main` branch as the default branch. Use imperative subjects, e.g. `Preserve whitespace when appending drafts`. PRs should describe behavior, validation results, compatibility limits, and related issues. Include demo UI screenshots. Review staged files before committing; personal runtime data, credentials, diagnostics, SDKs, caches, and published binaries must remain ignored.

## Documentation & Maintenance

- `docs/requirements.md`: product design and acceptance criteria.
- `docs/implementation.md`: architecture, flow, states, persistence, and safety implementation.
- `docs/real-mode-verification.md`: compatibility evidence and untested scenarios.
- `README.md`: setup, operation, and builds.

When architecture, core flows, state models, or safety constraints change, **check and update all affected documents in the same change**. Leave documents unchanged when no relevant changes exist. Distinguish design goals, implementation, and verification evidence.

## Security & Automation Constraints

Never send messages automatically. Preserve existing drafts; stop when target or text changes, and never replay uncertain writes. Match scoped names; titles are not unique identities. Keep credentials DPAPI-protected and screenshots transient. Exclude personal `data/`, databases, logs, and build caches from commits and distribution. UIA accessibility does not establish Tencent authorization.
