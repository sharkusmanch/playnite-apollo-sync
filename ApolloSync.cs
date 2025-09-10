using Playnite.SDK;
using Playnite.SDK.Events;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.IO;
using Playnite.SDK.Data;
using ApolloSync.Models;
using ApolloSync.Services;
using Newtonsoft.Json.Linq;

namespace ApolloSync
{
    public class ApolloSync : GenericPlugin
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private ApolloSyncSettingsViewModel settings { get; set; }

        private ManagedStore managedStore;
        private readonly IConfigService configService = new ConfigService();
        private readonly IManagedStoreService storeService = new ManagedStoreService();
        private readonly ISyncService syncService;

        public override Guid Id { get; } = Guid.Parse("f987343d-4168-4f44-9fb0-e3a21da314ad");

        public ApolloSync(IPlayniteAPI api) : base(api)
        {
            settings = new ApolloSyncSettingsViewModel(this);
            syncService = new SyncService(api);
            Properties = new GenericPluginProperties
            {
                HasSettings = true
            };
            LoadManagedStore();
        }

        #region Helper Methods
        private void ShowNotificationIfEnabled(NotificationMessage notification)
        {
            if (settings.Settings.ShowNotifications)
            {
                PlayniteApi.Notifications.Add(notification);
            }
        }

        private bool IsGameManuallyRemoved(Game game, JObject config)
        {
            // Manual removal means: it WAS managed but its entry is now missing from apps.json.
            // With identity UUIDs, presence is determined by uuid == game.Id.
            var apps = (JArray)(config["apps"] ?? new JArray());
            var presentInConfig = apps
                .OfType<JObject>()
                .Select(a => (string)a["uuid"])
                .Where(s => !string.IsNullOrEmpty(s) && Guid.TryParse(s, out _))
                .Any(s => Guid.Parse(s) == game.Id);

            var isManaged = managedStore.GameToUuid.ContainsKey(game.Id);

            if (isManaged && !presentInConfig)
            {
                logger.Debug($"Game {game.Name} appears to be manually removed (was managed but missing from apps.json)");
                return true;
            }

            return false;
        }
        #endregion

        public override void OnApplicationStarted(OnApplicationStartedEventArgs args)
        {
            // Sync managed store on startup to remove orphaned entries
            SyncManagedStore();

            // Perform sync on startup if enabled
            if (settings.Settings.SyncOnStartup)
            {
                logger.Info("Triggering sync due to application startup");
                Task.Run(() => SyncFilteredGamesWithProgress());
            }
        }

        public override void OnGameInstalled(OnGameInstalledEventArgs args)
        {
            // Check if the newly installed game meets our filter criteria
            var filteredGames = GetFilteredGames();
            if (filteredGames.Any(g => g.Id == args.Game.Id))
            {
                TryAddOrUpdateApp(args.Game);
            }
        }

        public override void OnGameStarted(OnGameStartedEventArgs args)
        {
            // Add code to be executed when game is started running.
        }

        public override void OnGameStarting(OnGameStartingEventArgs args)
        {
            // Add code to be executed when game is preparing to be started.
        }

        public override void OnGameStopped(OnGameStoppedEventArgs args)
        {
            // Add code to be executed when game is preparing to be started.
        }

        public override void OnGameUninstalled(OnGameUninstalledEventArgs args)
        {
            // Filter presets are now the source of truth for game management
            // Auto-removal based on install status has been removed
            logger.Debug($"Game uninstalled: {args.Game.Name} - use filter presets to control game management");
        }

        public override void OnApplicationStopped(OnApplicationStoppedEventArgs args)
        {
            // Add code to be executed when Playnite is shutting down.
        }

        public override void OnLibraryUpdated(OnLibraryUpdatedEventArgs args)
        {
            if (!settings.Settings.SyncOnLibraryUpdate)
            {
                return;
            }

            // Perform full sync on library update
            Task.Run(() => SyncFilteredGamesWithProgress());
        }

        public void TriggerSyncOnSettingsUpdate()
        {
            logger.Info("Triggering sync due to settings update");
            SyncFilteredGamesWithProgress();
        }

        public override ISettings GetSettings(bool firstRunSettings)
        {
            return settings;
        }

        public override UserControl GetSettingsView(bool firstRunSettings)
        {
            return new ApolloSyncSettingsView();
        }

