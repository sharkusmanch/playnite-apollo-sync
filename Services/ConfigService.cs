using Playnite.SDK;
using Newtonsoft.Json.Linq;
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace ApolloSync.Services
{
    public interface IConfigService
    {
        JObject Load(string path);
        void Save(string path, JObject config);
    }

    public class ConfigService : IConfigService
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private const string ApolloDir = "Apollo";
        private const string SunshineDir = "Sunshine";

        public JObject Load(string path)
        {
            try
            {
                var resolvedPath = ResolveConfigPath(path, preferExisting: true);
                logger.Debug($"ConfigService.Load - Resolved path: {resolvedPath}");

                if (string.IsNullOrWhiteSpace(resolvedPath) || !File.Exists(resolvedPath))
                {
                    logger.Debug($"ConfigService.Load - File doesn't exist, creating default config");
                    var j = new JObject
                    {
                        ["apps"] = new JArray(),
                        ["env"] = new JObject(),
                        ["version"] = 2
                    };
                    return j;
                }

                var json = File.ReadAllText(resolvedPath);
                var config = JObject.Parse(json);
                DeduplicateApps(config, "Load");
                var appsCount = ((JArray)config["apps"])?.Count ?? 0;

                logger.Info($"ConfigService.Load - Successfully loaded apps.json from: {resolvedPath} with {appsCount} apps");
                return config;
            }
            catch (Exception e)
            {
                logger.Error(e, "ApolloSync: Failed to load apps.json");
                return null;
            }
        }

        public void Save(string path, JObject config)
        {
            try
            {
                // preferExisting: true so a Sunshine-only install writes back to its own
                // apps.json instead of silently creating a new Apollo config that nothing reads.
                var resolvedPath = ResolveConfigPath(path, preferExisting: true);
                logger.Debug($"ConfigService.Save - Resolved path: {resolvedPath}");

                var dir = Path.GetDirectoryName(resolvedPath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                    logger.Debug($"ConfigService.Save - Created directory: {dir}");
                }

                // De-duplicate before writing, and normalize UUID casing
                DeduplicateApps(config, "Save");

                var jsonContent = config.ToString(Newtonsoft.Json.Formatting.Indented);
                logger.Debug($"ConfigService.Save - Writing config with {((JArray)config["apps"])?.Count ?? 0} apps");

                var bytes = Encoding.UTF8.GetBytes(jsonContent);

                // Prefer an atomic same-directory swap. File.Replace exchanges the directory
                // entry in a single operation and keeps the previous contents as a .bak, so an
                // interrupted write can never leave apps.json truncated. It needs create rights
                // in the destination directory, which we do not have when apps.json lives in
                // Program Files and only the file itself was granted modify (see
                // TryFixFilePermissionsWithElevation) — fall back to a copy-based write there.
                if (!TryReplaceInPlace(resolvedPath, bytes))
                {
                    CopyThroughTempFile(resolvedPath, bytes);
                }
                logger.Info($"ConfigService.Save - Successfully saved apps.json to: {resolvedPath}");
            }
            catch (Exception ex)
            {
                logger.Error(ex, $"ConfigService.Save - Failed to save apps.json to path: {path}");
                throw;
            }
        }

        /// <summary>
        /// Writes <paramref name="bytes"/> to a temp file beside <paramref name="destination"/> and
        /// swaps it in atomically, keeping the previous contents as "&lt;destination&gt;.bak".
        /// Returns false if the destination directory cannot be written to, so the caller can fall
        /// back to <see cref="CopyThroughTempFile"/>.
        /// </summary>
        private static bool TryReplaceInPlace(string destination, byte[] bytes)
        {
            var dir = Path.GetDirectoryName(destination);
            if (string.IsNullOrEmpty(dir))
            {
                return false;
            }

            var tmpPath = Path.Combine(dir, "apollosync_" + Path.GetRandomFileName() + ".tmp");
            try
            {
                // FileMode.CreateNew fails if a file (or symlink to an existing file) already
                // exists at tmpPath, preventing symlink substitution.
                using (var fs = new FileStream(tmpPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    fs.Write(bytes, 0, bytes.Length);
                    // Force to disk before the swap, so the directory entry can never point at
                    // content the OS has not yet committed.
                    fs.Flush(flushToDisk: true);
                }

                if (File.Exists(destination))
                {
                    File.Replace(tmpPath, destination, destination + ".bak", ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(tmpPath, destination);
                }

                logger.Debug($"ConfigService - Atomically replaced {destination}");
                return true;
            }
            catch (Exception ex)
            {
                // Expected when the install directory is not writable by the current user; the
                // caller retries via the copy path, which only needs rights on the file itself.
                logger.Debug($"ConfigService - Atomic replace unavailable for {destination} ({ex.GetType().Name}: {ex.Message}); falling back to copy");
                return false;
            }
            finally
            {
                // No-op when the swap succeeded — Replace/Move consumed the temp file.
                try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
            }
        }

        /// <summary>
        /// Writes <paramref name="bytes"/> to %TEMP% (always user-writable) and copies the result
        /// over <paramref name="destination"/>. Used when the destination directory is protected
        /// and only the file itself is writable, so no atomic swap is possible.
        /// </summary>
        internal static void CopyThroughTempFile(string destination, byte[] bytes)
        {
            // Back up the previous contents ONCE, before anything can damage the destination.
            // Taking this inside the retry loop would let attempt 2 copy a half-written
            // destination over the only good backup.
            BackupExistingFile(destination);

            var tmpPath = Path.Combine(Path.GetTempPath(), "apollosync_" + Path.GetRandomFileName() + ".tmp");
            try
            {
                var attempts = 0;
                const int maxAttempts = 3;
                while (true)
                {
                    // Delete any leftover tmp from a previous failed attempt so FileMode.CreateNew can succeed.
                    try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
                    try
                    {
                        // FileMode.CreateNew fails if a file (or symlink to an existing
                        // file) already exists at tmpPath, preventing symlink substitution.
                        using (var fs = new FileStream(tmpPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        {
                            fs.Write(bytes, 0, bytes.Length);
                            fs.Flush(flushToDisk: true);
                        }

                        File.Copy(tmpPath, destination, overwrite: true);
                        return;
                    }
                    catch (UnauthorizedAccessException)
                    {
                        throw; // handled by caller for permission prompt
                    }
                    catch (IOException)
                    {
                        attempts++;
                        if (attempts >= maxAttempts)
                            throw;
                        Thread.Sleep(150 * attempts);
                    }
                }
            }
            finally
            {
                try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
            }
        }

        /// <summary>
        /// Copies the current contents of <paramref name="destination"/> aside, so an interrupted
        /// write is recoverable. Prefers "&lt;destination&gt;.bak"; when the install directory is not
        /// writable — the usual case for the default Program Files install, where only the file
        /// itself was granted modify — falls back to the user's local application data, which
        /// always is. Failing to back up is logged, never fatal.
        /// </summary>
        private static void BackupExistingFile(string destination)
        {
            if (!File.Exists(destination))
            {
                return;
            }

            try
            {
                File.Copy(destination, destination + ".bak", overwrite: true);
                return;
            }
            catch (Exception ex)
            {
                logger.Debug($"ConfigService - Cannot write a backup beside {destination} ({ex.GetType().Name}); falling back to local app data");
            }

            try
            {
                var backupPath = GetFallbackBackupPath(destination);
                Directory.CreateDirectory(Path.GetDirectoryName(backupPath));
                File.Copy(destination, backupPath, overwrite: true);
                logger.Info($"ConfigService - Backed up {destination} to {backupPath}");
            }
            catch (Exception ex)
            {
                logger.Warn($"ConfigService - Could not write any backup for {destination}: {ex.Message}");
            }
        }

        /// <summary>
        /// A stable, always-writable backup location. The destination path is flattened into the
        /// file name so Apollo and Sunshine configs cannot overwrite each other's backup.
        /// </summary>
        internal static string GetFallbackBackupPath(string destination)
        {
            var flattened = destination;
            foreach (var invalid in Path.GetInvalidFileNameChars())
            {
                flattened = flattened.Replace(invalid, '_');
            }

            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ApolloSync",
                "backups",
                flattened + ".bak");
        }

        /// <summary>
        /// Returns true only for local absolute paths (e.g. "C:\foo\bar.json").
        /// Rejects UNC/network paths, device paths, relative paths, and empty strings.
        /// Used to guard elevated operations against NTLM relay and command injection.
        /// </summary>
        public static bool IsLocalAbsolutePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            if (path.StartsWith(@"\\", StringComparison.Ordinal)) return false; // UNC / device
            if (path.StartsWith("//", StringComparison.Ordinal)) return false;
            if (!Path.IsPathRooted(path)) return false;
            // Require a drive-letter root ("C:\") not a bare root ("\")
            var root = Path.GetPathRoot(path);
            return root != null
                && root.Length >= 3
                && char.IsLetter(root[0])
                && root[1] == ':';
        }

        /// <summary>
        /// Resolves the apps.json path used by Load/Save. If <paramref name="path"/> is non-empty
        /// it is returned as-is; otherwise the default Apollo/Sunshine install path is used.
        /// Exposed so callers (e.g. the permission-fix elevation flow) can act on the same
        /// concrete path that Load/Save will ultimately hit.
        /// </summary>
        public static string ResolveConfigPath(string path, bool preferExisting)
        {
            return string.IsNullOrWhiteSpace(path) ? ResolveDefaultPath(preferExisting) : path;
        }

        private static string ResolveDefaultPath(bool preferExisting)
        {
            try
            {
                var programW6432 = Environment.GetEnvironmentVariable("ProgramW6432");
                var programFiles = !string.IsNullOrWhiteSpace(programW6432)
                    ? programW6432
                    : Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

                var apolloPath = Path.Combine(programFiles, ApolloDir, "config", "apps.json");
                var sunshinePath = Path.Combine(programFiles, SunshineDir, "config", "apps.json");

                if (preferExisting)
                {
                    if (File.Exists(apolloPath))
                    {
                        return apolloPath;
                    }
                    if (File.Exists(sunshinePath))
                    {
                        return sunshinePath;
                    }
                }

                // Prefer Apollo when not checking for existence
                return apolloPath;
            }
            catch
            {
                return null;
            }
        }

        private void DeduplicateApps(JObject config, string stage)
        {
            try
            {
                var apps = config["apps"] as JArray;
                if (apps == null)
                {
                    config["apps"] = new JArray();
                    return;
                }

                var bestByUuid = new System.Collections.Generic.Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
                var seenOrder = new System.Collections.Generic.List<string>();
                var noUuidApps = new System.Collections.Generic.List<JObject>();
                foreach (var app in apps.OfType<JObject>())
                {
                    var uuidStr = ((string)app["uuid"])?.Trim();
                    if (string.IsNullOrEmpty(uuidStr))
                    {
                        // Cannot deduplicate without a UUID — preserve the entry as-is.
                        logger.Warn($"ConfigService.{stage} - App entry has no UUID; preserving as-is (cannot deduplicate): {app["name"]}");
                        noUuidApps.Add(app);
                        continue;
                    }

                    // Normalize to uppercase for writing
                    app["uuid"] = uuidStr.ToUpperInvariant();
                    var key = (string)app["uuid"]; // uppercase

                    int idVal = -1;
                    var idStr = (string)app["id"];
                    int.TryParse(idStr, out idVal);

                    if (!bestByUuid.TryGetValue(key, out var existing))
                    {
                        bestByUuid[key] = app;
                        seenOrder.Add(key);
                    }
                    else
                    {
                        int existingId = -1;
                        var existingIdStr = (string)existing["id"];
                        int.TryParse(existingIdStr, out existingId);
                        if (idVal > existingId)
                        {
                            bestByUuid[key] = app;
                        }
                    }
                }

                var before = apps.Count;
                var deduped = new JArray();
                foreach (var k in seenOrder)
                {
                    if (bestByUuid.TryGetValue(k, out var keep))
                    {
                        deduped.Add(keep);
                    }
                }
                // Append entries that had no UUID (preserved as-is, cannot participate in dedup).
                foreach (var noUuidApp in noUuidApps)
                {
                    deduped.Add(noUuidApp);
                }
                config["apps"] = deduped;
                var after = deduped.Count;
                if (after < before)
                {
                    logger.Info($"ConfigService.{stage} - De-duplicated apps by UUID: {before} -> {after}");
                }
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "ConfigService - Deduplication failed; continuing");
            }
        }
    }
}
