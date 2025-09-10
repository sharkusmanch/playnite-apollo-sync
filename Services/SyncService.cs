using ApolloSync.Models;
using Newtonsoft.Json.Linq;
using Playnite.SDK;
using Playnite.SDK.Models;
using System;
using System.Linq;

namespace ApolloSync.Services
{
    public interface ISyncService
    {
        bool AddOrUpdate(JObject config, ManagedStore store, Game game);
        bool Remove(JObject config, ManagedStore store, Game game);
    }

    public class SyncService : ISyncService
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private readonly IPlayniteAPI _api;

        public SyncService(IPlayniteAPI api = null)
        {
            _api = api;
        }

        public bool AddOrUpdate(JObject config, ManagedStore store, Game game)
        {
            logger.Debug($"SyncService.AddOrUpdate called for game: {game?.Name} (ID: {game?.Id})");

            if (config == null)
            {
                logger.Error("SyncService.AddOrUpdate: config is null");
                return false;
            }
            if (store == null)
            {
                logger.Error("SyncService.AddOrUpdate: store is null");
                return false;
            }
            if (game == null)
            {
                logger.Error("SyncService.AddOrUpdate: game is null");
                return false;
            }

            logger.Debug($"Game details - Name: {game.Name}, IsInstalled: {game.IsInstalled}, InstallDirectory: {game.InstallDirectory}");

            if (!store.GameToUuid.TryGetValue(game.Id, out var uuid))
            {
                uuid = Guid.NewGuid();
                store.GameToUuid[game.Id] = uuid;
                logger.Debug($"Generated new UUID for game {game.Name}: {uuid}");
            }
            else
            {
                logger.Debug($"Using existing UUID for game {game.Name}: {uuid}");
            }

            var entry = BuildAppEntry(game, uuid);
            if (entry == null)
            {
                logger.Error($"BuildAppEntry returned null for game: {game.Name}");
                return false;
            }

            logger.Debug($"Built app entry for game {game.Name}: {entry}");

            var apps = config["apps"] as JArray;
            if (apps == null)
            {
                apps = new JArray();
                config["apps"] = apps;
            }

            var uuidStr = ToApolloUuid(uuid);
            logger.Debug($"Apollo UUID string: {uuidStr}");

            var existing = apps.FirstOrDefault(a => string.Equals((string)a["uuid"], uuidStr, StringComparison.OrdinalIgnoreCase)) as JObject;
            if (existing != null)
            {
                logger.Debug($"Updating existing app entry for game: {game.Name}");
                // Update mutable fields
                existing["name"] = entry["name"];
                existing["detached"] = entry["detached"];
                if (entry["image-path"] != null)
                {
                    existing["image-path"] = entry["image-path"];
                }
            }
            else
            {
                logger.Debug($"Adding new app entry for game: {game.Name}");
                // Assign a unique numeric id as string, like the baseline script
                entry["id"] = GenerateUniqueId(apps);
                apps.Add(entry);
            }

            logger.Info($"Successfully processed game {game.Name} for Apollo/Sunshine");
            return true;
        }

        public bool Remove(JObject config, ManagedStore store, Game game)
        {
            if (config == null || store == null || game == null)
            {
                return false;
            }

            if (!store.GameToUuid.TryGetValue(game.Id, out var uuid))
            {
                return false;
            }

            var apps = (JArray)(config["apps"] ?? new JArray());
            var uuidStr = ToApolloUuid(uuid);
            for (int i = apps.Count - 1; i >= 0; i--)
            {
                var obj = apps[i] as JObject;
                if (obj != null && string.Equals((string)obj["uuid"], uuidStr, StringComparison.OrdinalIgnoreCase))
                {
                    apps.RemoveAt(i);
                }
            }
            store.GameToUuid.Remove(game.Id);
            return true;
        }

        private JObject BuildAppEntry(Game game, Guid uuid)
        {
            // Build an Apollo-style entry that launches Playnite by game GUID via DesktopApp.exe
            // Detached array example: "\"C:\\Users\\...\\Playnite.DesktopApp.exe\" --start <gameId>"
            var playnitePath = GetPlayniteDesktopPath();
            var startCmd = string.IsNullOrEmpty(playnitePath)
                ? $"playnite://play/{game.Id}"
                : $"\"{playnitePath}\" --start {game.Id}";

            var obj = new JObject
            {
                ["name"] = game.Name,
                ["uuid"] = ToApolloUuid(uuid),
                ["detached"] = new JArray(startCmd)
            };

            var imgPath = TryGetCoverImagePath(game);
            if (!string.IsNullOrEmpty(imgPath))
            {
                obj["image-path"] = imgPath;
            }
            return obj;
        }

        private static string GetPlayniteDesktopPath()
        {
            // Prefer API path if available
            try
            {
                // _api may be null in unit tests
                // ApplicationPath points at Playnite root; DesktopApp exe is inside
                // Combine safely; if API is absent, fallback to default location
            }
            catch { }

            var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var candidate = System.IO.Path.Combine(baseDir, "Playnite", "Playnite.DesktopApp.exe");
            return System.IO.File.Exists(candidate) ? candidate : null;
        }

        private static string ToApolloUuid(Guid uuid)
        {
            // Apollo examples show uppercase with hyphens
            return uuid.ToString().ToUpperInvariant();
        }

        private string TryGetCoverImagePath(Game game)
        {
            try
            {
                if (string.IsNullOrEmpty(game.CoverImage))
                {
                    logger.Debug($"No cover image set for game: {game.Name}");
                    return null;
                }

                // Skip remote covers as Apollo/Sunshine needs local file paths
                if (game.CoverImage.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    logger.Debug($"Skipping remote cover image for game: {game.Name}");
                    return null;
                }

                string sourcePath = null;
                if (_api != null)
                {
                    sourcePath = _api.Database.GetFullFilePath(game.CoverImage);
                }
                else if (System.IO.Path.IsPathRooted(game.CoverImage))
                {
                    sourcePath = game.CoverImage;
                }

                if (string.IsNullOrEmpty(sourcePath) || !System.IO.File.Exists(sourcePath))
                {
                    logger.Debug($"Cover image file not found for game: {game.Name}, path: {sourcePath}");
                    return null;
                }

                logger.Debug($"Using Playnite cover image path for game {game.Name}: {sourcePath}");
                return sourcePath;
            }
            catch (Exception ex)
            {
                logger.Info(ex, $"ApolloSync: Error getting cover image path for '{game.Name}'.");
                return null;
            }
        }

        private static string GenerateUniqueId(JArray apps)
        {
            // Collect existing ids as strings
            var existing = new System.Collections.Generic.HashSet<string>(
                apps
                    .OfType<JObject>()
                    .Select(a => (string)a["id"])
                    .Where(s => !string.IsNullOrEmpty(s)),
                StringComparer.Ordinal);

            var rnd = new Random();
            string id;
            do
            {
                id = rnd.Next().ToString();
            } while (existing.Contains(id));
            return id;
        }
    }
}
