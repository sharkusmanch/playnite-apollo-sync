using Playnite.SDK;
using Playnite.SDK.Events;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.IO;
using Playnite.SDK.Data;
using System.Windows;
using ApolloSync.Models;
using ApolloSync.Services;
using Newtonsoft.Json.Linq;

namespace ApolloSync
{
    public class ApolloSync : GenericPlugin
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private ApolloSyncSettingsViewModel _settings;

        private ManagedStore _managedStore;
        private readonly IConfigService configService = new ConfigService();
        private readonly IManagedStoreService storeService = new ManagedStoreService();
        private readonly ISyncService syncService;
        private readonly object _configLock = new object();

        public override Guid Id { get; } = Guid.Parse("f987343d-4168-4f44-9fb0-e3a21da314ad");

        public ApolloSync(IPlayniteAPI api) : base(api)
        {
            _settings = new ApolloSyncSettingsViewModel(this);
            syncService = new SyncService(api);
            Properties = new GenericPluginProperties
            {
                HasSettings = true
            };
            LoadManagedStore();
        }

        #region Helper Methods
        private void ShowNotificationIfEnabled(NotificationMessage notification, bool isUpdateOperation = false)
        {
            var mode = _settings.Settings.NotificationMode;

            // For backward compatibility, check legacy ShowNotifications setting
            if (!_settings.Settings.ShowNotifications)
            {
                mode = NotificationMode.Never;
            }

            switch (mode)
            {
                case NotificationMode.Always:
                    PlayniteApi.Notifications.Add(notification);
                    break;
                case NotificationMode.OnUpdateOnly:
                    if (isUpdateOperation)
                    {
                        PlayniteApi.Notifications.Add(notification);
                    }
                    break;
                case NotificationMode.Never:
                    // Don't show notifications
                    break;
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

            var isManaged = _managedStore.GameToUuid.ContainsKey(game.Id);

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
            // Delete any stale lock files left by a previous crash. Without this a
            // PowerShell wrapper that survived the crash would poll indefinitely,
            // keeping a stream open after Playnite restarts.
            foreach (var stale in Directory.EnumerateFiles(Path.GetTempPath(), "apollosync-*.lock"))
            {
                try { File.Delete(stale); } catch { /* Non-fatal: stale lock deletion failure on startup is ignored */ }
            }

            // Subscribe to game metadata changes (e.g., cover image updates)
            PlayniteApi.Database.Games.ItemUpdated += Games_ItemUpdated;

            // Refresh filter presets now that the database is ready
            _settings.RefreshFilterPresets();

            // Sync managed store on startup to remove orphaned entries
            SyncManagedStore();

            // Perform sync on startup if enabled
            if (_settings.Settings.SyncOnStartup)
            {
                logger.Info("Triggering sync due to application startup");
                SyncFilteredGamesWithProgress();
            }
        }

        public override void OnGameInstalled(OnGameInstalledEventArgs args)
        {
            // Skip if a full sync is already running — it will process this game
            if (_syncRunning == 1)
            {
                logger.Debug($"Skipping OnGameInstalled for {args.Game.Name} — sync in progress");
                return;
            }

            // Check if the newly installed game meets our filter criteria
            var filteredGames = GetFilteredGames();
            if (filteredGames.Any(g => g.Id == args.Game.Id))
            {
                lock (_configLock)
                {
                    TryAddOrUpdateApp(args.Game);
                }
            }
        }

        public override void OnGameStarted(OnGameStartedEventArgs args)
        {
            if (!_managedStore.GameToUuid.ContainsKey(args.Game.Id))
                return;

            var lockPath = SyncService.GetLockFilePath(args.Game.Id);
            try
            {
                // FileMode.Create overwrites a stale lock from a crashed previous session.
                using (new FileStream(lockPath, FileMode.Create, FileAccess.Write, FileShare.None)) { }
                logger.Debug($"Created session lock for managed game '{args.Game.Name}': {lockPath}");
            }
            catch (Exception ex)
            {
                logger.Error(ex, $"Failed to create session lock for '{args.Game.Name}'");
            }
        }

        public override void OnGameStarting(OnGameStartingEventArgs args)
        {
            // Add code to be executed when game is preparing to be started.
        }

        public override void OnGameStopped(OnGameStoppedEventArgs args)
        {
            if (!_managedStore.GameToUuid.ContainsKey(args.Game.Id))
                return;

            var lockPath = SyncService.GetLockFilePath(args.Game.Id);
            try
            {
                if (File.Exists(lockPath))
                {
                    File.Delete(lockPath);
                    logger.Debug($"Deleted session lock for managed game '{args.Game.Name}': {lockPath}");
                }
            }
            catch (Exception ex)
            {
                logger.Error(ex, $"Failed to delete session lock for '{args.Game.Name}'");
            }
        }

        public override void OnGameUninstalled(OnGameUninstalledEventArgs args)
        {
            // Filter presets are now the source of truth for game management
            // Auto-removal based on install status has been removed
            logger.Debug($"Game uninstalled: {args.Game.Name} - use filter presets to control game management");
        }

        public override void OnApplicationStopped(OnApplicationStoppedEventArgs args)
        {
            PlayniteApi.Database.Games.ItemUpdated -= Games_ItemUpdated;
            CancelSync();
            try
            {
                var completed = _syncTask?.Wait(TimeSpan.FromSeconds(5)) ?? true;
                if (!completed)
                    logger.Warn("Sync task did not complete within 5 s during shutdown; proceeding anyway");
            }
            catch (AggregateException) { }

            // Clean up any leftover lock files — guards against Playnite crashing mid-session
            // leaving a lock that would keep a stream open indefinitely.
            foreach (var gameId in _managedStore.GameToUuid.Keys)
            {
                var lockPath = SyncService.GetLockFilePath(gameId);
                try { if (File.Exists(lockPath)) File.Delete(lockPath); } catch { }
            }
        }

        private void Games_ItemUpdated(object sender, ItemUpdatedEventArgs<Game> args)
        {
            // Skip if a full sync is already running
            if (_syncRunning == 1) return;

            foreach (var update in args.UpdatedItems)
            {
                if (update.OldData.CoverImage != update.NewData.CoverImage
                    && GameMeetsCurrentFilters(update.NewData))
                {
                    logger.Info($"Cover image changed for game: {update.NewData.Name}");
                    lock (_configLock)
                    {
                        TryAddOrUpdateApp(update.NewData);
                    }
                }
            }
        }

        public override void OnLibraryUpdated(OnLibraryUpdatedEventArgs args)
        {
            if (!_settings.Settings.SyncOnLibraryUpdate)
            {
                return;
            }

            // Perform full sync on library update
            SyncFilteredGamesWithProgress();
        }

        public void TriggerSyncOnSettingsUpdate()
        {
            logger.Info("Triggering sync due to settings update");
            SyncFilteredGamesWithProgress();
        }

        public override ISettings GetSettings(bool firstRunSettings)
        {
            return _settings;
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
                    MenuSection = "@" + ResourceProvider.GetString("LOC_ApolloSync_MenuSection"),
                    Action = _ => SyncFilteredGamesWithProgress()
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
                Action = _ => SyncFilteredGamesWithProgress()
            });
            return items;
        }

        #region Managed Store
        private void LoadManagedStore()
        {
            // Load managed store from plugin settings instead of separate file
            _managedStore = new ManagedStore
            {
                GameToUuid = new System.Collections.Concurrent.ConcurrentDictionary<Guid, Guid>(
                    _settings.Settings.ManagedGameMappings ?? new Dictionary<Guid, Guid>())
            };
            logger.Debug($"Loaded managed store from settings with {_managedStore.GameToUuid.Count} entries");
        }

        public List<Game> GetManagedGamesForSettings()
        {
            try
            {
                // Ensure managed store is loaded
                if (_managedStore == null)
                {
                    LoadManagedStore();
                }

                var managedGames = new List<Game>();

                if (_managedStore?.GameToUuid != null)
                {
                    foreach (var gameId in _managedStore.GameToUuid.Keys)
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
                if (_managedStore?.GameToUuid == null) return;

                lock (_configLock)
                {
                    foreach (var gameId in gameIds)
                    {
                        Guid removed;
                        _managedStore.GameToUuid.TryRemove(gameId, out removed);
                    }

                    SaveManagedStore();
                }
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
            _settings.Settings.ManagedGameMappings = new Dictionary<Guid, Guid>(_managedStore.GameToUuid);
            SavePluginSettings(_settings.Settings);
            logger.Debug($"Saved managed store to settings with {_managedStore.GameToUuid.Count} entries");
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
                var toRemove = _managedStore.GameToUuid.Where(kvp => !configUuids.Contains(kvp.Value)).ToList();

                if (toRemove.Count > 0)
                {
                    logger.Info($"Syncing managed store: removing {toRemove.Count} orphaned entries");
                    foreach (var kvp in toRemove)
                    {
                        logger.Debug($"Removing orphaned managed store entry: Game {kvp.Key} -> UUID {kvp.Value}");
                        Guid removedUuid;
                        _managedStore.GameToUuid.TryRemove(kvp.Key, out removedUuid);
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

        #endregion

        #region Game Filtering
        private List<Game> GetFilteredGames()
        {
            // Use filter presets with OR logic - game matches if it matches ANY selected preset
            if (_settings.Settings.IncludedFilterPresetIds?.Count > 0)
            {
                var matchingGames = new HashSet<Game>();

                foreach (var presetId in _settings.Settings.IncludedFilterPresetIds)
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
            logger.Warn("No filter presets selected - no games will be filtered. Please select filter presets in _settings.");
            return new List<Game>();
        }

        private bool GameMeetsCurrentFilters(Game game)
        {
            // Use filter presets with OR logic - game matches if it matches ANY selected preset
            if (_settings.Settings.IncludedFilterPresetIds?.Count > 0)
            {
                foreach (var presetId in _settings.Settings.IncludedFilterPresetIds)
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

        private int RemoveFilteredOutGames(JObject config, HashSet<Guid> pinnedGameIds = null)
        {
            var removedCount = 0;
            var pinned = pinnedGameIds ?? new HashSet<Guid>(_settings.Settings.PinnedGameIds);

            try
            {
                var apps = (JArray)(config["apps"] ?? new JArray());
                var appsToRemove = new List<JObject>();
                var managedGamesToRemove = new List<Guid>();

                // Check each managed game
                foreach (var gameEntry in _managedStore.GameToUuid.ToList())
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
                    if (pinned.Contains(gameId))
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
                    Guid removedId;
                    _managedStore.GameToUuid.TryRemove(gameId, out removedId);
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
        private volatile int _syncRunning;
        private CancellationTokenSource _syncCts;
        private Task _syncTask;

        private void SyncFilteredGamesWithProgress()
        {
            // Prevent overlapping syncs
            if (Interlocked.CompareExchange(ref _syncRunning, 1, 0) != 0)
            {
                logger.Info("Sync already in progress, skipping");
                return;
            }

            var cts = new CancellationTokenSource();
            _syncCts = cts;

            _syncTask = Task.Run(() =>
            {
                try
                {
                    SyncFilteredGamesBackground(cts.Token);
                }
                finally
                {
                    _syncCts = null;
                    cts.Dispose();
                    Interlocked.Exchange(ref _syncRunning, 0);
                }
            });
        }

        private void CancelSync()
        {
            try { _syncCts?.Cancel(); } catch (ObjectDisposedException) { }
        }

        private void SyncFilteredGamesBackground(CancellationToken cancellationToken)
        {
            logger.Info("Starting sync filtered games operation");

            var filteredGames = GetFilteredGames();
            if (filteredGames.Count == 0)
            {
                ShowNotificationIfEnabled(new NotificationMessage(
                    "apollosync-no-games",
                    "No games match the selected filter presets. Please check your filter preset configuration.",
                    NotificationType.Error), isUpdateOperation: true);
                return;
            }

            logger.Info($"Found {filteredGames.Count} games matching filter presets to sync");

            var localSuccess = 0;
            var localFailure = 0;
            var localErrors = new List<string>();
            var localRemoved = 0;

            // Snapshot pinned game IDs for thread-safe access
            HashSet<Guid> pinnedSnapshot;
            lock (_configLock)
            {
                pinnedSnapshot = new HashSet<Guid>(_settings.Settings.PinnedGameIds);
            }

            // Load config once at the beginning
            JObject config;
            lock (_configLock)
            {
                config = LoadAppsConfig();
            }
            if (config == null)
            {
                localErrors.Add("Failed to load apps.json configuration");
                ShowSyncCompletionNotification(0, filteredGames.Count, localErrors, 0);
                return;
            }

            logger.Info($"Starting batch sync operation with {filteredGames.Count} games");

            // Phase 1: Remove games that no longer meet filters (unless pinned)
            var removedGames = RemoveFilteredOutGames(config, pinnedSnapshot);
            localRemoved = removedGames;
            logger.Info($"Removed {removedGames} games that no longer meet filters");

            // Phase 2: Add/update games that meet current filters
            for (int i = 0; i < filteredGames.Count; i++)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    logger.Info("Sync cancelled by user");
                    break;
                }

                var game = filteredGames[i];
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
                lock (_configLock)
                {
                    try
                    {
                        SaveAppsConfig(config);
                        SaveManagedStore();
                        logger.Info("Batch save completed successfully");
                    }
                    catch (Exception ex)
                    {
                        logger.Error(ex, "Failed to save batch changes");
                        localErrors.Add("Failed to save changes to disk");
                    }
                }
            }

            logger.Info($"Sync all completed. Success: {localSuccess}, Failed: {localFailure}, Removed: {localRemoved}");
            ShowSyncCompletionNotification(localSuccess, localFailure, localErrors, localRemoved);
        }

        private void ShowSyncCompletionNotification(int successCount, int failureCount, List<string> errors, int removedCount)
        {
            // Show completion notification
            var message = string.Format(
                ResourceProvider.GetString("LOC_ApolloSync_Message_SyncAll_Complete"),
                successCount, failureCount);

            if (removedCount > 0)
            {
                message += $" Removed {removedCount} filtered out games.";
            }

            // Determine notification type based on results
            var notificationType = failureCount == 0 && !errors.Any()
                ? NotificationType.Info
                : NotificationType.Error;

            // Add notification with click action for errors
            var notification = new NotificationMessage(
                "apollosync-sync-complete",
                message,
                notificationType);

            // If there are errors, allow clicking to view details
            if (errors.Any())
            {
                notification = new NotificationMessage(
                    "apollosync-sync-complete",
                    message + " (Click to view error details)",
                    notificationType);
            }

            ShowNotificationIfEnabled(notification, isUpdateOperation: true);
        }

        private void ExportGamesWithFeedback(IEnumerable<Game> games)
        {
            var gameList = games.ToList();
            if (gameList.Count == 0)
                return;

            // Pin games on the calling (UI) thread before going async — PinnedGameIds is
            // not thread-safe and must not be accessed from Task.Run.
            var pinnedCount = 0;
            foreach (var game in gameList)
            {
                if (!_settings.Settings.PinnedGameIds.Contains(game.Id))
                {
                    _settings.Settings.PinnedGameIds.Add(game.Id);
                    pinnedCount++;
                }
            }
            if (pinnedCount > 0)
            {
                SavePluginSettings(_settings.Settings);
                logger.Info($"Auto-pinned {pinnedCount} manually exported games");
            }

            // Run on a background thread so the UI is never blocked by the up-to-30 s
            // CancelSync wait. The lock inside still serialises access to apps.json.
            Task.Run(() =>
            {
                // Cancel any running background sync before touching apps.json to prevent
                // the sync's stale snapshot from overwriting our changes on its final save.
                CancelSync();
                try
                {
                    var syncCompleted = _syncTask?.Wait(TimeSpan.FromSeconds(30)) ?? true;
                    if (!syncCompleted)
                        logger.Warn("Previous sync task did not finish within 30 s; proceeding with operation anyway");
                }
                catch (AggregateException) { }

                logger.Info("Starting export games operation");
                logger.Info($"Exporting {gameList.Count} games");

                var successCount = 0;
                var failureCount = 0;
                var errors = new List<string>();

                lock (_configLock)
                {
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
                        }

                        // Save everything once at the end
                        if (successCount > 0)
                        {
                            logger.Info($"Saving batch export changes to disk. Processed {successCount} games successfully.");
                            try
                            {
                                SaveAppsConfig(config);
                                SaveManagedStore();
                                logger.Info("Batch export save completed successfully");
                            }
                            catch (Exception ex)
                            {
                                logger.Error(ex, "Failed to save batch export changes");
                                errors.Add("Failed to save changes to disk");
                            }
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
                    notificationType), isUpdateOperation: false);
            }); // end Task.Run
        }

        private void RemoveGamesWithFeedback(IEnumerable<Game> games)
        {
            var gameList = games.ToList();
            if (gameList.Count == 0)
                return;

            Task.Run(() =>
            {
                // Same lost-update guard and UI-thread protection as ExportGamesWithFeedback.
                CancelSync();
                try
                {
                    var syncCompleted = _syncTask?.Wait(TimeSpan.FromSeconds(30)) ?? true;
                    if (!syncCompleted)
                        logger.Warn("Previous sync task did not finish within 30 s; proceeding with operation anyway");
                }
                catch (AggregateException) { }

                logger.Info("Starting remove games operation");
                logger.Info($"Removing {gameList.Count} games");

                var successCount = 0;
                var failureCount = 0;
                var errors = new List<string>();

                lock (_configLock)
                {
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
                                logger.Info("Batch removal save completed successfully");
                            }
                            catch (Exception ex)
                            {
                                logger.Error(ex, "Failed to save batch removal changes");
                                errors.Add("Failed to save changes to disk");
                            }
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
                    notificationType), isUpdateOperation: false);
            }); // end Task.Run
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
                if (!_settings.Settings.PinnedGameIds.Contains(game.Id))
                {
                    _settings.Settings.PinnedGameIds.Add(game.Id);
                    pinnedCount++;
                    logger.Debug($"Pinned game: {game.Name} (ID: {game.Id})");
                }
            }

            if (pinnedCount > 0)
            {
                SavePluginSettings(_settings.Settings);
                ShowNotificationIfEnabled(new NotificationMessage(
                    "apollosync-pin-complete",
                    $"Pinned {pinnedCount} games. Pinned games will not be automatically removed when filters change or games are uninstalled.",
                    NotificationType.Info), isUpdateOperation: false);
            }
            else
            {
                ShowNotificationIfEnabled(new NotificationMessage(
                    "apollosync-pin-info",
                    "All selected games are already pinned.",
                    NotificationType.Info), isUpdateOperation: false);
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
                if (_settings.Settings.PinnedGameIds.Contains(game.Id))
                {
                    _settings.Settings.PinnedGameIds.Remove(game.Id);
                    unpinnedCount++;
                    logger.Debug($"Unpinned game: {game.Name} (ID: {game.Id})");
                }
            }

            if (unpinnedCount > 0)
            {
                SavePluginSettings(_settings.Settings);
                ShowNotificationIfEnabled(new NotificationMessage(
                    "apollosync-unpin-complete",
                    $"Unpinned {unpinnedCount} games. These games may now be automatically removed based on your filter _settings.",
                    NotificationType.Info), isUpdateOperation: false);
            }
            else
            {
                ShowNotificationIfEnabled(new NotificationMessage(
                    "apollosync-unpin-info",
                    "None of the selected games were pinned.",
                    NotificationType.Info), isUpdateOperation: false);
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
                var ok = syncService.AddOrUpdate(config, _managedStore, game);

                if (ok)
                {
                    logger.Debug($"Successfully added/updated app for game: {game.Name}");
                    logger.Debug($"Config before save has {((JArray)config["apps"])?.Count ?? 0} apps");
                    SaveAppsConfig(config);
                    SaveManagedStore();
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
                var ok = syncService.AddOrUpdate(config, _managedStore, game);
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
                var ok = syncService.Remove(config, _managedStore, game);
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
                var path = _settings.Settings.AppsJsonPath;
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
                var path = _settings.Settings.AppsJsonPath;
                logger.Debug($"Saving apps config to path: {path}");
                try
                {
                    configService.Save(path, config);
                }
                catch (UnauthorizedAccessException)
                {
                    // Inform user and offer to fix permissions with elevation
                    // Must dispatch to UI thread since this may be called from a background task
                    var handled = false;
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        var message = ResourceProvider.GetString("LOC_ApolloSync_Permissions_Prompt_Body");
                        var title = ResourceProvider.GetString("LOC_ApolloSync_Permissions_Prompt_Title");
                        var result = PlayniteApi.Dialogs.ShowMessage(message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning);
                        if (result == MessageBoxResult.Yes)
                        {
                            TryFixFilePermissionsWithElevation(path);
                            handled = true;
                        }
                    });
                    if (handled)
                    {
                        // Retry once after permissions fix
                        configService.Save(path, config);
                    }
                    else
                    {
                        throw;
                    }
                }
                logger.Debug("Successfully saved apps config");
            }
            catch (Exception ex)
            {
                logger.Error(ex, $"Exception occurred while saving apps config");
            }
        }

        private void TryFixFilePermissionsWithElevation(string filePath)
        {
            try
            {
                // Reject non-local paths to prevent NTLM relay attacks and injection via icacls.
                if (!Services.ConfigService.IsLocalAbsolutePath(filePath))
                {
                    logger.Error($"TryFixFilePermissionsWithElevation: rejecting non-local path '{filePath}'");
                    throw new ArgumentException($"Permission fix is only supported for local paths; got: {filePath}");
                }

                // Write the icacls invocation to a temp script that receives the file path as
                // $args[0]. PowerShell passes positional arguments verbatim — no shell
                // interpretation of special characters — so filePath cannot inject commands.
                var scriptPath = Path.Combine(
                    Path.GetTempPath(),
                    "apollosync_perms_" + Path.GetRandomFileName() + ".ps1");
                try
                {
                    using (var fs = new FileStream(scriptPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    using (var w = new StreamWriter(fs, new UTF8Encoding(false)))
                    {
                        w.Write("icacls $args[0] /grant '*S-1-5-32-545:(M)'");
                    }

                    var psi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "powershell.exe",
                        // Both scriptPath and filePath are local absolute paths; Windows filenames
                        // cannot contain double quotes, so quoting here is safe.
                        Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{scriptPath}\" \"{filePath}\"",
                        Verb = "runas",
                        UseShellExecute = true,
                        CreateNoWindow = true
                    };
                    System.Diagnostics.Process.Start(psi)?.WaitForExit(30000);
                }
                finally
                {
                    try { if (File.Exists(scriptPath)) File.Delete(scriptPath); } catch { }
                }
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Failed to trigger permission fix");
                throw;
            }
        }
        #endregion
    }
}