        public override IEnumerable<MainMenuItem> GetMainMenuItems(GetMainMenuItemsArgs args)
        {
            return new List<MainMenuItem>
            {
                new MainMenuItem
                {
                    Description = ResourceProvider.GetString("LOC_ApolloSync_Menu_SyncAll"),
                    MenuSection = ResourceProvider.GetString("LOC_ApolloSync_MenuSection"),
                    Action = _ =>
                    {
                        Task.Run(() => SyncFilteredGamesWithProgress());
                    }
                }
            };
        }

        public override IEnumerable<GameMenuItem> GetGameMenuItems(GetGameMenuItemsArgs args)
        {
            var items = new List<GameMenuItem>();
            items.Add(new GameMenuItem
            {
                Description = ResourceProvider.GetString("LOC_ApolloSync_Menu_Export"),
                MenuSection = ResourceProvider.GetString("LOC_ApolloSync_MenuSection"),
                Action = _ =>
                {
                    ExportGamesWithFeedback(args.Games);
                }
            });

            items.Add(new GameMenuItem
            {
                Description = ResourceProvider.GetString("LOC_ApolloSync_Menu_Remove"),
                MenuSection = ResourceProvider.GetString("LOC_ApolloSync_MenuSection"),
                Action = _ =>
                {
                    RemoveGamesWithFeedback(args.Games);
                }
            });

            items.Add(new GameMenuItem
            {
                Description = ResourceProvider.GetString("LOC_ApolloSync_Menu_Pin"),
                MenuSection = ResourceProvider.GetString("LOC_ApolloSync_MenuSection"),
                Action = _ =>
                {
                    PinGames(args.Games);
                }
            });

            items.Add(new GameMenuItem
            {
                Description = ResourceProvider.GetString("LOC_ApolloSync_Menu_Unpin"),
                MenuSection = ResourceProvider.GetString("LOC_ApolloSync_MenuSection"),
                Action = _ =>
                {
                    UnpinGames(args.Games);
                }
            });

            items.Add(new GameMenuItem
            {
                Description = ResourceProvider.GetString("LOC_ApolloSync_Menu_SyncAll"),
                MenuSection = ResourceProvider.GetString("LOC_ApolloSync_MenuSection"),
                Action = _ =>
                {
                    Task.Run(() => SyncFilteredGamesWithProgress());
                }
            });
            return items;
        }

        #region Managed Store
        private void LoadManagedStore()
        {
            // Load managed store from plugin settings instead of separate file
            managedStore = new ManagedStore
            {
                GameToUuid = settings.Settings.ManagedGameMappings ?? new Dictionary<Guid, Guid>()
            };
            logger.Debug($"Loaded managed store from settings with {managedStore.GameToUuid.Count} entries");
        }

        public List<Game> GetManagedGamesForSettings()
        {
            try
            {
                // Ensure managed store is loaded
                if (managedStore == null)
                {
                    LoadManagedStore();
                }

                var managedGames = new List<Game>();

                if (managedStore?.GameToUuid != null)
                {
                    foreach (var gameId in managedStore.GameToUuid.Keys)
                    {
                        var game = PlayniteApi.Database.Games.Get(gameId);
                        if (game != null)
                        {
                            managedGames.Add(game);
                        }
                    }
                }

                return managedGames.OrderBy(g => g.Name).ToList();
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Error getting managed games for settings");
                return new List<Game>();
            }
        }

        public void RemoveGamesFromManaged(List<Guid> gameIds)
        {
            try
            {
                if (managedStore?.GameToUuid == null) return;

                foreach (var gameId in gameIds)
                {
                    managedStore.GameToUuid.Remove(gameId);
                }

                SaveManagedStore();
                logger.Info($"Removed {gameIds.Count} games from managed store");
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Error removing games from managed store");
            }
        }

        private void SaveManagedStore()
        {
            // Save managed store to plugin settings instead of separate file
            settings.Settings.ManagedGameMappings = managedStore.GameToUuid;
            SavePluginSettings(settings.Settings);
            logger.Debug($"Saved managed store to settings with {managedStore.GameToUuid.Count} entries");
        }

