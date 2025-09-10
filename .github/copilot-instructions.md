# Copilot instructions for ApolloSync

## Quick facts
- Language/target: C# (.NET Framework 4.6.2)
- Test framework: MSTest v2
- Package manager: NuGet (packages referenced in csproj)
- Task runner: Taskfile.yaml (https://taskfile.dev)
- Playnite SDK: 6.12.0
- Plugin Id: f987343d-4168-4f44-9fb0-e3a21da314ad
- OS/shell: Windows + PowerShell (pwsh)

## Build, test, and package
- Restore:
  - pwsh
    dotnet restore ApolloSync.csproj
- Build (Release):
  - pwsh
    dotnet build ApolloSync.csproj --configuration Release
- Tests:
  - pwsh
    dotnet test Tests/ApolloSync.Tests.csproj -v minimal
- Using Taskfile:
  - pwsh
    task restore
    task build
    task test
    task pack
    task all

Outputs:
- Build: bin/Debug|Release/net462/
- Packed extension: dist/*.pext

## Core invariants (don’t break these)
- apps.json UUID must equal the Playnite Game.Id (uppercase with hyphens). Do not generate random UUIDs.
- ManagedStore keeps an identity mapping: GameId -> GameId.
- The numeric "id" field in apps.json stays a unique random integer string; keep generator logic intact.
- Cover images:
  - Use local filesystem path only if it exists.
  - Skip remote URLs (http/https) for image-path.
- Removal:
  - Remove by matching the UUID (which equals Game.Id).
  - If store mapping is missing, fall back to Game.Id.

## Code layout
- ApolloSync.cs: Main plugin orchestration (menus, syncing, pin/unpin, filter logic, managed store sync).
- Services/SyncService.cs:
  - AddOrUpdate(JObject config, ManagedStore store, Game game): builds/updates apps.json entry.
  - Remove(JObject config, ManagedStore store, Game game): removes from apps.json and store.
- Services/ConfigService.cs: Load/Save apps.json.
- Models/Models.cs: ManagedStore (Dictionary<Guid, Guid> GameToUuid identity mapping).
- Settings: ApolloSyncSettings, view model and XAML view.
- Tests/: MSTest unit tests for SyncService and apps.json management.

## Adding tests
- Place new tests in Tests/ and keep the SDK-style test project.
- Prefer fast unit tests that avoid real Playnite API calls; simulate inputs with Playnite SDK models where possible.
- For any new feature or behavior change, add unit tests whenever possible (happy path plus at least one edge case).
- Run:
  - pwsh
    dotnet test Tests/ApolloSync.Tests.csproj -v minimal

## Taskfile tasks
- restore: dotnet restore ApolloSync.csproj
- format: dotnet format ApolloSync.csproj
- build: dotnet build ApolloSync.csproj --configuration Release
- pack: packages via Playnite Toolbox into dist/
- install: installs the packed extension (opens .pext)
- logs: tails Playnite extensions.log
- test: dotnet test Tests/ApolloSync.Tests.csproj -v minimal

## Do / Don’t for AI edits
- Do:
  - Keep changes minimal and scoped.
  - Update or add unit tests for public behavior changes.
  - Respect Windows paths and pwsh command syntax.
  - Keep Playnite SDK at 6.12.0 unless explicitly upgraded everywhere.
  - Prefer object-oriented design: encapsulate logic in small classes/services with clear responsibilities and interfaces.
- Don’t:
  - Introduce migration code for apps.json UUIDs (unreleased; identity mapping only).
  - Hardcode user-specific paths except for known Playnite defaults.
  - Add network calls or external side effects.

## Notes
- Known advisory: Newtonsoft.Json 10.0.3 in main project (ExcludeAssets=runtime); tests use 13.x. Upgrade separately if desired.
- .gitignore excludes .nuget/ and .vscode/ entirely.
