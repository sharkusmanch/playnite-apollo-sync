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
        private string appsJsonPath = string.Empty;

        public string AppsJsonPath
        {
            get => appsJsonPath;
            set => SetValue(ref appsJsonPath, value);
        }

        private bool syncOnSettingsUpdated = true;
        public bool SyncOnSettingsUpdated
        {
            get => syncOnSettingsUpdated;
            set => SetValue(ref syncOnSettingsUpdated, value);
        }

        private bool syncOnLibraryUpdate = true;
        public bool SyncOnLibraryUpdate
        {
            get => syncOnLibraryUpdate;
            set => SetValue(ref syncOnLibraryUpdate, value);
        }

        private bool syncOnStartup = false;
        public bool SyncOnStartup
        {
            get => syncOnStartup;
            set => SetValue(ref syncOnStartup, value);
        }

        private List<Guid> pinnedGameIds = new List<Guid>();
        public List<Guid> PinnedGameIds
        {
            get => pinnedGameIds;
            set => SetValue(ref pinnedGameIds, value);
        }

        private List<Guid> includedFilterPresetIds = new List<Guid>();
        public List<Guid> IncludedFilterPresetIds
        {
            get => includedFilterPresetIds;
            set => SetValue(ref includedFilterPresetIds, value);
        }

        private bool showNotifications = true;
        public bool ShowNotifications
        {
            get => showNotifications;
            set => SetValue(ref showNotifications, value);
        }

        private Dictionary<Guid, Guid> managedGameMappings = new Dictionary<Guid, Guid>();
        public Dictionary<Guid, Guid> ManagedGameMappings
        {
            get => managedGameMappings;
            set => SetValue(ref managedGameMappings, value);
        }

        // No non-serialized properties at this time.
    }

    public class ApolloSyncSettingsViewModel : ObservableObject, ISettings
    {
        private readonly ApolloSync plugin;
        private ApolloSyncSettings editingClone { get; set; }

        private ApolloSyncSettings settings;
        public ApolloSyncSettings Settings
        {
            get => settings;
            set
            {
                settings = value;
                OnPropertyChanged();
            }
        }

        public List<FilterPreset> AvailableFilterPresets { get; private set; }

        public List<Game> GetManagedGames()
        {
            return plugin.GetManagedGamesForSettings();
        }

        public void RemoveGamesFromManaged(List<Guid> gameIds)
        {
            plugin.RemoveGamesFromManaged(gameIds);
        }

        public ApolloSyncSettingsViewModel(ApolloSync plugin)
        {
            // Injecting your plugin instance is required for Save/Load method because Playnite saves data to a location based on what plugin requested the operation.
            this.plugin = plugin;

            // Load saved settings.
            var savedSettings = plugin.LoadPluginSettings<ApolloSyncSettings>();

            // LoadPluginSettings returns null if no saved data is available.
            if (savedSettings != null)
            {
                Settings = savedSettings;
            }
            else
            {
                Settings = new ApolloSyncSettings();
            }

            // Initialize available filter presets
            RefreshAvailableOptions();
        }

        public void RefreshFilterPresets()
        {
            RefreshAvailableOptions();
        }

        private void RefreshAvailableOptions()
        {
            if (plugin.PlayniteApi?.Database != null)
            {
                AvailableFilterPresets = plugin.PlayniteApi.Database.FilterPresets.OrderBy(f => f.Name).ToList();
            }
        }

        public void BeginEdit()
        {
            // Code executed when settings view is opened and user starts editing values.
            editingClone = Serialization.GetClone(Settings);
            RefreshAvailableOptions();
        }

        public void CancelEdit()
        {
            // Code executed when user decides to cancel any changes made since BeginEdit was called.
            // This method should revert any changes made to Option1 and Option2.
            Settings = editingClone;
        }

        public void EndEdit()
        {
            // Code executed when user decides to confirm changes made since BeginEdit was called.
            // This method should save settings made to Option1 and Option2.
            plugin.SavePluginSettings(Settings);

            // Trigger sync if enabled
            if (Settings.SyncOnSettingsUpdated)
            {
                Task.Run(() => plugin.TriggerSyncOnSettingsUpdate());
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
                catch
                {
                    errors.Add(ResourceProvider.GetString("LOC_ApolloSync_Settings_AppsJsonPath_Invalid"));
                }
            }
            // If empty, ConfigService will resolve defaults. No error.
            return errors.Count == 0;
        }

    }
}
