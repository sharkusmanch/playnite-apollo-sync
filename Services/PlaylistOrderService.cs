using ApolloSync.Models;
using Newtonsoft.Json.Linq;
using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace ApolloSync.Services
{
    /// <summary>
    /// Reorders apps.json entries to match Playlist order when the Playlist filter preset
    /// is included for sync and the Playlist plugin is loaded.
    /// </summary>
    internal static class PlaylistOrderService
    {
        private static readonly ILogger logger = LogManager.GetLogger();

        internal static readonly Guid PlaylistPluginId = Guid.Parse("b0313f81-2b86-4eba-9f24-1a727dedbd45");
        internal const string PlaylistPresetName = "Playlist";
        internal const string PlaylistFileName = "playlist.txt";

        private const int RetrySleepMs = 150;
        private const int MaxNonEmptyContentRetries = 2;

        public static bool IsPluginLoaded(IPlayniteAPI api)
        {
            if (api?.Addons?.Plugins == null)
            {
                return false;
            }

            return api.Addons.Plugins.Any(p => p.Id == PlaylistPluginId);
        }

        /// <summary>
        /// True if any included preset id maps to the name "Playlist" (Ordinal).
        /// <paramref name="getPresetName"/> returns null for missing presets.
        /// </summary>
        public static bool IsPlaylistPresetIncluded(IEnumerable<Guid> includedIds, Func<Guid, string> getPresetName)
        {
            if (includedIds == null || getPresetName == null)
            {
                return false;
            }

            foreach (var id in includedIds)
            {
                var name = getPresetName(id);
                if (string.Equals(name, PlaylistPresetName, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Parses playlist.txt lines into ordered game ids. Trims, skips invalid, de-dupes (first wins).
        /// </summary>
        public static List<Guid> ParsePlaylistLines(IEnumerable<string> lines)
        {
            var result = new List<Guid>();
            if (lines == null)
            {
                return result;
            }

            var seen = new HashSet<Guid>();
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                Guid id;
                if (!Guid.TryParse(line.Trim(), out id))
                {
                    continue;
                }

                if (seen.Add(id))
                {
                    result.Add(id);
                }
            }

            return result;
        }

        /// <summary>
        /// Reads playlist.txt with FileShare.ReadWrite and short retries for truncate/torn races.
        /// </summary>
        public static List<Guid> TryReadOrderedGameIds(string playlistTxtPath)
        {
            if (string.IsNullOrWhiteSpace(playlistTxtPath) || !File.Exists(playlistTxtPath))
            {
                return new List<Guid>();
            }

            try
            {
                var content = ReadFileTextShared(playlistTxtPath);
                var parsed = ParsePlaylistLines(SplitLines(content));

                if (parsed.Count > 0)
                {
                    return parsed;
                }

                // Empty or unparseable — retry for truncate / torn write races.
                if (content.Length == 0)
                {
                    Thread.Sleep(RetrySleepMs);
                    content = ReadFileTextShared(playlistTxtPath);
                    return ParsePlaylistLines(SplitLines(content));
                }

                for (var attempt = 0; attempt < MaxNonEmptyContentRetries; attempt++)
                {
                    Thread.Sleep(RetrySleepMs);
                    content = ReadFileTextShared(playlistTxtPath);
                    parsed = ParsePlaylistLines(SplitLines(content));
                    if (parsed.Count > 0)
                    {
                        return parsed;
                    }
                }

                return parsed;
            }
            catch (IOException ex)
            {
                logger.Warn(ex, $"PlaylistOrderService: failed to read playlist file '{playlistTxtPath}'");
                return new List<Guid>();
            }
            catch (UnauthorizedAccessException ex)
            {
                logger.Warn(ex, $"PlaylistOrderService: access denied reading playlist file '{playlistTxtPath}'");
                return new List<Guid>();
            }
        }

        /// <summary>
        /// Reorders <paramref name="apps"/>: unmanaged, pinned∩playlist, pinned\playlist,
        /// non-pinned playlist, other managed. Returns whether the order changed.
        /// </summary>
        public static bool ReorderApps(
            JArray apps,
            IList<Guid> playlistGameIds,
            ManagedStore store,
            ICollection<Guid> pinnedGameIds)
        {
            if (apps == null || store == null || store.GameToUuid == null)
            {
                return false;
            }

            var playlistIds = playlistGameIds ?? (IList<Guid>)new List<Guid>();
            var pinnedIds = pinnedGameIds ?? (ICollection<Guid>)new List<Guid>();

            var managedUuidSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var uuid in store.GameToUuid.Values)
            {
                managedUuidSet.Add(ToApolloUuid(uuid));
            }

            var pinnedUuidSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pinnedId in pinnedIds)
            {
                Guid apolloUuid;
                if (store.GameToUuid.TryGetValue(pinnedId, out apolloUuid))
                {
                    pinnedUuidSet.Add(ToApolloUuid(apolloUuid));
                }
            }

            var orderedPlaylistUuids = new List<string>();
            var playlistUuidSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var gameId in playlistIds)
            {
                Guid apolloUuid;
                if (!store.GameToUuid.TryGetValue(gameId, out apolloUuid))
                {
                    continue;
                }

                var uuidStr = ToApolloUuid(apolloUuid);
                if (playlistUuidSet.Add(uuidStr))
                {
                    orderedPlaylistUuids.Add(uuidStr);
                }
            }

            var unmanaged = new List<JToken>();
            var uuidToToken = new Dictionary<string, JToken>(StringComparer.OrdinalIgnoreCase);
            var pinnedOnPlaylist = new List<string>();
            var pinnedNotOnPlaylist = new List<string>();
            var remaining = new List<string>();

            foreach (var token in apps)
            {
                var obj = token as JObject;
                if (obj == null)
                {
                    unmanaged.Add(token);
                    continue;
                }

                var uuidStr = ((string)obj["uuid"])?.Trim();
                if (string.IsNullOrEmpty(uuidStr) || !managedUuidSet.Contains(uuidStr))
                {
                    unmanaged.Add(token);
                    continue;
                }

                uuidStr = uuidStr.ToUpperInvariant();
                if (uuidToToken.ContainsKey(uuidStr))
                {
                    // Assume unique after Load; first occurrence wins.
                    continue;
                }

                uuidToToken[uuidStr] = token;

                if (pinnedUuidSet.Contains(uuidStr))
                {
                    if (playlistUuidSet.Contains(uuidStr))
                    {
                        pinnedOnPlaylist.Add(uuidStr);
                    }
                    else
                    {
                        pinnedNotOnPlaylist.Add(uuidStr);
                    }
                }
                else
                {
                    remaining.Add(uuidStr);
                }
            }

            var pinnedOnPlaylistSet = new HashSet<string>(pinnedOnPlaylist, StringComparer.OrdinalIgnoreCase);
            var remainingSet = new HashSet<string>(remaining, StringComparer.OrdinalIgnoreCase);
            var consumed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var newOrder = new List<JToken>(apps.Count);

            foreach (var token in unmanaged)
            {
                newOrder.Add(token);
            }

            // Pinned ∩ playlist (playlist.txt order)
            foreach (var uuidStr in orderedPlaylistUuids)
            {
                if (!pinnedOnPlaylistSet.Contains(uuidStr) || consumed.Contains(uuidStr))
                {
                    continue;
                }

                JToken token;
                if (uuidToToken.TryGetValue(uuidStr, out token))
                {
                    newOrder.Add(token);
                    consumed.Add(uuidStr);
                }
            }

            // Pinned \ playlist (relative order)
            foreach (var uuidStr in pinnedNotOnPlaylist)
            {
                JToken token;
                if (uuidToToken.TryGetValue(uuidStr, out token))
                {
                    newOrder.Add(token);
                    consumed.Add(uuidStr);
                }
            }

            // Non-pinned ∩ playlist (playlist.txt order)
            foreach (var uuidStr in orderedPlaylistUuids)
            {
                if (!remainingSet.Contains(uuidStr) || consumed.Contains(uuidStr))
                {
                    continue;
                }

                JToken token;
                if (uuidToToken.TryGetValue(uuidStr, out token))
                {
                    newOrder.Add(token);
                    consumed.Add(uuidStr);
                }
            }

            // Other managed (relative order)
            foreach (var uuidStr in remaining)
            {
                if (consumed.Contains(uuidStr))
                {
                    continue;
                }

                JToken token;
                if (uuidToToken.TryGetValue(uuidStr, out token))
                {
                    newOrder.Add(token);
                    consumed.Add(uuidStr);
                }
            }

            if (newOrder.Count == apps.Count)
            {
                var unchanged = true;
                for (var i = 0; i < apps.Count; i++)
                {
                    if (!ReferenceEquals(apps[i], newOrder[i]))
                    {
                        unchanged = false;
                        break;
                    }
                }

                if (unchanged)
                {
                    return false;
                }
            }

            apps.Clear();
            foreach (var token in newOrder)
            {
                apps.Add(token);
            }

            return true;
        }

        public static string GetPlaylistFilePath(IPlayniteAPI api)
        {
            if (api?.Paths?.ExtensionsDataPath == null)
            {
                return null;
            }

            return Path.Combine(api.Paths.ExtensionsDataPath, PlaylistPluginId.ToString(), PlaylistFileName);
        }

        private static string ToApolloUuid(Guid uuid)
        {
            return uuid.ToString().ToUpperInvariant();
        }

        private static string ReadFileTextShared(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(fs, Encoding.UTF8))
            {
                return reader.ReadToEnd();
            }
        }

        private static IEnumerable<string> SplitLines(string content)
        {
            if (content == null)
            {
                yield break;
            }

            using (var reader = new StringReader(content))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    yield return line;
                }
            }
        }
    }
}