        private void SyncManagedStore()
        {
            logger.Debug("Starting managed store sync");

            try
            {
                var config = LoadAppsConfig();
                if (config == null)
                {
                    logger.Debug("Could not load apps config for managed store sync - skipping");
                    return;
                }

                var apps = (JArray)(config["apps"] ?? new JArray());
                var configUuids = new HashSet<Guid>();

                // Collect all UUIDs currently in the apps.json
                foreach (var app in apps.OfType<JObject>())
                {
                    var uuidStr = (string)app["uuid"];
                    if (!string.IsNullOrEmpty(uuidStr) && Guid.TryParse(uuidStr, out var uuid))
                    {
                        configUuids.Add(uuid);
                    }
                }

                // Find managed store entries that don't exist in apps.json
                var toRemove = managedStore.GameToUuid.Where(kvp => !configUuids.Contains(kvp.Value)).ToList();

                if (toRemove.Count > 0)
                {
                    logger.Info($"Syncing managed store: removing {toRemove.Count} orphaned entries");
                    foreach (var kvp in toRemove)
                    {
                        logger.Debug($"Removing orphaned managed store entry: Game {kvp.Key} -> UUID {kvp.Value}");
                        managedStore.GameToUuid.Remove(kvp.Key);
                    }
                    SaveManagedStore();
                    logger.Info("Managed store sync completed");
                }
                else
                {
                    logger.Debug("Managed store sync: no orphaned entries found");
                }
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Error during managed store sync");
            }
        }

        private void RemoveGamesFromManagedWindow(List<Guid> gameIds)
        {
            try
            {
                logger.Info($"Removing {gameIds.Count} games from managed window - using immediate write mode");

                // For individual removals from the UI, write immediately for each game
                int removedCount = 0;
                foreach (var gameId in gameIds)
                {
                    if (RemoveSingleGameImmediate(gameId))
                    {
                        removedCount++;
                    }
                }

                if (removedCount > 0)
                {
                    SyncManagedStore();
                    logger.Info($"Successfully removed {removedCount} games from Apollo/Sunshine with immediate writes");
                }
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Error removing games from managed window");
                throw; // Re-throw to let the UI handle it
            }
        }

        private bool RemoveSingleGameImmediate(Guid gameId)
        {
            try
            {
                var config = LoadAppsConfig();
                if (config == null)
                {
                    logger.Error("Failed to load apps configuration for single game removal");
                    return false;
                }

                var game = PlayniteApi.Database.Games.FirstOrDefault(g => g.Id == gameId);
                if (game != null)
                {
                    logger.Debug($"Removing game {game.Name} with immediate write");

                    // Remove from apps.json and managed store
                    if (TryRemoveAppBatch(game, config))
                    {
                        // Immediately save both apps.json and managed store
                        SaveAppsConfig(config);
                        SaveManagedStore();
                        logger.Debug($"Immediately saved removal of game: {game.Name}");
                        return true;
                    }
                    else
                    {
                        logger.Warn($"Failed to remove game from config: {game.Name}");
                        return false;
                    }
                }
                else
                {
                    // Game doesn't exist in Playnite anymore, remove from managed store directly
                    if (managedStore.GameToUuid.ContainsKey(gameId))
                    {
                        managedStore.GameToUuid.Remove(gameId);
                        SaveManagedStore();
                        logger.Debug($"Removed orphaned game ID from managed store: {gameId}");
                        return true;
                    }
                }

                return false;
            }
            catch (Exception ex)
            {
                logger.Error(ex, $"Error in RemoveSingleGameImmediate for game ID: {gameId}");
                return false;
            }
        }
        #endregion

        #region Game Filtering
        private List<Game> GetFilteredGames()
        {
            // Use filter presets with OR logic - game matches if it matches ANY selected preset
            if (settings.Settings.IncludedFilterPresetIds?.Count > 0)
            {
                var matchingGames = new HashSet<Game>();

                foreach (var presetId in settings.Settings.IncludedFilterPresetIds)
                {
                    var filterPreset = PlayniteApi.Database.FilterPresets
                        .FirstOrDefault(fp => fp.Id == presetId);

                    if (filterPreset?.Settings != null)
                    {
                        try
                        {
                            var presetGames = PlayniteApi.Database.GetFilteredGames(filterPreset.Settings);
                            foreach (var game in presetGames)
                            {
                                matchingGames.Add(game);
                            }
                            logger.Debug($"Filter preset '{filterPreset.Name}' matched {presetGames.Count()} games");
                        }
                        catch (Exception ex)
                        {
                            logger.Error(ex, $"Failed to apply filter preset: {filterPreset.Name}");
                        }
                    }
                }

                logger.Info($"Combined filter presets matched {matchingGames.Count} games");
                return matchingGames.ToList();
            }

            // No filter presets selected - return no games
            logger.Warn("No filter presets selected - no games will be filtered. Please select filter presets in settings.");
            return new List<Game>();
        }

