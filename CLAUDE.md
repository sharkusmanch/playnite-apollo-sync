# CLAUDE.md

Behavioral guidelines to reduce common LLM coding mistakes. Merge with project-specific instructions as needed.

**Tradeoff:** These guidelines bias toward caution over speed. For trivial tasks, use judgment.

## 1. Think Before Coding

**Don't assume. Don't hide confusion. Surface tradeoffs.**

Before implementing:
- State your assumptions explicitly. If uncertain, ask.
- If multiple interpretations exist, present them - don't pick silently.
- If a simpler approach exists, say so. Push back when warranted.
- If something is unclear, stop. Name what's confusing. Ask.

## 2. Simplicity First

**Minimum code that solves the problem. Nothing speculative.**

- No features beyond what was asked.
- No abstractions for single-use code.
- No "flexibility" or "configurability" that wasn't requested.
- No error handling for impossible scenarios.
- If you write 200 lines and it could be 50, rewrite it.

Ask yourself: "Would a senior engineer say this is overcomplicated?" If yes, simplify.

## 3. Surgical Changes

**Touch only what you must. Clean up only your own mess.**

When editing existing code:
- Don't "improve" adjacent code, comments, or formatting.
- Don't refactor things that aren't broken.
- Match existing style, even if you'd do it differently.
- If you notice unrelated dead code, mention it - don't delete it.

When your changes create orphans:
- Remove imports/variables/functions that YOUR changes made unused.
- Don't remove pre-existing dead code unless asked.

The test: Every changed line should trace directly to the user's request.

## 4. Goal-Driven Execution

**Define success criteria. Loop until verified.**

Transform tasks into verifiable goals:
- "Add validation" → "Write tests for invalid inputs, then make them pass"
- "Fix the bug" → "Write a test that reproduces it, then make it pass"
- "Refactor X" → "Ensure tests pass before and after"

For multi-step tasks, state a brief plan:
```
1. [Step] → verify: [check]
2. [Step] → verify: [check]
3. [Step] → verify: [check]
```

Strong success criteria let you loop independently. Weak criteria ("make it work") require constant clarification.

---

**These guidelines are working if:** fewer unnecessary changes in diffs, fewer rewrites due to overcomplication, and clarifying questions come before implementation rather than after mistakes.

---

## 5. C# Conventions

This project targets **.NET Framework 4.6.2**. C# 8+ features (nullable reference types, switch expressions, using declarations) are not available.

**Build**
- Use `dotnet build ApolloSync.csproj -c Release`. This is what CI runs. It does generate the XAML `.g.cs` code-behind (verified: `obj/Release/net462/Settings/ApolloSyncSettingsView.g.cs` is produced on a clean build with the .NET 8 SDK).
- A previous version of this file mandated `MSBuild.exe` on the grounds that `dotnet build` skips WPF XAML codegen on .NET Framework. That is not true here. `MSBuild.exe` from a **Build Tools**-only install additionally fails to resolve `Microsoft.NET.Sdk.WindowsDesktop` unless the managed desktop workload is present.
- This project uses **SDK-style `.csproj`** (`Microsoft.NET.Sdk.WindowsDesktop`). New `.cs` files are auto-included by default. Do not add `<Compile Include>` entries manually.
- When adding NuGet packages, only use versions that ship a `net462` (or `net461`/`net45`) target folder. Don't rely on `netstandard2.0` fallbacks — they can silently break at runtime inside Playnite's AppDomain.

