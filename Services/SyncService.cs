using ApolloSync.Models;
using Newtonsoft.Json.Linq;
using Playnite.SDK;
using Playnite.SDK.Models;
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text;

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
        private static readonly Random _rng = new Random();
        private readonly IPlayniteAPI _api;
        private readonly string _imageCacheDir;

        public SyncService(IPlayniteAPI api = null, string imageCacheDir = null)
        {
            _api = api;
            _imageCacheDir = imageCacheDir
                ?? Path.Combine(Path.GetTempPath(), "ApolloSync", "imagecache");
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

            // Use Playnite's own game GUID as the Apollo/Sunshine UUID.
            var uuid = game.Id;

            // Keep mapping in the managed store (identity mapping) for compatibility with existing flows.
            store.GameToUuid[game.Id] = uuid;
            logger.Debug($"Using Playnite game ID as UUID for {game.Name}: {uuid}");

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

            // Find existing by new UUID
            var existing = apps.FirstOrDefault(a => string.Equals((string)a["uuid"], uuidStr, StringComparison.OrdinalIgnoreCase)) as JObject;
            if (existing != null)
            {
                logger.Debug($"Updating existing app entry for game: {game.Name}");
                // Update mutable fields
                existing["name"] = entry["name"];
                existing["cmd"] = entry["cmd"];
                existing.Remove("detached"); // Remove deprecated field left over from pre-cmd migration
                if (entry["image-path"] != null)
                {
                    existing["image-path"] = entry["image-path"];
                }
                else
                {
                    existing.Remove("image-path"); // Clear stale path if game no longer has a cover
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
                // Fallback to using the Playnite game ID directly (new behavior).
                uuid = game.Id;
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
            Guid removed;
            store.GameToUuid.TryRemove(game.Id, out removed);
            CleanupCachedImage(game);
            return true;
        }

        private void CleanupCachedImage(Game game)
        {
            try
            {
                var pngPath = Path.Combine(_imageCacheDir, game.Id.ToString("N") + ".png");
                if (File.Exists(pngPath))
                {
                    File.Delete(pngPath);
                    logger.Debug($"Deleted cached PNG for game {game.Name}: {pngPath}");
                }
            }
            catch (Exception ex)
            {
                logger.Debug($"Could not clean up cached image for {game.Name}: {ex.Message}");
            }
        }

        public static string GetLockFilePath(Guid gameId)
        {
            return Path.Combine(Path.GetTempPath(), $"apollosync-{gameId:N}.lock");
        }

        private JObject BuildAppEntry(Game game, Guid uuid)
        {
            // Build a PowerShell wrapper cmd that Apollo can track for session lifetime.
            // Playnite.DesktopApp.exe --start exits immediately (it signals an existing
            // Playnite instance via IPC), so we can't track it directly. Instead:
            //   1. Launch the game via Playnite
            //   2. Wait for the plugin's OnGameStarted to create a lock file
            //   3. Wait for the plugin's OnGameStopped to delete the lock file
            //   4. Exit — Apollo sees the process exit and ends the stream
            var lockFileName = $"apollosync-{game.Id:N}.lock";
            var playnitePath = GetPlayniteDesktopPath();

            var launchLine = string.IsNullOrEmpty(playnitePath)
                ? $"Start-Process 'playnite://play/{game.Id}'"
                : $"& \"{playnitePath}\" --start {game.Id}";

            var psScript =
                $"$lf = Join-Path $env:TEMP '{lockFileName}'\r\n" +
                $"{launchLine}\r\n" +
                "$t = 0\r\n" +
                "while (-not (Test-Path $lf) -and $t -lt 120) { Start-Sleep -Milliseconds 500; $t++ }\r\n" +
                "while (Test-Path $lf) { Start-Sleep -Seconds 2 }";

            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(psScript));
            var cmd = $"powershell.exe -NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand {encoded}";

            var obj = new JObject
            {
                ["name"] = game.Name,
                ["uuid"] = ToApolloUuid(uuid),
                ["cmd"] = cmd
            };

            var imgPath = TryGetCoverImagePath(game, uuid);
            if (!string.IsNullOrEmpty(imgPath))
            {
                obj["image-path"] = imgPath;
            }
            return obj;
        }

        private static string GetPlayniteDesktopPath()
        {
            var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var candidate = Path.Combine(baseDir, "Playnite", "Playnite.DesktopApp.exe");
            return File.Exists(candidate) ? candidate : null;
        }

        private static string ToApolloUuid(Guid uuid)
        {
            // Apollo examples show uppercase with hyphens
            return uuid.ToString().ToUpperInvariant();
        }

        private string TryGetCoverImagePath(Game game, Guid gameUuid)
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

                // Apollo requires PNG images; convert if necessary
                if (sourcePath.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                {
                    logger.Debug($"Using Playnite cover image path for game {game.Name}: {sourcePath}");
                    return sourcePath;
                }

                var pngPath = ConvertToPng(sourcePath, game.Name, gameUuid);
                if (pngPath != null)
                {
                    logger.Debug($"Using converted PNG for game {game.Name}: {pngPath}");
                    return pngPath;
                }

                logger.Debug($"PNG conversion failed for game {game.Name}, skipping image");
                return null;
            }
            catch (Exception ex)
            {
                logger.Info(ex, $"ApolloSync: Error getting cover image path for '{game.Name}'.");
                return null;
            }
        }

        private string ConvertToPng(string sourcePath, string gameName, Guid gameUuid)
        {
            try
            {
                Directory.CreateDirectory(_imageCacheDir);

                // Use game UUID as cache key to avoid collisions from duplicate filenames
                var pngPath = Path.Combine(_imageCacheDir, gameUuid.ToString("N") + ".png");

                // Skip conversion if cached PNG already exists and is newer than source
                if (File.Exists(pngPath) && File.GetLastWriteTimeUtc(pngPath) >= File.GetLastWriteTimeUtc(sourcePath))
                {
                    return pngPath;
                }

                // Load via MemoryStream to avoid locking the source file in Playnite's database
                var tmpPath = pngPath + ".tmp";
                try
                {
                    using (var ms = new MemoryStream(File.ReadAllBytes(sourcePath)))
                    using (var image = Image.FromStream(ms))
                    {
                        // Write to temp file, then replace — not fully atomic on Windows
                        // (Delete+Move gap) but prevents corrupt cache from partial writes
                        image.Save(tmpPath, ImageFormat.Png);
                    }
                    if (File.Exists(pngPath))
                    {
                        File.Delete(pngPath);
                    }
                    File.Move(tmpPath, pngPath);
                }
                catch
                {
                    // Clean up orphaned temp file on any failure
                    try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
                    throw;
                }

                logger.Debug($"Converted cover image to PNG for game {gameName}: {pngPath}");
                return pngPath;
            }
            catch (Exception ex)
            {
                logger.Info(ex, $"ApolloSync: Failed to convert cover image to PNG for '{gameName}'.");
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

            string id;
            do
            {
                lock (_rng) { id = _rng.Next().ToString(); }
            } while (existing.Contains(id));
            return id;
        }
    }
}