        private bool GameMeetsCurrentFilters(Game game)
        {
            // Use filter presets with OR logic - game matches if it matches ANY selected preset
            if (settings.Settings.IncludedFilterPresetIds?.Count > 0)
            {
                foreach (var presetId in settings.Settings.IncludedFilterPresetIds)
                {
                    var filterPreset = PlayniteApi.Database.FilterPresets
                        .FirstOrDefault(fp => fp.Id == presetId);

                    if (filterPreset?.Settings != null)
                    {
                        try
                        {
                            if (PlayniteApi.Database.GetGameMatchesFilter(game, filterPreset.Settings))
                            {
                                return true; // Match found, return true immediately
                            }
                        }
                        catch (Exception ex)
                        {
                            logger.Error(ex, $"Failed to check game against filter preset: {filterPreset.Name}");
                        }
                    }
                }
            }

            // No filter presets selected or no matches found
            return false;


        }

        private int RemoveFilteredOutGames(JObject config)
        {
            var removedCount = 0;

            try
            {
                var apps = (JArray)(config["apps"] ?? new JArray());
                var appsToRemove = new List<JObject>();
                var managedGamesToRemove = new List<Guid>();

                // Check each managed game
                foreach (var gameEntry in managedStore.GameToUuid.ToList())
                {
                    var gameId = gameEntry.Key;
                    var appUuid = gameEntry.Value;

                    // Get the game from database
                    var game = PlayniteApi.Database.Games.Get(gameId);
                    if (game == null)
                    {
                        // Game no longer exists in database, remove it
                        logger.Info($"Removing game {gameId} - no longer exists in database");
                        managedGamesToRemove.Add(gameId);

                        // Find and mark app for removal
                        var appToRemove = apps.OfType<JObject>().FirstOrDefault(app =>
                            Guid.TryParse((string)app["uuid"], out var uuid) && uuid == appUuid);
                        if (appToRemove != null)
                        {
                            appsToRemove.Add(appToRemove);
                        }
                        continue;
                    }

                    // Check if game is pinned
                    if (settings.Settings.PinnedGameIds.Contains(gameId))
                    {
                        logger.Debug($"Game {game.Name} is pinned, keeping in apps.json even if it doesn't meet filters");
                        continue;
                    }

                    // Check if game still meets current filters
                    if (!GameMeetsCurrentFilters(game))
                    {
                        logger.Info($"Removing game {game.Name} - no longer meets filters (not pinned)");
                        managedGamesToRemove.Add(gameId);

                        // Find and mark app for removal
                        var appToRemove = apps.OfType<JObject>().FirstOrDefault(app =>
                            Guid.TryParse((string)app["uuid"], out var uuid) && uuid == appUuid);
                        if (appToRemove != null)
                        {
                            appsToRemove.Add(appToRemove);
                        }
                    }
                }

                // Remove apps from config
                foreach (var appToRemove in appsToRemove)
                {
                    apps.Remove(appToRemove);
                    removedCount++;
                }

                // Remove from managed store
                foreach (var gameId in managedGamesToRemove)
                {
                    managedStore.GameToUuid.Remove(gameId);
                }

                logger.Info($"RemoveFilteredOutGames completed: removed {removedCount} games from apps.json");
                return removedCount;
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Error in RemoveFilteredOutGames");
                return removedCount;
            }
        }
        #endregion