**Naming** (Section 5 naming rules take priority over "match existing style" — don't perpetuate violations in new code)
- Classes, methods, properties, enums: `PascalCase`
- Local variables and parameters: `camelCase`
- Private fields (instance and static): `_camelCase` (e.g., `_api`, `_rng`)
- Exception: the logger is `logger` (no underscore) — this is the Playnite SDK convention
- Constants: `PascalCase`
- Interfaces: `IPascalCase`

**Exception handling**
- Catch only exceptions you can handle meaningfully. **Always log them before re-throwing or recovering.**
- Use `throw;` (not `throw ex;`) to preserve the original stack trace.
- Don't use exceptions for control flow; guard with defensive checks instead.
- Exception: cleanup code (deleting temp/lock files in `finally` or `OnApplicationStopped`) may use empty `catch { }` — add a comment explaining why silence is acceptable.
- For batch operations (processing N games), catch and log per-item failures so one bad game doesn't abort the rest. This is not "error handling for impossible scenarios" — it's expected partial failure.

**Resource management**
- Wrap `IDisposable` objects in `using` blocks.
- Don't leave streams, file handles, or HTTP clients undisposed.
- GDI+/`System.Drawing` objects (`Image`, `Bitmap`, `Graphics`) must be disposed — memory leaks are silent and cumulative. Always use `using` or explicit `Dispose()` in a `finally` block.

**Async**
- No `async void` except for event handlers.
- Don't block async with `.Result` or `.Wait()` — deadlock risk on UI threads.

**Access modifiers**
- Default to `private`. Only widen visibility when there's an explicit reason.
- Prefer interfaces for service dependencies (already established in this codebase).

---

## 6. Playnite Plugin Rules

**Logging**
- Always declare: `private static readonly ILogger logger = LogManager.GetLogger();`
- Use `logger.Debug/Info/Warn/Error` — never `Console.WriteLine` or `Debug.WriteLine`.

**Threading**
- The Playnite SDK is **not thread-safe** for UI and view operations.
- Never modify UI objects from a background thread.
- The established pattern in this codebase is to run sync in `Task.Run` and call `PlayniteApi.Notifications.Add()` and `PlayniteApi.Database` reads directly from that background thread — this works in practice. Do not add `Dispatcher.Invoke` wrapping to these existing call sites.
- Actual UI manipulation (dialogs, view navigation) must always be marshalled via `Application.Current.Dispatcher.Invoke(...)`.

**Database**
- Wrap bulk game updates in `PlayniteApi.Database.BufferedUpdate()` to avoid excessive change events.
- Games reference Platforms, Series, etc. by ID list — update through the correct collection (e.g., `api.Database.Platforms.Update()`), not by modifying the game's ID list alone.

**Settings**
- Settings are serialized to JSON automatically via `LoadPluginSettings<T>()` / `SavePluginSettings()`.
- Use `[DontSerialize]` (Playnite SDK attribute) to exclude non-persistent properties.
- Implement `BeginEdit`, `CancelEdit`, `EndEdit`, `VerifySettings` on the settings view model.

**Assembly references**
- Only reference `PlayniteSDK` NuGet package. Never reference `Playnite.dll` or `Playnite.Common.dll` directly — causes load failures.
- Avoid NuGet packages whose versions conflict with Playnite's bundled assemblies.

**Paths**
- Never hardcode Playnite-internal paths. Use `api.Paths.ConfigurationPath`, `api.Paths.ExtensionsDataPath`, etc. for portability between installed and portable Playnite.
- Exception: Apollo/Sunshine config paths (`%ProgramFiles%\...`) are external application paths, not Playnite paths — hardcoding those is intentional.
- Temp/lock files go to `Path.GetTempPath()`, not plugin config paths, to avoid permission issues.

**Lifecycle**
- Don't access `PlayniteApi.Database` before `OnApplicationStarted()` fires; data may not be ready.
- Clean up timers, file watchers, and background threads in `OnApplicationStopped()`.

**Playnite SDK type quirks**
- `Game.Playtime` and `Game.PlayCount` are `ulong` — cast explicitly to `long`/`int` when assigning to DTOs or doing arithmetic. No implicit conversion exists.
- `Game.CompletionStatusId` defaults to `Guid.Empty`, not `null`. Guard with `!= Guid.Empty` before using it. `game.CompletionStatus?.Name` is nullable — access safely.
- `Game.Platforms`, `Game.Genres`, `Game.Tags`, etc. are `List<T>` — check for `null` before iterating; they're not initialized to empty lists by default.

**ManagedStore (plugin-specific state)**
- `ManagedStore` holds two things: `GameToUuid` (the UUID map) and `ManuallyRemoved` (games the user explicitly removed). Both live outside Playnite's database and are persisted by projecting them into plugin settings in `SaveManagedStore()`.
- When modifying sync logic, verify the store roundtrips correctly (write → reload → compare).
- The store and `apps.json` can diverge if a sync is interrupted; treat them as potentially inconsistent and reconcile defensively.
- UUID mapping is bidirectional: adding a game must write the entry to `apps.json` AND add to `store.GameToUuid`; removing must do both. Partial updates cause divergence.
- `ManuallyRemoved` must be recorded explicitly at the point of user action. Do not infer it from "in the store but missing from `apps.json`" — `SyncManagedStore()` prunes exactly those entries on startup, so the inference is always false after a restart.

**apps.json invariants**
- UUIDs in `apps.json` are always uppercase. `ConfigService` normalizes on load and save via `DeduplicateApps()`.
- Duplicate UUIDs are resolved by keeping the entry with the highest `id` value — lower-id duplicates are silently discarded.
- Writes go through two strategies, in order:
  - **Preferred** — temp file in the *destination directory*, `Flush(flushToDisk: true)`, then `File.Replace`. Atomic, and keeps the previous contents as `apps.json.bak`. Requires create rights in the destination directory.
  - **Fallback** — write to `%TEMP%` with `FileMode.CreateNew`, then `File.Copy` over the destination, retrying up to 3 times on `IOException`. **Not atomic**: `File.Copy` truncates and rewrites in place, so a crash mid-copy is recoverable only from the backup.
- **The default Program Files install always takes the fallback.** `TryFixFilePermissionsWithElevation` grants modify on the *file*, not the directory, so `File.Replace` can never create its temp file there. Do not describe the write as atomic in user-facing text.
- The backup is taken exactly once, *before* the retry loop — taking it inside would let a later attempt copy a half-written destination over the only good copy. `BackupExistingFile` tries `<destination>.bak` first and falls back to `%LOCALAPPDATA%\ApolloSync\backups\` (with the destination path flattened into the file name) when the install directory isn't writable, so a backup exists even in the Program Files case.
- Always delete the temp file in a `finally` block. Never write directly to `apps.json` in place.
- `SaveAppsConfig` propagates write failures. Callers must not persist the managed store after a failed write, or the store will claim ownership that `apps.json` doesn't reflect.

**Lock files and launch command**
- Lock files are named `apollosync-{gameId:N}.lock` (format specifier `N` = no-hyphen GUID) and always live in `Path.GetTempPath()`. Different games get different lock paths.
- Use `FileMode.Create` (not `CreateNew`) for lock files — stale locks from crashed sessions must be overwritten.
- The `cmd` field in an Apollo app entry must be a `powershell.exe -EncodedCommand <base64>` command, never a direct executable path. The encoded script polls for the lock file by game ID to coordinate launch timing with Apollo/Sunshine.
