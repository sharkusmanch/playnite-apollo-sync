using ApolloSync.Models;
using ApolloSync.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using Playnite.SDK.Models;
using System;

namespace ApolloSync.Tests
{
    [TestClass]
    public class SyncServiceTests
    {
        [TestMethod]
        public void AddOrUpdate_CreatesEntryAndUuid()
        {
            var service = new SyncService();
            var config = new JObject { ["apps"] = new JArray() };
            var store = new ManagedStore();
            var game = new Game("Test Game") { Id = Guid.NewGuid(), InstallDirectory = "C:\\Games\\Test" };

            var ok = service.AddOrUpdate(config, store, game);
            Assert.IsTrue(ok);
            Assert.AreEqual(1, ((JArray)config["apps"]).Count);
            Assert.IsTrue(store.GameToUuid.ContainsKey(game.Id));
        }

        [TestMethod]
        public void AddOrUpdate_UsesCoverImagePathDirectly()
        {
            var service = new SyncService();
            var config = new JObject { ["apps"] = new JArray() };
            var store = new ManagedStore();
            // Create a real temp image file so TryGetCoverImagePath returns it
            var tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ApolloSyncTests");
            System.IO.Directory.CreateDirectory(tempDir);
            var tempImage = System.IO.Path.Combine(tempDir, Guid.NewGuid().ToString("N") + ".jpg");
            System.IO.File.WriteAllText(tempImage, "dummy");

            var game = new Game("Test Game")
            {
                Id = Guid.NewGuid(),
                InstallDirectory = "C:\\Games\\Test",
                CoverImage = tempImage  // Local file path
            };

            var ok = service.AddOrUpdate(config, store, game);
            Assert.IsTrue(ok);

            var apps = (JArray)config["apps"];
            Assert.AreEqual(1, apps.Count);

            var app = (JObject)apps[0];

            // Should have image-path pointing directly to the cover image
            Assert.IsNotNull(app["image-path"]);
            Assert.AreEqual(tempImage, (string)app["image-path"]);
        }

        [TestMethod]
        public void AddOrUpdate_SkipsRemoteCoverImages()
        {
            var service = new SyncService();
            var config = new JObject { ["apps"] = new JArray() };
            var store = new ManagedStore();
            var game = new Game("Test Game")
            {
                Id = Guid.NewGuid(),
                InstallDirectory = "C:\\Games\\Test",
                CoverImage = "https://example.com/cover.jpg"  // Remote URL
            };

            var ok = service.AddOrUpdate(config, store, game);
            Assert.IsTrue(ok);

            var apps = (JArray)config["apps"];
            Assert.AreEqual(1, apps.Count);

            var app = (JObject)apps[0];

            // Should not have image-path for remote images
            Assert.IsNull(app["image-path"]);
        }

        [TestMethod]
        public void Remove_DeletesEntryAndUuid()
        {
            var service = new SyncService();
            var config = new JObject { ["apps"] = new JArray() };
            var store = new ManagedStore();
            var game = new Game("Test Game") { Id = Guid.NewGuid(), InstallDirectory = "C:\\Games\\Test" };

            var ok1 = service.AddOrUpdate(config, store, game);
            Assert.IsTrue(ok1);
            var ok2 = service.Remove(config, store, game);
            Assert.IsTrue(ok2);
            Assert.AreEqual(0, ((JArray)config["apps"]).Count);
            Assert.IsFalse(store.GameToUuid.ContainsKey(game.Id));
        }
    }
}
