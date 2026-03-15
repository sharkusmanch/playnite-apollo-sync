# ApolloSync

A Playnite extension that syncs your game library to [Apollo](https://github.com/ClassicOldSong/Apollo)/[Sunshine](https://github.com/LizardByte/Sunshine) streaming applications.

## Features

- **Automatic sync** -- exports games matching your filters to Apollo/Sunshine's `apps.json` on install, library update, settings change, or Playnite startup (each individually configurable)
- **Filter presets** -- uses your existing Playnite filter presets to decide what gets exported (OR logic when multiple are selected)
- **Additional filters** -- platform, label, completion status, and category filters
- **Pin/unpin** -- pin games to prevent auto-removal even when they stop matching filters
- **Manual export/remove** -- right-click context menu for individual games
- **Manage exported games** -- view and manage all synced games from the settings tab
- **Non-destructive** -- only touches entries it created; preserves manual Apollo/Sunshine entries
- **Notification control** -- always, on sync only, or never
- **Permissions handling** -- prompts to fix write permissions if `apps.json` is in a protected directory

## Installation

Download the latest `.pext` from [Releases](https://github.com/sharkusmanch/playnite-apollo-sync/releases) and open it, or install from the Playnite add-on browser.

## Setup

1. Go to **Extensions** > **ApolloSync** > **Settings**
2. Set the path to your `apps.json` (or leave blank for default location)
3. Select one or more Playnite filter presets and/or configure platform/label/category/completion status filters
4. Enable desired sync triggers (library update, startup, settings change)

## Building

Requires .NET Framework 4.6.2 SDK and [Task](https://taskfile.dev).

```bash
git clone https://github.com/sharkusmanch/playnite-apollo-sync.git
cd playnite-apollo-sync
task all
```

## License

[MIT](LICENSE)
