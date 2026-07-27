using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Collections.Generic;
using ApolloSync.Models;
using ApolloSync.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using Playnite.SDK.Models;

namespace ApolloSync.Tests
{
    [TestClass]
    public class AppsJsonManagementTests
    {
        private static string CreateTempFilePath()
        {
            var dir = Path.Combine(Path.GetTempPath(), "ApolloSyncTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "apps.json");
        }

        [TestMethod]
        public void Persistence_Across_ConfigService_Writes()
        {
            // Arrange
            var configService = new ConfigService();
            var sync = new SyncService();
            var store = new ManagedStore();
            var path = CreateTempFilePath();

            var g1 = new Game("Game One") { Id = Guid.NewGuid(), InstallDirectory = "C:\\G1" };
            var g2 = new Game("Game Two") { Id = Guid.NewGuid(), InstallDirectory = "C:\\G2" };

            var config = configService.Load(path); // default empty structure
            Assert.IsNotNull(config);

            // Act
            Assert.IsTrue(sync.AddOrUpdate(config, store, g1));
            Assert.IsTrue(sync.AddOrUpdate(config, store, g2));
            configService.Save(path, config);

            // Reload
            var loaded = configService.Load(path);

            // Assert
            Assert.IsNotNull(loaded);
            var apps = (JArray)loaded["apps"];
            Assert.IsNotNull(apps);
            Assert.AreEqual(2, apps.Count);

            // Ensure names persisted and UUIDs match managed store
            var names = apps.Select(a => (string)((JObject)a)["name"]).ToList();
            CollectionAssert.AreEquivalent(new[] { "Game One", "Game Two" }, names);

            // UUID format is uppercase Guid string; verify parseable and in store
            foreach (var app in apps.OfType<JObject>())
            {
                var uuidStr = (string)app["uuid"];
                Assert.IsTrue(Guid.TryParse(uuidStr, out var uuid));

                // Check mapping exists (either game)
                Assert.IsTrue(store.GameToUuid.Values.Contains(uuid));
            }
        }

        [TestMethod]
        public void Save_KeepsBackupOfPreviousContents()
        {
            // The write must be survivable: the previous apps.json is recoverable from .bak
            // if the process dies before the new contents are committed.
            var configService = new ConfigService();
            var sync = new SyncService();
            var store = new ManagedStore();
            var path = CreateTempFilePath();

            var first = new Game("First") { Id = Guid.NewGuid(), InstallDirectory = "C:\\F" };
            var config = configService.Load(path);
            Assert.IsTrue(sync.AddOrUpdate(config, store, first));
            configService.Save(path, config);
            Assert.IsFalse(File.Exists(path + ".bak"), "No backup expected on first write");

            var second = new Game("Second") { Id = Guid.NewGuid(), InstallDirectory = "C:\\S" };
            Assert.IsTrue(sync.AddOrUpdate(config, store, second));
            configService.Save(path, config);

            Assert.IsTrue(File.Exists(path + ".bak"), "Second write should leave a backup");
            var backup = JObject.Parse(File.ReadAllText(path + ".bak"));
            var backupNames = ((JArray)backup["apps"]).Select(a => (string)((JObject)a)["name"]).ToList();
            CollectionAssert.AreEquivalent(new[] { "First" }, backupNames);

            var current = JObject.Parse(File.ReadAllText(path));
            var currentNames = ((JArray)current["apps"]).Select(a => (string)((JObject)a)["name"]).ToList();
            CollectionAssert.AreEquivalent(new[] { "First", "Second" }, currentNames);
        }

        [TestMethod]
        public void Remove_Selected_Single_Removes_From_Apps_And_Store()
        {
            // Arrange
            var sync = new SyncService();
            var store = new ManagedStore();
            var config = new JObject { ["apps"] = new JArray() };
            var game = new Game("To Remove") { Id = Guid.NewGuid(), InstallDirectory = "C:\\X" };

            Assert.IsTrue(sync.AddOrUpdate(config, store, game));
            Assert.AreEqual(1, ((JArray)config["apps"]).Count);
            Assert.IsTrue(store.GameToUuid.ContainsKey(game.Id));

            // Act
            var ok = sync.Remove(config, store, game);

            // Assert
            Assert.IsTrue(ok);
            Assert.AreEqual(0, ((JArray)config["apps"]).Count);
            Assert.IsFalse(store.GameToUuid.ContainsKey(game.Id));
        }

        // ── ShouldRemoveManagedGame ───────────────────────────────────────────────
        // The real decision rule used by RemoveFilteredOutGames. These previously
        // reimplemented the loop inline, which left the production method untested.

        [TestMethod]
        public void ShouldRemoveManagedGame_RemovesUnpinnedGameThatNoLongerMatches()
        {
            Assert.IsTrue(global::ApolloSync.ApolloSync.ShouldRemoveManagedGame(
                gameExistsInLibrary: true, isPinned: false, meetsFilters: false, filterEvaluationFailed: false));
        }

        [TestMethod]
        public void ShouldRemoveManagedGame_KeepsMatchingGame()
        {
            Assert.IsFalse(global::ApolloSync.ApolloSync.ShouldRemoveManagedGame(
                gameExistsInLibrary: true, isPinned: false, meetsFilters: true, filterEvaluationFailed: false));
        }

        [TestMethod]
        public void ShouldRemoveManagedGame_KeepsPinnedGameThatNoLongerMatches()
        {
            Assert.IsFalse(global::ApolloSync.ApolloSync.ShouldRemoveManagedGame(
                gameExistsInLibrary: true, isPinned: true, meetsFilters: false, filterEvaluationFailed: false));
        }

        [TestMethod]
        public void ShouldRemoveManagedGame_RemovesGameDeletedFromLibrary()
        {
            Assert.IsTrue(global::ApolloSync.ApolloSync.ShouldRemoveManagedGame(
                gameExistsInLibrary: false, isPinned: false, meetsFilters: false, filterEvaluationFailed: false));
        }

        [TestMethod]
        public void ShouldRemoveManagedGame_RemovesGameDeletedFromLibraryEvenWhenPinned()
        {
            // Pinning cannot preserve a game that no longer exists.
            Assert.IsTrue(global::ApolloSync.ApolloSync.ShouldRemoveManagedGame(
                gameExistsInLibrary: false, isPinned: true, meetsFilters: false, filterEvaluationFailed: false));
        }

        [TestMethod]
        public void ShouldRemoveManagedGame_KeepsGameWhenFilterEvaluationFailed()
        {
            // Regression: a throwing filter preset used to surface as "does not match", which
            // deleted every non-pinned managed entry on one transient failure.
            Assert.IsFalse(global::ApolloSync.ApolloSync.ShouldRemoveManagedGame(
                gameExistsInLibrary: true, isPinned: false, meetsFilters: false, filterEvaluationFailed: true));
        }

        // ── Removal wiring ────────────────────────────────────────────────────────

        [TestMethod]
        public void Remove_FilteredOut_NotPinned_Removes_Entry()
        {
            // Arrange
            var sync = new SyncService();
            var store = new ManagedStore();
            var config = new JObject { ["apps"] = new JArray() };

            var match = new Game("Match") { Id = Guid.NewGuid(), InstallDirectory = "C:\\M" };
            var noMatch = new Game("NoMatch") { Id = Guid.NewGuid(), InstallDirectory = "C:\\N" };

            Assert.IsTrue(sync.AddOrUpdate(config, store, match));
            Assert.IsTrue(sync.AddOrUpdate(config, store, noMatch));

            // Act: apply the production decision rule, then the production removal.
            var pinned = new HashSet<Guid>();
            foreach (var game in new[] { match, noMatch })
            {
                var meetsFilters = game.Id == match.Id;
                if (global::ApolloSync.ApolloSync.ShouldRemoveManagedGame(
                        gameExistsInLibrary: true,
                        isPinned: pinned.Contains(game.Id),
                        meetsFilters: meetsFilters,
                        filterEvaluationFailed: false))
                {
                    Assert.IsTrue(sync.Remove(config, store, game));
                }
            }

            // Assert
            var apps = (JArray)config["apps"];
            Assert.AreEqual(1, apps.Count);
            Assert.AreEqual("Match", (string)((JObject)apps[0])["name"]);
            Assert.IsTrue(store.GameToUuid.ContainsKey(match.Id));
            Assert.IsFalse(store.GameToUuid.ContainsKey(noMatch.Id));
        }

        // ── ManuallyRemoved round-trip ────────────────────────────────────────────

        [TestMethod]
        public void ManagedStore_ManuallyRemoved_RoundTripsThroughSettingsProjection()
        {
            // Mirrors LoadManagedStore/SaveManagedStore, which project the store to and from
            // plugin settings. The manual-removal record must survive a restart — inferring it
            // from "missing from apps.json" is what made the feature dead.
            var store = new ManagedStore();
            var kept = Guid.NewGuid();
            var removed = Guid.NewGuid();
            store.GameToUuid[kept] = Guid.NewGuid();
            store.MarkManuallyRemoved(removed);

            var mappings = new Dictionary<Guid, Guid>(store.GameToUuid);
            var manuallyRemoved = store.ManuallyRemovedSnapshot();

            var reloaded = new ManagedStore
            {
                GameToUuid = new System.Collections.Concurrent.ConcurrentDictionary<Guid, Guid>(mappings)
            };
            reloaded.ResetManuallyRemoved(manuallyRemoved);

            Assert.IsTrue(reloaded.GameToUuid.ContainsKey(kept));
            Assert.IsTrue(reloaded.IsManuallyRemoved(removed));
            Assert.IsFalse(reloaded.IsManuallyRemoved(kept));
        }

        [TestMethod]
        public void ManagedStore_ClearManualRemoval_AllowsGameToBeExportedAgain()
        {
            var store = new ManagedStore();
            var id = Guid.NewGuid();

            store.MarkManuallyRemoved(id);
            Assert.IsTrue(store.IsManuallyRemoved(id));

            store.ClearManualRemoval(id);
            Assert.IsFalse(store.IsManuallyRemoved(id));
        }

        [TestMethod]
        public void ManagedStore_ResetManuallyRemoved_HandlesNullAndReplacesContents()
        {
            var store = new ManagedStore();
            var stale = Guid.NewGuid();
            var fresh = Guid.NewGuid();

            store.MarkManuallyRemoved(stale);
            store.ResetManuallyRemoved(new[] { fresh });

            Assert.IsFalse(store.IsManuallyRemoved(stale), "Reset should replace, not merge");
            Assert.IsTrue(store.IsManuallyRemoved(fresh));

            // LoadManagedStore passes the settings list straight through, which may be null.
            store.ResetManuallyRemoved(null);
            Assert.AreEqual(0, store.ManuallyRemovedCount);
        }

        [TestMethod]
        public void ManagedStore_ManuallyRemoved_SurvivesConcurrentReadsAndWrites()
        {
            // The sync thread calls IsManuallyRemoved outside _configLock while UI-thread
            // handlers mark removals. A plain HashSet throws or returns garbage when a write
            // resizes it mid-read.
            var store = new ManagedStore();
            var probe = Guid.NewGuid();
            store.MarkManuallyRemoved(probe);

            var error = (Exception)null;
            var writer = new System.Threading.Thread(() =>
            {
                try
                {
                    for (var i = 0; i < 20000; i++)
                    {
                        store.MarkManuallyRemoved(Guid.NewGuid());
                    }
                }
                catch (Exception ex) { error = ex; }
            });

            var reader = new System.Threading.Thread(() =>
            {
                try
                {
                    for (var i = 0; i < 20000; i++)
                    {
                        Assert.IsTrue(store.IsManuallyRemoved(probe));
                    }
                }
                catch (Exception ex) { error = ex; }
            });

            writer.Start();
            reader.Start();
            writer.Join();
            reader.Join();

            Assert.IsNull(error, "Concurrent access threw: " + error);
        }

        [TestMethod]
        public void ManualRemoval_SurvivesAnExportWhoseSaveFails()
        {
            // Regression: ExportGamesWithFeedback used to clear the manual-removal record inside
            // the per-game loop, before the batch SaveAppsConfig. If that write threw, the record
            // was gone for the rest of the session even though apps.json never changed, so the
            // next cover-image change or sync silently re-added the game.
            //
            // Mirrors the production ordering: mutate in memory, then only clear the records
            // after the write succeeds.
            var store = new ManagedStore();
            var sync = new SyncService();
            var config = new JObject { ["apps"] = new JArray() };
            var game = new Game("Previously Removed") { Id = Guid.NewGuid(), InstallDirectory = "C:\\P" };

            store.MarkManuallyRemoved(game.Id);

            var exported = new List<Guid>();
            Assert.IsTrue(sync.AddOrUpdate(config, store, game));
            exported.Add(game.Id);

            // The write fails.
            var saveSucceeded = false;
            try
            {
                throw new UnauthorizedAccessException("apps.json is not writable");
            }
            catch (UnauthorizedAccessException)
            {
                saveSucceeded = false;
            }

            if (saveSucceeded)
            {
                foreach (var id in exported) store.ClearManualRemoval(id);
            }

            Assert.IsTrue(store.IsManuallyRemoved(game.Id),
                "A failed export must not drop the manual-removal protection");

            // And once a write does succeed, the protection is correctly lifted.
            foreach (var id in exported) store.ClearManualRemoval(id);
            Assert.IsFalse(store.IsManuallyRemoved(game.Id));
        }

        // ── Write fallback path ───────────────────────────────────────────────────

        [TestMethod]
        public void CopyThroughTempFile_BacksUpPreviousContentsBeforeOverwriting()
        {
            // This is the path that actually runs for a Program Files install, where the
            // directory is not writable and the atomic swap is unavailable.
            var path = CreateTempFilePath();
            File.WriteAllText(path, "{\"apps\":[{\"name\":\"Old\"}]}");

            ConfigService.CopyThroughTempFile(path, Encoding.UTF8.GetBytes("{\"apps\":[{\"name\":\"New\"}]}"));

            Assert.IsTrue(File.Exists(path + ".bak"), "Fallback write must leave a backup");
            StringAssert.Contains(File.ReadAllText(path + ".bak"), "Old");
            StringAssert.Contains(File.ReadAllText(path), "New");
        }

        [TestMethod]
        public void CopyThroughTempFile_CreatesDestinationWhenMissing()
        {
            var path = CreateTempFilePath();
            Assert.IsFalse(File.Exists(path));

            ConfigService.CopyThroughTempFile(path, Encoding.UTF8.GetBytes("{\"apps\":[]}"));

            Assert.IsTrue(File.Exists(path));
            Assert.IsFalse(File.Exists(path + ".bak"), "Nothing to back up when the file did not exist");
        }

        [TestMethod]
        public void GetFallbackBackupPath_IsDistinctPerConfigAndAValidFileName()
        {
            var apollo = ConfigService.GetFallbackBackupPath(@"C:\Program Files\Apollo\config\apps.json");
            var sunshine = ConfigService.GetFallbackBackupPath(@"C:\Program Files\Sunshine\config\apps.json");

            Assert.AreNotEqual(apollo, sunshine, "Apollo and Sunshine backups must not collide");
            CollectionAssert.DoesNotContain(Path.GetInvalidFileNameChars(), Path.GetFileName(apollo).ToCharArray()[0]);
            foreach (var c in Path.GetInvalidFileNameChars())
            {
                Assert.IsFalse(Path.GetFileName(apollo).Contains(c.ToString()), "Backup file name must be valid");
            }
        }

        // ── IsLocalAbsolutePath ───────────────────────────────────────────────────

        [TestMethod]
        public void IsLocalAbsolutePath_AcceptsStandardLocalPath()
        {
            Assert.IsTrue(ConfigService.IsLocalAbsolutePath(@"C:\Program Files\Apollo\config\apps.json"));
            Assert.IsTrue(ConfigService.IsLocalAbsolutePath(@"D:\data\apps.json"));
        }

        [TestMethod]
        public void IsLocalAbsolutePath_RejectsUncPaths()
        {
            Assert.IsFalse(ConfigService.IsLocalAbsolutePath(@"\\server\share\apps.json"));
            Assert.IsFalse(ConfigService.IsLocalAbsolutePath("//server/share/apps.json"));
        }

        [TestMethod]
        public void IsLocalAbsolutePath_RejectsRelativePaths()
        {
            Assert.IsFalse(ConfigService.IsLocalAbsolutePath("config\\apps.json"));
            Assert.IsFalse(ConfigService.IsLocalAbsolutePath("apps.json"));
            Assert.IsFalse(ConfigService.IsLocalAbsolutePath(".\\apps.json"));
        }

        [TestMethod]
        public void IsLocalAbsolutePath_RejectsBareRootAndEmpty()
        {
            Assert.IsFalse(ConfigService.IsLocalAbsolutePath(@"\apps.json")); // rooted but no drive
            Assert.IsFalse(ConfigService.IsLocalAbsolutePath(string.Empty));
            Assert.IsFalse(ConfigService.IsLocalAbsolutePath(null));
            Assert.IsFalse(ConfigService.IsLocalAbsolutePath("   "));
        }

        // ── ResolveConfigPath ─────────────────────────────────────────────────────

        [TestMethod]
        public void ResolveConfigPath_PassesThroughCustomPath()
        {
            var custom = @"D:\custom\apps.json";
            Assert.AreEqual(custom, ConfigService.ResolveConfigPath(custom, preferExisting: true));
            Assert.AreEqual(custom, ConfigService.ResolveConfigPath(custom, preferExisting: false));
        }

        // Reads ProgramW6432; serialize with the other tests that mutate it.
        [TestMethod]
        [DoNotParallelize]
        public void ResolveConfigPath_EmptyInputReturnsLocalAbsolutePath()
        {
            // Regression guard for issue #11: an empty AppsJsonPath setting must still
            // resolve to a concrete local absolute path so the permission-fix elevation
            // flow (which rejects non-local paths) can run.
            foreach (var input in new[] { null, string.Empty, "   " })
            {
                var resolved = ConfigService.ResolveConfigPath(input, preferExisting: false);
                Assert.IsTrue(
                    ConfigService.IsLocalAbsolutePath(resolved),
                    $"Expected local absolute path for input '{input ?? "<null>"}', got '{resolved}'");
                StringAssert.EndsWith(resolved, @"Apollo\config\apps.json");
            }
        }

        [TestMethod]
        [DoNotParallelize]
        public void Save_WithEmptyPath_WritesToSunshineWhenOnlySunshineInstalled()
        {
            // Regression guard: previously Save defaulted to preferExisting:false and would
            // create a new Apollo config even for Sunshine-only users, making their sync
            // silently write to a file nothing ever reads.
            var originalProgramW6432 = Environment.GetEnvironmentVariable("ProgramW6432");
            var fakeProgramFiles = Path.Combine(Path.GetTempPath(), "ApolloSyncTests", Guid.NewGuid().ToString("N"));
            var sunshineConfig = Path.Combine(fakeProgramFiles, "Sunshine", "config");
            var sunshineApps = Path.Combine(sunshineConfig, "apps.json");
            var apolloApps = Path.Combine(fakeProgramFiles, "Apollo", "config", "apps.json");

            try
            {
                Directory.CreateDirectory(sunshineConfig);
                File.WriteAllText(sunshineApps, "{\"apps\":[],\"env\":{},\"version\":2}");
                Environment.SetEnvironmentVariable("ProgramW6432", fakeProgramFiles);

                var config = new JObject
                {
                    ["apps"] = new JArray(new JObject { ["name"] = "Probe", ["uuid"] = Guid.NewGuid().ToString().ToUpperInvariant() }),
                    ["env"] = new JObject(),
                    ["version"] = 2
                };

                new ConfigService().Save(null, config);

                Assert.IsTrue(File.Exists(sunshineApps), "Save should have written to the existing Sunshine apps.json");
                Assert.IsFalse(File.Exists(apolloApps), "Save must not silently create an Apollo config when only Sunshine exists");

                var reloaded = JObject.Parse(File.ReadAllText(sunshineApps));
                Assert.AreEqual("Probe", (string)((JArray)reloaded["apps"])[0]["name"]);
            }
            finally
            {
                Environment.SetEnvironmentVariable("ProgramW6432", originalProgramW6432);
                // Best-effort temp cleanup: a locked file here shouldn't fail the test.
                try { Directory.Delete(fakeProgramFiles, recursive: true); } catch { }
            }
        }

        // Mutates ProgramW6432 at process scope — must not race with any other test
        // that reads it (i.e. anything calling ConfigService.ResolveConfigPath with an empty path).
        [TestMethod]
        [DoNotParallelize]
        public void ResolveConfigPath_PreferExisting_PicksSunshineWhenOnlySunshineInstalled()
        {
            var originalProgramW6432 = Environment.GetEnvironmentVariable("ProgramW6432");
            var fakeProgramFiles = Path.Combine(Path.GetTempPath(), "ApolloSyncTests", Guid.NewGuid().ToString("N"));
            var sunshineConfig = Path.Combine(fakeProgramFiles, "Sunshine", "config");
            var sunshineApps = Path.Combine(sunshineConfig, "apps.json");

            try
            {
                Directory.CreateDirectory(sunshineConfig);
                File.WriteAllText(sunshineApps, "{}");
                Environment.SetEnvironmentVariable("ProgramW6432", fakeProgramFiles);

                var resolvedExisting = ConfigService.ResolveConfigPath(null, preferExisting: true);
                Assert.AreEqual(sunshineApps, resolvedExisting);

                // With preferExisting=false we always get the Apollo path, even if only Sunshine exists.
                var resolvedDefault = ConfigService.ResolveConfigPath(null, preferExisting: false);
                Assert.AreEqual(Path.Combine(fakeProgramFiles, "Apollo", "config", "apps.json"), resolvedDefault);
            }
            finally
            {
                Environment.SetEnvironmentVariable("ProgramW6432", originalProgramW6432);
                // Best-effort temp cleanup: a locked file here shouldn't fail the test.
                try { Directory.Delete(fakeProgramFiles, recursive: true); } catch { }
            }
        }

        // ── Atomic write ──────────────────────────────────────────────────────────

        [TestMethod]
        public void ConfigService_Save_LeavesNoTempFile()
        {
            // After a successful save no .tmp file should remain
            var configService = new ConfigService();
            var path = CreateTempFilePath();
            var config = new JObject { ["apps"] = new JArray(), ["env"] = new JObject(), ["version"] = 2 };

            var tmpsBefore = Directory.EnumerateFiles(Path.GetTempPath(), "apollosync_*.tmp").ToHashSet();

            configService.Save(path, config);

            Assert.IsTrue(File.Exists(path), "apps.json should exist after save");
            var newTmps = Directory.EnumerateFiles(Path.GetTempPath(), "apollosync_*.tmp")
                .Where(f => !tmpsBefore.Contains(f))
                .ToList();
            Assert.AreEqual(0, newTmps.Count, "No apollosync_*.tmp files should remain in TEMP after a successful save");
        }

        [TestMethod]
        public void ConfigService_Save_OverwritesExistingFile()
        {
            // Saving twice should update the file, not append or fail
            var configService = new ConfigService();
            var sync = new SyncService();
            var store = new ManagedStore();
            var path = CreateTempFilePath();

            var g1 = new Game("Alpha") { Id = Guid.NewGuid() };
            var g2 = new Game("Beta") { Id = Guid.NewGuid() };

            // First save: one game
            var config = configService.Load(path);
            sync.AddOrUpdate(config, store, g1);
            configService.Save(path, config);

            // Second save: two games
            config = configService.Load(path);
            sync.AddOrUpdate(config, store, g2);
            configService.Save(path, config);

            // Reload and verify
            var loaded = configService.Load(path);
            var apps = (JArray)loaded["apps"];
            Assert.AreEqual(2, apps.Count, "Both games should be present after second save");
        }

        [TestMethod]
        public void ConfigService_Save_UnaffectedByStaleFilesNextToTarget()
        {
            // Temp files are now written to %TEMP%, not the Apollo directory, so any stale
            // files sitting alongside apps.json should have no effect on saves.
            var configService = new ConfigService();
            var path = CreateTempFilePath();

            // Pre-create a stale file that an old version of the plugin might have left behind
            File.WriteAllText(path + ".tmp", "{ corrupted json from old version");

            var config = new JObject
            {
                ["apps"] = new JArray(),
                ["env"] = new JObject(),
                ["version"] = 2
            };

            configService.Save(path, config);

            Assert.IsTrue(File.Exists(path), "apps.json should be written");

            // The written file should be valid JSON
            var loaded = configService.Load(path);
            Assert.IsNotNull(loaded);
            Assert.IsNotNull(loaded["apps"]);
        }

        [TestMethod]
        public void ConfigService_RoundTrip_PreservesUnknownFields()
        {
            // Fields not managed by this plugin (e.g. set by Apollo itself) should survive a
            // load → modify → save round-trip.
            var configService = new ConfigService();
            var sync = new SyncService();
            var store = new ManagedStore();
            var path = CreateTempFilePath();

            // Write a file with a custom top-level field and a custom per-app field
            var initial = new JObject
            {
                ["apps"] = new JArray
                {
                    new JObject
                    {
                        ["name"] = "Existing App",
                        ["uuid"] = Guid.NewGuid().ToString().ToUpperInvariant(),
                        ["id"] = "1",
                        ["cmd"] = "notepad.exe",
                        ["apollo-specific"] = "keep me"
                    }
                },
                ["env"] = new JObject(),
                ["version"] = 2,
                ["top-level-custom"] = "also keep me"
            };
            File.WriteAllText(path, initial.ToString(Newtonsoft.Json.Formatting.Indented));

            // Load, add a new game, save
            var config = configService.Load(path);
            var newGame = new Game("New Game") { Id = Guid.NewGuid() };
            sync.AddOrUpdate(config, store, newGame);
            configService.Save(path, config);

            // Reload and verify custom fields survived
            var reloaded = configService.Load(path);
            Assert.AreEqual("also keep me", (string)reloaded["top-level-custom"]);

            var existingApp = ((JArray)reloaded["apps"])
                .OfType<JObject>()
                .FirstOrDefault(a => (string)a["name"] == "Existing App");
            Assert.IsNotNull(existingApp, "Existing app entry should survive");
            Assert.AreEqual("keep me", (string)existingApp["apollo-specific"]);
        }

        [TestMethod]
        public void Remove_FilteredOut_Pinned_Skips_Removal()
        {
            // Arrange
            var config = new JObject { ["apps"] = new JArray() };
            var store = new ManagedStore();

            var g = new Playnite.SDK.Models.Game("Pinned") { Id = Guid.NewGuid() };
            var uuid = Guid.NewGuid();
            store.GameToUuid[g.Id] = uuid;
            ((JArray)config["apps"]).Add(new JObject { ["name"] = g.Name, ["uuid"] = uuid.ToString().ToUpperInvariant() });

            var pinned = new HashSet<Guid> { g.Id };
            Func<Playnite.SDK.Models.Game, bool> meetsFilter = _ => false; // filtered out

            // Act
            var apps = (JArray)config["apps"];
            var removedCount = 0;
            foreach (var kv in store.GameToUuid.ToList())
            {
                var gameId = kv.Key;
                var appUuid = kv.Value;
                var game = g; // only one
                if (pinned.Contains(gameId)) continue; // skip removal when pinned
                if (!meetsFilter(game))
                {
                    var app = apps.OfType<JObject>().FirstOrDefault(a => Guid.TryParse((string)a["uuid"], out var u) && u == appUuid);
                    if (app != null)
                    {
                        apps.Remove(app);
                        removedCount++;
                    }
                    Guid _removedVal; store.GameToUuid.TryRemove(gameId, out _removedVal);
                }
            }

            // Assert
            Assert.AreEqual(0, removedCount);
            Assert.AreEqual(1, apps.Count);
            Assert.IsTrue(store.GameToUuid.ContainsKey(g.Id));
        }
    }
}