        #region User Operations with Feedback
        private void SyncFilteredGamesWithProgress()
        {
            logger.Info("Starting sync filtered games operation");

            var filteredGames = GetFilteredGames();
            if (filteredGames.Count == 0)
            {
                ShowNotificationIfEnabled(new NotificationMessage(
                    "apollosync-no-games",
                    "No games match the selected filter presets. Please check your filter preset configuration.",
                    NotificationType.Error));
                return;
            }

            logger.Info($"Found {filteredGames.Count} games matching filter presets to sync");

            var progressOptions = new GlobalProgressOptions(
                ResourceProvider.GetString("LOC_ApolloSync_Progress_SyncAll_Title"))
            {
                IsIndeterminate = false,
                Cancelable = true
            };

            var syncResults = new { successCount = 0, failureCount = 0, errors = new List<string>(), removedCount = 0 };

            PlayniteApi.Dialogs.ActivateGlobalProgress((progressArgs) =>
            {
                var localSuccess = 0;
                var localFailure = 0;
                var localErrors = new List<string>();
                var localRemoved = 0;

                // Load config once at the beginning
                var config = LoadAppsConfig();
                if (config == null)
                {
                    localErrors.Add("Failed to load apps.json configuration");
                    syncResults = new { successCount = 0, failureCount = filteredGames.Count, errors = localErrors, removedCount = 0 };
                    return;
                }

                // Phase 0: Sync managed store first to clean up any manually removed games
                progressArgs.Text = "Syncing managed store...";
                SyncManagedStore();

                logger.Info($"Starting batch sync operation with {filteredGames.Count} games");

                // Phase 1: Remove games that no longer meet filters (unless pinned)
                progressArgs.Text = "Checking managed games for filter compliance...";
                var removedGames = RemoveFilteredOutGames(config);
                localRemoved = removedGames;
                logger.Info($"Removed {removedGames} games that no longer meet filters");

                // Phase 2: Add/update games that meet current filters
                for (int i = 0; i < filteredGames.Count; i++)
                {
                    if (progressArgs.CancelToken.IsCancellationRequested)
                    {
                        logger.Info("Sync all operation cancelled by user");
                        break;
                    }

                    var game = filteredGames[i];
                    progressArgs.Text = string.Format(
                        ResourceProvider.GetString("LOC_ApolloSync_Progress_SyncAll_Current"),
                        game.Name);
                    progressArgs.CurrentProgressValue = i;
                    progressArgs.ProgressMaxValue = filteredGames.Count;

                    logger.Debug($"Processing game: {game.Name} (ID: {game.Id})");

                    try
                    {
                        // Check if this game was manually removed and should not be re-added
                        if (IsGameManuallyRemoved(game, config))
                        {
                            logger.Info($"Skipping game {game.Name} - appears to have been manually removed from Apollo management");
                            continue;
                        }

                        // Use batch operation that doesn't save to disk
                        if (TryAddOrUpdateAppBatch(game, config))
                        {
                            localSuccess++;
                            logger.Debug($"Successfully processed: {game.Name}");
                        }
                        else
                        {
                            localFailure++;
                            var error = $"Failed to process: {game.Name}";
                            localErrors.Add(error);
                            logger.Warn(error);
                        }
                    }
                    catch (Exception ex)
                    {
                        localFailure++;
                        var error = $"Error processing {game.Name}: {ex.Message}";
                        localErrors.Add(error);
                        logger.Error(ex, error);
                    }
                }

                // Save everything once at the end
                if (localSuccess > 0 || localRemoved > 0)
                {
                    logger.Info($"Saving batch changes to disk. Processed {localSuccess} games successfully, removed {localRemoved} games.");
                    try
                    {
                        SaveAppsConfig(config);
                        SaveManagedStore();
                        SyncManagedStore(); // Ensure managed store stays in sync
                        logger.Info("Batch save completed successfully");
                    }
                    catch (Exception ex)
                    {
                        logger.Error(ex, "Failed to save batch changes");
                        localErrors.Add("Failed to save changes to disk");
                    }
                }

                syncResults = new { successCount = localSuccess, failureCount = localFailure, errors = localErrors, removedCount = localRemoved };
                logger.Info($"Sync all completed. Success: {localSuccess}, Failed: {localFailure}, Removed: {localRemoved}");
            }, progressOptions);

            // Show completion notification
            var message = string.Format(
                ResourceProvider.GetString("LOC_ApolloSync_Message_SyncAll_Complete"),
                syncResults.successCount, syncResults.failureCount);

            if (syncResults.removedCount > 0)
            {
                message += $" Removed {syncResults.removedCount} filtered out games.";
            }

            // Determine notification type based on results
            var notificationType = syncResults.failureCount == 0 && !syncResults.errors.Any()
                ? NotificationType.Info
                : NotificationType.Error;

            // Add notification with click action for errors
            var notification = new NotificationMessage(
                "apollosync-sync-complete",
                message,
                notificationType);

            // If there are errors, allow clicking to view details
            if (syncResults.errors.Any())
            {
                var detailMessage = message + Environment.NewLine + Environment.NewLine + "Errors:" + Environment.NewLine +
                                   string.Join(Environment.NewLine, syncResults.errors);

                // Override the message to indicate clickable
                notification = new NotificationMessage(
                    "apollosync-sync-complete",
                    message + " (Click to view error details)",
                    notificationType);
            }

            ShowNotificationIfEnabled(notification);
        }

