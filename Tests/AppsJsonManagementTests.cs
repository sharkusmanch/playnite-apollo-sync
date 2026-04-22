using System;
using System.IO;
using System.Linq;
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

        [TestMethod]
        public void Remove_FilteredOut_NotPinned_Removes_Entry()
        {
            // This test simulates the logic of RemoveFilteredOutGames without using ApolloSync internals.
            // Arrange
            var config = new JObject { ["apps"] = new JArray() };
            var store = new ManagedStore();

            var g1 = new Playnite.SDK.Models.Game("Match") { Id = Guid.NewGuid() };
            var g2 = new Playnite.SDK.Models.Game("NoMatch") { Id = Guid.NewGuid() };

            var uuid1 = Guid.NewGuid();
            var uuid2 = Guid.NewGuid();
            store.GameToUuid[g1.Id] = uuid1;
            store.GameToUuid[g2.Id] = uuid2;

            var apps = (JArray)config["apps"];
            apps.Add(new JObject { ["name"] = g1.Name, ["uuid"] = uuid1.ToString().ToUpperInvariant() });
            apps.Add(new JObject { ["name"] = g2.Name, ["uuid"] = uuid2.ToString().ToUpperInvariant() });

            // Simulate filter: only g1 matches, g2 does not, and g2 is not pinned.
            var pinned = new HashSet<Guid>();
            Func<Playnite.SDK.Models.Game, bool> meetsFilter = game => game.Id == g1.Id;

            // Act: perform simplified removal similar to RemoveFilteredOutGames
            var removedCount = 0;
            var toRemoveApps = new System.Collections.Generic.List<JObject>();
            var toRemoveGames = new System.Collections.Generic.List<Guid>();
            foreach (var kv in store.GameToUuid.ToList())
            {
                var gameId = kv.Key;
                var appUuid = kv.Value;
                // Look up pseudo game by mapping
                var game = gameId == g1.Id ? g1 : g2;
                if (pinned.Contains(gameId)) continue;
                if (!meetsFilter(game))
                {
                    toRemoveGames.Add(gameId);
                    var app = apps.OfType<JObject>().FirstOrDefault(a => Guid.TryParse((string)a["uuid"], out var u) && u == appUuid);
                    if (app != null) toRemoveApps.Add(app);
                }
            }
            foreach (var a in toRemoveApps) { apps.Remove(a); removedCount++; }
            foreach (var gid in toRemoveGames) { Guid _removedVal; store.GameToUuid.TryRemove(gid, out _removedVal); }

            // Assert
            Assert.AreEqual(1, removedCount);
            Assert.AreEqual(1, apps.Count);
            Assert.IsTrue(store.GameToUuid.ContainsKey(g1.Id));
            Assert.IsFalse(store.GameToUuid.ContainsKey(g2.Id));
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

        [TestMethod]
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
