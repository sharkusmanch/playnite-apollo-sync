# ApolloSync

![Version](https://img.shields.io/badge/version-0.1.0-blue.svg)
![Platform](https://img.shields.io/badge/platform-Playnite-green.svg)
![License](https://img.shields.io/badge/license-MIT-yellow.svg)

A powerful Playnite extension that automatically synchronizes your game library with Apollo/Sunshine streaming applications, enabling seamless game streaming across devices.

## 🚀 Features

### ⚡ Automatic Synchronization
- **Auto-export installed games** - Automatically adds newly installed games to Apollo/Sunshine
- **Auto-remove uninstalled games** - Automatically removes uninstalled games from Apollo/Sunshine
- **Library update sync** - Ensures all installed games are exported when Playnite's library is updated
- **Batch operations** - Optimized performance with single-write batch processing

### 🎯 Advanced Filtering
- **Platform filtering** - Choose specific platforms to include/exclude from export
- **Label-based filtering** - Export only games with specific labels
- **Completion status filtering** - Filter by game completion status (Not Played, Completed, etc.)
- **Smart defaults** - Automatically creates and manages default labels

### 🛠️ Management Tools
- **Manual export/remove** - Right-click context menu for individual games
- **Bulk sync operations** - Main menu option to sync all installed games
- **Managed games window** - View and manage all exported games
- **Progress tracking** - Real-time progress feedback for all operations

### 🎮 Seamless Integration
- **Direct image paths** - Uses Playnite's existing cover images without duplication
- **Non-destructive** - Only manages games it creates, preserves manual entries
- **Error handling** - Comprehensive error reporting and recovery
- **Localization ready** - Full localization support

## 📦 Installation

### Requirements
- **Playnite** 6.2.0 or higher
- **Apollo** or **Sunshine** streaming application
- **.NET Framework 4.6.2** or higher

### Install from Playnite Add-ons
1. Open Playnite
2. Go to **Add-ons** → **Browse**
3. Search for "ApolloSync"
4. Click **Install**

### Manual Installation
1. Download the latest `.pext` file from [Releases](https://github.com/your-username/ApolloSync/releases)
2. In Playnite, go to **Add-ons** → **Install Add-on**
3. Select the downloaded `.pext` file
4. Restart Playnite

## ⚙️ Configuration

### Initial Setup
1. Go to **Extensions** → **ApolloSync** → **Settings**
2. Set the path to your Apollo/Sunshine `apps.json` file
3. Configure automatic sync options as desired
4. Set up filters if you want to limit which games are exported

### Settings Overview

#### General Tab
- **Auto-export installed games** - Automatically export games when they're installed
- **Auto-remove uninstalled games** - Automatically remove games when they're uninstalled
- **Sync all on library update** - Export all installed games after library updates
- **Apps.json path** - Path to your Apollo/Sunshine configuration file

#### Filters Tab
- **Platform selection** - Choose which gaming platforms to include
- **Label requirements** - Require specific labels for export
- **Completion status** - Filter by game completion status
- **Bulk selection tools** - Select All/Clear All buttons for easy management

## 🎮 Usage

### Automatic Operations
Once configured, ApolloSync works automatically:
- Installing a game in Playnite → Automatically appears in Apollo/Sunshine
- Uninstalling a game → Automatically removed from Apollo/Sunshine
- Library updates → All installed games are synchronized

### Manual Operations

#### Game Context Menu
Right-click any game in Playnite to access:
- **Export to Apollo/Sunshine** - Manually export selected games
- **Remove from Apollo/Sunshine** - Manually remove selected games
- **Sync all installed games** - Sync entire library

#### Main Menu
Access **Extensions** → **ApolloSync** for:
- **Sync all installed games** - Bulk synchronization
- **Manage Exported Games** - View and manage exported games

#### Managed Games Window
- View all games currently exported to Apollo/Sunshine
- Remove games individually or in bulk
- See export status and metadata
- Filter and search exported games

## 🔧 Technical Details

### Architecture
- **Service-based design** - Modular architecture with separate services for config, sync, and storage
- **Batch processing** - Optimized I/O with single-file writes
- **Event-driven** - Responds to Playnite events for automatic synchronization
- **Error resilient** - Comprehensive error handling and logging

### File Management
- **Non-destructive** - Only manages entries it creates
- **Backup aware** - Preserves existing Apollo/Sunshine configurations
- **Direct image paths** - References Playnite images directly without copying
- **JSON integrity** - Maintains proper JSON structure and formatting

### Supported Formats
- **Apollo** apps.json format
- **Sunshine** apps.json format
- **Cross-compatible** - Works with both streaming solutions

## 🐛 Troubleshooting

### Common Issues

#### "Extension failed to load properly"
- Ensure .NET Framework 4.6.2+ is installed
- Check Playnite logs for detailed error messages
- Try reinstalling the extension

#### Games not appearing in Apollo/Sunshine
- Verify the apps.json path is correct
- Check that games meet your filter criteria
- Ensure Apollo/Sunshine has reloaded its configuration

#### Sync operations fail
- Verify write permissions to the apps.json file
- Check that the file isn't locked by another application
- Review error messages in the progress dialog

### Debug Information
Enable debug logging in Playnite settings and check:
- `%APPDATA%\Playnite\extensions.log` for extension logs
- Settings validation messages
- Sync operation results

## 🚧 Development

### Building from Source

#### Prerequisites
- Visual Studio 2019+ or VS Code with C# extension
- .NET Framework 4.6.2 SDK
- Playnite SDK 6.2.0

#### Build Steps
```bash
# Clone the repository
git clone https://github.com/your-username/ApolloSync.git
cd ApolloSync

# Restore dependencies
dotnet restore

# Build the project
dotnet build --configuration Release

# Package the extension (requires Task)
task pack
```

#### Project Structure
```
ApolloSync/
├── ApolloSync.cs              # Main plugin class
├── ApolloSyncSettings.cs      # Settings model and view model
├── ApolloSyncSettingsView.xaml.cs # Settings UI
├── ManagedGamesWindow.xaml.cs # Managed games interface
├── Models/
│   └── Models.cs              # Data models
├── Services/
│   ├── ConfigService.cs       # Configuration management
│   ├── SyncService.cs         # Core sync logic
│   └── ManagedStoreService.cs # Game tracking
└── Localization/
    └── en_US.xaml             # Localization strings
```

### Contributing
1. Fork the repository
2. Create a feature branch
3. Make your changes
4. Add tests if applicable
5. Submit a pull request

## 📄 License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.

## 🙏 Acknowledgments

- **JosefNemec** - For the amazing Playnite platform
- **Apollo Team** - For the excellent game streaming solution
- **Sunshine Team** - For the open-source streaming alternative
- **Community** - For feedback and feature requests

## 📞 Support

### Getting Help
- **Issues** - Report bugs or request features on [GitHub Issues](https://github.com/your-username/ApolloSync/issues)
- **Discussions** - General questions and community support
- **Wiki** - Additional documentation and guides

### Reporting Bugs
When reporting issues, please include:
- Playnite version
- ApolloSync version
- Steps to reproduce
- Error messages or logs
- System information (OS, .NET version)

---

**Made with ❤️ for the Playnite community**