        private void ExportGamesWithFeedback(IEnumerable<Game> games)
        {
            logger.Info("Starting export games operation");

            var gameList = games.ToList();
            if (gameList.Count == 0)
            {
                return;
            }

            logger.Info($"Exporting {gameList.Count} games");

            // Pin games by default when manually exporting
            var pinnedCount = 0;
            foreach (var game in gameList)
            {
                if (!settings.Settings.PinnedGameIds.Contains(game.Id))
                {
                    settings.Settings.PinnedGameIds.Add(game.Id);
                    pinnedCount++;
                }
            }

            if (pinnedCount > 0)
            {
                SavePluginSettings(settings.Settings);
                logger.Info($"Auto-pinned {pinnedCount} manually exported games");
            }

            var successCount = 0;
            var failureCount = 0;
            var errors = new List<string>();

            // Load config once at the beginning
            var config = LoadAppsConfig();
            if (config == null)
            {
                errors.Add("Failed to load apps.json configuration");
                failureCount = gameList.Count;
            }
            else
            {
                foreach (var game in gameList)
                {
                    logger.Debug($"Exporting game: {game.Name} (ID: {game.Id})");

                    try
                    {
                        // Use batch operation that doesn't save to disk
                        if (TryAddOrUpdateAppBatch(game, config))
                        {
                            successCount++;
                            logger.Debug($"Successfully processed: {game.Name}");
                        }
                        else
                        {
                            failureCount++;
                            var error = $"Failed to export: {game.Name}";
                            errors.Add(error);
                            logger.Warn(error);
                        }
                    }
                    catch (Exception ex)
                    {
                        failureCount++;
                        var error = $"Error exporting {game.Name}: {ex.Message}";
                        errors.Add(error);
                        logger.Error(ex, error);
                    }
                }                // Save everything once at the end
                if (successCount > 0)
                {
                    logger.Info($"Saving batch export changes to disk. Processed {successCount} games successfully.");
                    try
                    {
                        SaveAppsConfig(config);
                        SaveManagedStore();
                        SyncManagedStore(); // Ensure managed store stays in sync
                        logger.Info("Batch export save completed successfully");
                    }
                    catch (Exception ex)
                    {
                        logger.Error(ex, "Failed to save batch export changes");
                        errors.Add("Failed to save changes to disk");
                    }
                }
            }

            logger.Info($"Export completed. Success: {successCount}, Failed: {failureCount}");

            // Show completion notification
            var message = string.Format(
                ResourceProvider.GetString("LOC_ApolloSync_Message_Export_Complete"),
                successCount, failureCount);

            var notificationType = failureCount == 0 && !errors.Any()
                ? NotificationType.Info
                : NotificationType.Error;

            if (errors.Any())
            {
                message += $" ({errors.Count} errors occurred)";
            }

            ShowNotificationIfEnabled(new NotificationMessage(
                "apollosync-export-complete",
                message,
                notificationType));
        }

