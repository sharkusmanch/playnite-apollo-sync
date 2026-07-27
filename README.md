# ApolloSync

A Playnite extension that syncs your game library to [Apollo](https://github.com/ClassicOldSong/Apollo)/[Sunshine](https://github.com/LizardByte/Sunshine) streaming applications.

## Features

- **Automatic sync** -- exports games matching your filters to Apollo/Sunshine's `apps.json` on install, library update, settings change, or Playnite startup (each individually configurable)
- **Filter presets** -- uses your existing Playnite filter presets to decide what gets exported (OR logic when multiple are selected). Filter by platform, category, completion status or anything else by building the preset in Playnite's library view.
- **Pin/unpin** -- pin games to prevent auto-removal even when they stop matching filters
- **Manual export/remove** -- right-click context menu for individual games. A game you remove by hand stays removed; sync will not re-add it until you export it again.
- **Manage exported games** -- view and manage all synced games from the settings tab
- **Preserves your own entries** -- only adds and removes the app entries it created. Note that saving `apps.json` also normalizes it: UUIDs on *all* entries are upper-cased, entries without a UUID are moved to the end, and any array element that is not a JSON object is dropped.
- **Notification control** -- always, on sync only, or never
- **Permissions handling** -- prompts to fix write permissions if `apps.json` is in a protected directory

## Installation

Download the latest `.pext` from [Releases](https://github.com/sharkusmanch/playnite-apollo-sync/releases) and open it, or install from the Playnite add-on browser.

## Setup

1. Go to **Extensions** > **ApolloSync** > **Settings**
2. Set the path to your `apps.json` (or leave blank for default location)
3. Select one or more Playnite filter presets (create them in Playnite's main library view first)
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
