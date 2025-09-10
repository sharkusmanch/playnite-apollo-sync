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
            foreach (var gid in toRemoveGames) { store.GameToUuid.Remove(gid); }

            // Assert
            Assert.AreEqual(1, removedCount);
            Assert.AreEqual(1, apps.Count);
            Assert.IsTrue(store.GameToUuid.ContainsKey(g1.Id));
            Assert.IsFalse(store.GameToUuid.ContainsKey(g2.Id));
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
                    store.GameToUuid.Remove(gameId);
                }
            }

            // Assert
            Assert.AreEqual(0, removedCount);
            Assert.AreEqual(1, apps.Count);
            Assert.IsTrue(store.GameToUuid.ContainsKey(g.Id));
        }
    }
}