        private void RemoveGamesWithFeedback(IEnumerable<Game> games)
        {
            logger.Info("Starting remove games operation");

            var gameList = games.ToList();
            if (gameList.Count == 0)
            {
                return;
            }

            logger.Info($"Removing {gameList.Count} games");

            var successCount = 0;
            var failureCount = 0;
            var errors = new List<string>();

            // Load config once at the beginning
            var config = LoadAppsConfig();
            if (config == null)
            {
                errors.Add("Failed to load apps.json configuration");
                failureCount = gameList.Count;
            }
            else
            {
                foreach (var game in gameList)
                {
                    logger.Debug($"Removing game: {game.Name} (ID: {game.Id})");

                    try
                    {
                        // Use batch operation that doesn't save to disk
                        if (TryRemoveAppBatch(game, config))
                        {
                            successCount++;
                            logger.Debug($"Successfully processed removal: {game.Name}");
                        }
                        else
                        {
                            failureCount++;
                            var error = $"Failed to remove: {game.Name}";
                            errors.Add(error);
                            logger.Warn(error);
                        }
                    }
                    catch (Exception ex)
                    {
                        failureCount++;
                        var error = $"Error removing {game.Name}: {ex.Message}";
                        errors.Add(error);
                        logger.Error(ex, error);
                    }
                }

                // Save everything once at the end
                if (successCount > 0)
                {
                    logger.Info($"Saving batch removal changes to disk. Processed {successCount} games successfully.");
                    try
                    {
                        SaveAppsConfig(config);
                        SaveManagedStore();
                        SyncManagedStore(); // Ensure managed store stays in sync
                        logger.Info("Batch removal save completed successfully");
                    }
                    catch (Exception ex)
                    {
                        logger.Error(ex, "Failed to save batch removal changes");
                        errors.Add("Failed to save changes to disk");
                    }
                }
            }

            logger.Info($"Remove completed. Success: {successCount}, Failed: {failureCount}");

            // Show completion notification
            var message = string.Format(
                ResourceProvider.GetString("LOC_ApolloSync_Message_Remove_Complete"),
                successCount, failureCount);

            var notificationType = failureCount == 0 && !errors.Any()
                ? NotificationType.Info
                : NotificationType.Error;

            if (errors.Any())
            {
                message += $" ({errors.Count} errors occurred)";
            }

            ShowNotificationIfEnabled(new NotificationMessage(
                "apollosync-remove-complete",
                message,
                notificationType));
        }

        private void PinGames(IEnumerable<Game> games)
        {
            var gameList = games.ToList();
            if (gameList.Count == 0)
            {
                return;
            }

            logger.Info($"Pinning {gameList.Count} games");

            var pinnedCount = 0;
            foreach (var game in gameList)
            {
                if (!settings.Settings.PinnedGameIds.Contains(game.Id))
                {
                    settings.Settings.PinnedGameIds.Add(game.Id);
                    pinnedCount++;
                    logger.Debug($"Pinned game: {game.Name} (ID: {game.Id})");
                }
            }

            if (pinnedCount > 0)
            {
                SavePluginSettings(settings.Settings);
                ShowNotificationIfEnabled(new NotificationMessage(
                    "apollosync-pin-complete",
                    $"Pinned {pinnedCount} games. Pinned games will not be automatically removed when filters change or games are uninstalled.",
                    NotificationType.Info));
            }
            else
            {
                ShowNotificationIfEnabled(new NotificationMessage(
                    "apollosync-pin-info",
                    "All selected games are already pinned.",
                    NotificationType.Info));
            }
        }

        private void UnpinGames(IEnumerable<Game> games)
        {
            var gameList = games.ToList();
            if (gameList.Count == 0)
            {
                return;
            }

            logger.Info($"Unpinning {gameList.Count} games");

            var unpinnedCount = 0;
            foreach (var game in gameList)
            {
                if (settings.Settings.PinnedGameIds.Contains(game.Id))
                {
                    settings.Settings.PinnedGameIds.Remove(game.Id);
                    unpinnedCount++;
                    logger.Debug($"Unpinned game: {game.Name} (ID: {game.Id})");
                }
            }

            if (unpinnedCount > 0)
            {
                SavePluginSettings(settings.Settings);
                ShowNotificationIfEnabled(new NotificationMessage(
                    "apollosync-unpin-complete",
                    $"Unpinned {unpinnedCount} games. These games may now be automatically removed based on your filter settings.",
                    NotificationType.Info));
            }
            else
            {
                ShowNotificationIfEnabled(new NotificationMessage(
                    "apollosync-unpin-info",
                    "None of the selected games were pinned.",
                    NotificationType.Info));
            }
        }
        #endregion

        #region Apps.json operations
        private bool TryAddOrUpdateApp(Game game)
        {
            if (game == null)
            {
                logger.Debug("TryAddOrUpdateApp called with null game");
                return false;
            }

            logger.Debug($"TryAddOrUpdateApp called for game: {game.Name} (ID: {game.Id})");

            try
            {
                var config = LoadAppsConfig();
                if (config == null)
                {
                    logger.Error("Failed to load apps config - config is null");
                    return false;
                }

                logger.Debug($"Loaded config has {((JArray)config["apps"])?.Count ?? 0} apps at start");
                logger.Debug($"Attempting to add/update app for game: {game.Name}");
                var ok = syncService.AddOrUpdate(config, managedStore, game);

                if (ok)
                {
                    logger.Debug($"Successfully added/updated app for game: {game.Name}");
                    logger.Debug($"Config before save has {((JArray)config["apps"])?.Count ?? 0} apps");
                    SaveAppsConfig(config);
                    SaveManagedStore();
                    SyncManagedStore(); // Ensure managed store stays in sync
                    logger.Debug($"Saved config and managed store for game: {game.Name}");
                }
                else
                {
                    logger.Warn($"SyncService.AddOrUpdate returned false for game: {game.Name}");
                }

                return ok;
            }
            catch (Exception ex)
            {
                logger.Error(ex, $"Exception occurred while trying to add/update app for game: {game.Name}");
                return false;
            }
        }

