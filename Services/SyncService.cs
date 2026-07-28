using ApolloSync.Models;
using Newtonsoft.Json.Linq;
using Playnite.SDK;
using Playnite.SDK.Models;
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
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

        // Moonlight's app grid forces every cover to 200x267 with QML's default Stretch fill
        // (moonlight-qt app/gui/AppView.qml), so anything that is not 3:4 arrives distorted.
        // Covers are normalised onto this canvas to make the result independent of whichever
        // metadata source the user's library came from.
        internal const int CoverWidth = 600;
        internal const int CoverHeight = 800;

        // Part of the cache file name so that changing the canvas regenerates rather than serving
        // images normalised under the old rules. Derived from the dimensions rather than a hand
        // bumped constant, which nothing would have forced anyone to remember to change. The
        // trailing revision covers changes that do not alter the dimensions, such as the draw
        // call gaining WrapMode.TileFlipXY.
        private static readonly string CoverCacheVersion = CoverWidth + "x" + CoverHeight + "r2";

        private readonly IPlayniteAPI _api;
        private readonly string _imageCacheDir;
        private readonly Func<bool> _manageCoverImages;

        public SyncService(IPlayniteAPI api = null, string imageCacheDir = null, Func<bool> manageCoverImages = null)
        {
            _api = api;
            // Not %TEMP%: these are long-lived derived files that apps.json points at by absolute
            // path, and Storage Sense and Disk Cleanup both delete from %TEMP%, which would
            // silently strip artwork from every exported game. Same root as the config backups.
            _imageCacheDir = imageCacheDir
                ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ApolloSync", "imagecache");
            _manageCoverImages = manageCoverImages ?? (() => true);
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

            // Read the setting exactly once per call. It was previously read again further down,
            // with a full image decode/encode in between — long enough for the user to tick the
            // box on and have the update branch clear image-path against a null it produced while
            // the setting was still off.
            var manageCoverImages = _manageCoverImages();

            string coverPath = null;
            var coverConversionFailed = false;
            if (manageCoverImages)
            {
                coverPath = TryGetCoverImagePath(game, uuid, out coverConversionFailed);
            }

            var entry = BuildAppEntry(game, uuid, coverPath);
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

                // With cover management off, image-path is not ours to touch — the user may have
                // set artwork in Apollo's own UI, and clearing or overwriting it would undo that.
                //
                // A failed conversion is also not a reason to clear it. TryGetCoverImagePath
                // returns null both for "this game has no cover" and "we could not produce one",
                // and only the first of those means the entry should lose its artwork. Every
                // cover now goes through conversion, so transient failures — a full disk, the
                // cached file briefly locked — reach here where they never could before.
                if (manageCoverImages && !coverConversionFailed)
                {
                    if (entry["image-path"] != null)
                    {
                        existing["image-path"] = entry["image-path"];
                    }
                    else
                    {
                        existing.Remove("image-path"); // Clear stale path if game no longer has a cover
                    }
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

        /// <summary>
        /// The single definition of where a game's normalised cover lives. Both the writer and
        /// the cleanup path must agree, and they did not when the version suffix was introduced
        /// in only one of them — removing a game then left its cached image behind forever.
        /// </summary>
        private string GetCoverCachePath(Guid gameId)
        {
            return Path.Combine(_imageCacheDir, gameId.ToString("N") + "_" + CoverCacheVersion + ".png");
        }

        private void CleanupCachedImage(Game game)
        {
            try
            {
                if (!Directory.Exists(_imageCacheDir))
                {
                    return;
                }

                // Glob rather than just the current name: covers cached under an earlier canvas
                // are still this game's files, and nothing else would ever delete them. The cache
                // now lives in %LOCALAPPDATA%, which Storage Sense does not clean.
                foreach (var stale in Directory.GetFiles(_imageCacheDir, game.Id.ToString("N") + "_*.png"))
                {
                    File.Delete(stale);
                    logger.Debug($"Deleted cached PNG for game {game.Name}: {stale}");
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

        private JObject BuildAppEntry(Game game, Guid uuid, string coverPath)
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
                "$elapsed = 0\r\n" +
                "while ((Test-Path $lf) -and $elapsed -lt 28800) { Start-Sleep -Seconds 2; $elapsed += 2 }";

            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(psScript));
            var cmd = $"powershell.exe -NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand {encoded}";

            var obj = new JObject
            {
                ["name"] = game.Name,
                ["uuid"] = ToApolloUuid(uuid),
                ["cmd"] = cmd
            };

            if (!string.IsNullOrEmpty(coverPath))
            {
                obj["image-path"] = coverPath;
            }
            return obj;
        }

        private string GetPlayniteDesktopPath()
        {
            if (_api?.Paths?.ApplicationPath != null)
            {
                var candidate = Path.Combine(_api.Paths.ApplicationPath, "Playnite.DesktopApp.exe");
                return File.Exists(candidate) ? candidate : null;
            }
            return null;
        }

        private static string ToApolloUuid(Guid uuid)
        {
            // Apollo examples show uppercase with hyphens
            return uuid.ToString().ToUpperInvariant();
        }

        /// <summary>
        /// Returns the path to the game's normalised cover, or null if there isn't one.
        /// <paramref name="conversionFailed"/> separates "this game has no cover" from "it has one
        /// and we could not produce a PNG from it" — only the former should clear an existing
        /// image-path, and callers that delete on a null result must check it.
        /// </summary>
        private string TryGetCoverImagePath(Game game, Guid gameUuid, out bool conversionFailed)
        {
            conversionFailed = false;

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

                // Every cover goes through conversion, including ones already named .png. The
                // extension is not evidence of the contents: Playnite names cover files after the
                // source URL, and metadata providers routinely serve JPEG bytes from a .png URL.
                // Trusting the name handed Apollo a JPEG it could not decode, and the game showed
                // no artwork at all. Image.FromStream sniffs the real format, so this also
                // normalises the aspect ratio in the same pass.
                var pngPath = ConvertToPng(sourcePath, game.Name, gameUuid);
                if (pngPath != null)
                {
                    logger.Debug($"Using converted PNG for game {game.Name}: {pngPath}");
                    return pngPath;
                }

                conversionFailed = true;
                logger.Warn($"PNG conversion failed for game {game.Name}; leaving any existing box art in place");
                return null;
            }
            catch (Exception ex)
            {
                conversionFailed = true;
                logger.Info(ex, $"ApolloSync: Error getting cover image path for '{game.Name}'.");
                return null;
            }
        }

        /// <summary>
        /// Scales <paramref name="sourceWidth"/> x <paramref name="sourceHeight"/> to fit inside
        /// the cover canvas without distorting it, and returns where to draw it. Fit-and-pad
        /// rather than crop: the square art many metadata sources produce would lose 25% off both
        /// sides, which is exactly where cover titles and logos sit.
        /// Pure, so the geometry is testable without touching GDI+.
        /// </summary>
        internal static Rectangle GetLetterboxBounds(int sourceWidth, int sourceHeight, int canvasWidth, int canvasHeight)
        {
            if (sourceWidth <= 0 || sourceHeight <= 0)
            {
                return new Rectangle(0, 0, canvasWidth, canvasHeight);
            }

            var scale = Math.Min((double)canvasWidth / sourceWidth, (double)canvasHeight / sourceHeight);

            // Round rather than truncate, and clamp: a rounded-up edge must not exceed the canvas.
            var width = Math.Min(canvasWidth, Math.Max(1, (int)Math.Round(sourceWidth * scale)));
            var height = Math.Min(canvasHeight, Math.Max(1, (int)Math.Round(sourceHeight * scale)));

            return new Rectangle((canvasWidth - width) / 2, (canvasHeight - height) / 2, width, height);
        }

        private string ConvertToPng(string sourcePath, string gameName, Guid gameUuid)
        {
            try
            {
                Directory.CreateDirectory(_imageCacheDir);

                // Use game UUID as cache key to avoid collisions from duplicate filenames
                var pngPath = GetCoverCachePath(gameUuid);

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
                    using (var canvas = new Bitmap(CoverWidth, CoverHeight, PixelFormat.Format32bppArgb))
                    {
                        using (var g = Graphics.FromImage(canvas))
                        using (var attributes = new ImageAttributes())
                        {
                            // Transparent padding rather than bars: the grid background shows
                            // through, so letterboxing is not visible as a border.
                            g.Clear(Color.Transparent);
                            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                            g.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;

                            // Without this the bicubic kernel samples past the edge of the source
                            // and blends with the transparent canvas, leaving a translucent halo
                            // around every cover — measured down to ~46% alpha at the corners,
                            // which reads as a dark outline anywhere the alpha is flattened.
                            attributes.SetWrapMode(WrapMode.TileFlipXY);

                            var bounds = GetLetterboxBounds(image.Width, image.Height, CoverWidth, CoverHeight);
                            g.DrawImage(image, bounds, 0, 0, image.Width, image.Height,
                                GraphicsUnit.Pixel, attributes);
                        }

                        // Write to temp file, then replace — not fully atomic on Windows
                        // (Delete+Move gap) but prevents corrupt cache from partial writes
                        canvas.Save(tmpPath, ImageFormat.Png);
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
