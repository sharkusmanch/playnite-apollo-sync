using Playnite.SDK;
using Playnite.SDK.Data;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ApolloSync
{
    public enum NotificationMode
    {
        Always,
        OnUpdateOnly,
        Never
    }

    public class LabelOption
    {
        public string Name { get; set; }
        public Guid? Id { get; set; }
    }

    public class CompletionStatusOption
    {
        public string Name { get; set; }
        public CompletionStatus Status { get; set; }
        public bool IsSelected { get; set; }
    }

    public class ApolloSyncSettings : ObservableObject
    {
        private string _appsJsonPath = string.Empty;

        public string AppsJsonPath
        {
            get => _appsJsonPath;
            set => SetValue(ref _appsJsonPath, value);
        }

        private bool _syncOnSettingsUpdated = true;
        public bool SyncOnSettingsUpdated
        {
            get => _syncOnSettingsUpdated;
            set => SetValue(ref _syncOnSettingsUpdated, value);
        }

        private bool _syncOnLibraryUpdate = true;
        public bool SyncOnLibraryUpdate
        {
            get => _syncOnLibraryUpdate;
            set => SetValue(ref _syncOnLibraryUpdate, value);
        }

        private bool _syncOnStartup = false;
        public bool SyncOnStartup
        {
            get => _syncOnStartup;
            set => SetValue(ref _syncOnStartup, value);
        }

        private List<Guid> _pinnedGameIds = new List<Guid>();
        public List<Guid> PinnedGameIds
        {
            get => _pinnedGameIds;
            set => SetValue(ref _pinnedGameIds, value);
        }

        private List<Guid> _includedFilterPresetIds = new List<Guid>();
        public List<Guid> IncludedFilterPresetIds
        {
            get => _includedFilterPresetIds;
            set => SetValue(ref _includedFilterPresetIds, value);
        }

        private bool _showNotifications = true;
        public bool ShowNotifications
        {
            get => _showNotifications;
            set => SetValue(ref _showNotifications, value);
        }

        private NotificationMode _notificationMode = NotificationMode.Always;
        public NotificationMode NotificationMode
        {
            get => _notificationMode;
            set => SetValue(ref _notificationMode, value);
        }

        private Dictionary<Guid, Guid> _managedGameMappings = new Dictionary<Guid, Guid>();
        public Dictionary<Guid, Guid> ManagedGameMappings
        {
            get => _managedGameMappings;
            set => SetValue(ref _managedGameMappings, value);
        }

        // No non-serialized properties at this time.
    }

    public class ApolloSyncSettingsViewModel : ObservableObject, ISettings
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private readonly ApolloSync _plugin;
        private ApolloSyncSettings _editingClone;

        private ApolloSyncSettings _settings;
        public ApolloSyncSettings Settings
        {
            get => _settings;
            set
            {
                _settings = value;
                OnPropertyChanged();
            }
        }

        public List<FilterPreset> AvailableFilterPresets { get; private set; }

        public List<Game> GetManagedGames()
        {
            return _plugin.GetManagedGamesForSettings();
        }

        public void RemoveGamesFromManaged(List<Guid> gameIds)
        {
            _plugin.RemoveGamesFromManaged(gameIds);
        }

        public ApolloSyncSettingsViewModel(ApolloSync plugin)
        {
            // Injecting your plugin instance is required for Save/Load method because Playnite saves data to a location based on what plugin requested the operation.
            _plugin = plugin;

            // Load saved settings.
            var savedSettings = _plugin.LoadPluginSettings<ApolloSyncSettings>();

            // LoadPluginSettings returns null if no saved data is available.
            if (savedSettings != null)
            {
                Settings = savedSettings;
            }
            else
            {
                Settings = new ApolloSyncSettings();
            }

            // Filter presets are loaded in OnApplicationStarted via RefreshFilterPresets()
            // to avoid accessing PlayniteApi.Database before it is ready.
        }

        public void RefreshFilterPresets()
        {
            RefreshAvailableOptions();
        }

        private void RefreshAvailableOptions()
        {
            if (_plugin.PlayniteApi?.Database != null)
            {
                AvailableFilterPresets = _plugin.PlayniteApi.Database.FilterPresets.OrderBy(f => f.Name).ToList();
            }
        }

        public void BeginEdit()
        {
            // Code executed when settings view is opened and user starts editing values.
            _editingClone = Serialization.GetClone(Settings);
            RefreshAvailableOptions();
        }

        public void CancelEdit()
        {
            // Code executed when user decides to cancel any changes made since BeginEdit was called.
            // This method should revert any changes made to Option1 and Option2.
            Settings = _editingClone;
        }

        public void EndEdit()
        {
            // Code executed when user decides to confirm changes made since BeginEdit was called.
            // This method should save settings made to Option1 and Option2.
            _plugin.SavePluginSettings(Settings);

            // Trigger sync if enabled
            if (Settings.SyncOnSettingsUpdated)
            {
                _plugin.TriggerSyncOnSettingsUpdate();
            }
        }

        public bool VerifySettings(out List<string> errors)
        {
            // Code execute when user decides to confirm changes made since BeginEdit was called.
            // Executed before EndEdit is called and EndEdit is not called if false is returned.
            // List of errors is presented to user if verification fails.
            errors = new List<string>();
            if (!string.IsNullOrWhiteSpace(Settings.AppsJsonPath))
            {
                try
                {
                    var dir = System.IO.Path.GetDirectoryName(Settings.AppsJsonPath);
                    if (string.IsNullOrEmpty(dir) || (!System.IO.Directory.Exists(dir) && !System.IO.File.Exists(Settings.AppsJsonPath)))
                    {
                        errors.Add(ResourceProvider.GetString("LOC_ApolloSync_Settings_AppsJsonPath_Invalid"));
                    }
                }
                catch (Exception ex)
                {
                    logger.Warn(ex, "VerifySettings: path validation threw on invalid input; treating as invalid path");
                    errors.Add(ResourceProvider.GetString("LOC_ApolloSync_Settings_AppsJsonPath_Invalid"));
                }
            }
            // If empty, ConfigService will resolve defaults. No error.
            return errors.Count == 0;
        }

    }
}