        /// <summary>
        /// Batch version that doesn't save to disk - for use in bulk operations
        /// </summary>
        private bool TryAddOrUpdateAppBatch(Game game, JObject config)
        {
            if (game == null)
            {
                logger.Debug("TryAddOrUpdateAppBatch called with null game");
                return false;
            }

            if (config == null)
            {
                logger.Debug("TryAddOrUpdateAppBatch called with null config");
                return false;
            }

            logger.Debug($"TryAddOrUpdateAppBatch called for game: {game.Name} (ID: {game.Id})");

            try
            {
                var ok = syncService.AddOrUpdate(config, managedStore, game);
                if (ok)
                {
                    logger.Debug($"Successfully processed app for game: {game.Name}");
                    return true;
                }
                else
                {
                    logger.Debug($"Failed to process app for game: {game.Name}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                logger.Error(ex, $"Exception in TryAddOrUpdateAppBatch for game: {game?.Name}");
                return false;
            }
        }

        private bool TryRemoveApp(Game game)
        {
            if (game == null)
            {
                logger.Debug("TryRemoveApp called with null game");
                return false;
            }

            logger.Debug($"TryRemoveApp called for game: {game.Name} (ID: {game.Id})");

            try
            {
                var config = LoadAppsConfig();
                if (config == null)
                {
                    logger.Error("Failed to load apps config - config is null");
                    return false;
                }

                logger.Debug($"Attempting to remove app for game: {game.Name}");
                var ok = syncService.Remove(config, managedStore, game);

                if (ok)
                {
                    logger.Debug($"Successfully removed app for game: {game.Name}");
                    SaveAppsConfig(config);
                    SaveManagedStore();
                    SyncManagedStore(); // Ensure managed store stays in sync
                    logger.Debug($"Saved config and managed store after removing game: {game.Name}");
                }
                else
                {
                    logger.Warn($"SyncService.Remove returned false for game: {game.Name}");
                }

                return ok;
            }
            catch (Exception ex)
            {
                logger.Error(ex, $"Exception occurred while trying to remove app for game: {game.Name}");
                return false;
            }
        }

        /// <summary>
        /// Batch version that doesn't save to disk - for use in bulk operations
        /// </summary>
        private bool TryRemoveAppBatch(Game game, JObject config)
        {
            if (game == null)
            {
                logger.Debug("TryRemoveAppBatch called with null game");
                return false;
            }

            if (config == null)
            {
                logger.Debug("TryRemoveAppBatch called with null config");
                return false;
            }

            logger.Debug($"TryRemoveAppBatch called for game: {game.Name} (ID: {game.Id})");

            try
            {
                var ok = syncService.Remove(config, managedStore, game);
                if (ok)
                {
                    logger.Debug($"Successfully processed removal for game: {game.Name}");
                    return true;
                }
                else
                {
                    logger.Debug($"Failed to process removal for game: {game.Name}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                logger.Error(ex, $"Exception in TryRemoveAppBatch for game: {game?.Name}");
                return false;
            }
        }

        private JObject LoadAppsConfig()
        {
            try
            {
                var path = settings.Settings.AppsJsonPath;
                logger.Debug($"Loading apps config from path: {path}");
                var config = configService.Load(path);

                if (config == null)
                {
                    logger.Error($"Failed to load apps config from path: {path}");
                }
                else
                {
                    logger.Debug("Successfully loaded apps config");
                }

                return config;
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Exception occurred while loading apps config");
                return null;
            }
        }

        private void SaveAppsConfig(JObject config)
        {
            try
            {
                var path = settings.Settings.AppsJsonPath;
                logger.Debug($"Saving apps config to path: {path}");
                configService.Save(path, config);
                logger.Debug("Successfully saved apps config");
            }
            catch (Exception ex)
            {
                logger.Error(ex, $"Exception occurred while saving apps config");
            }
        }
        #endregion
    }
}
