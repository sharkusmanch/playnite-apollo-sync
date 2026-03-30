using ApolloSync.Models;
using ApolloSync.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using Playnite.SDK.Models;
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

namespace ApolloSync.Tests
{
    [TestClass]
    public class SyncServiceTests
    {
        private static readonly string TestDir = Path.Combine(Path.GetTempPath(), "ApolloSyncTests");
        private static readonly string TestCacheDir = Path.Combine(Path.GetTempPath(), "ApolloSyncTests", "cache");

        [TestInitialize]
        public void Setup()
        {
            Directory.CreateDirectory(TestDir);
            Directory.CreateDirectory(TestCacheDir);
        }

        [TestCleanup]
        public void Cleanup()
        {
            if (Directory.Exists(TestDir))
            {
                Directory.Delete(TestDir, true);
            }
        }

        [TestMethod]
        public void AddOrUpdate_CreatesEntryAndUuid()
        {
            var service = new SyncService(imageCacheDir: TestCacheDir);
            var config = new JObject { ["apps"] = new JArray() };
            var store = new ManagedStore();
            var game = new Game("Test Game") { Id = Guid.NewGuid(), InstallDirectory = "C:\\Games\\Test" };

            var ok = service.AddOrUpdate(config, store, game);
            Assert.IsTrue(ok);
            Assert.AreEqual(1, ((JArray)config["apps"]).Count);
            Assert.IsTrue(store.GameToUuid.ContainsKey(game.Id));
        }

        [TestMethod]
        public void AddOrUpdate_UsesPngCoverImagePathDirectly()
        {
            var service = new SyncService(imageCacheDir: TestCacheDir);
            var config = new JObject { ["apps"] = new JArray() };
            var store = new ManagedStore();
            var tempImage = Path.Combine(TestDir, Guid.NewGuid().ToString("N") + ".png");
            using (var bmp = new Bitmap(1, 1))
            {
                bmp.Save(tempImage, ImageFormat.Png);
            }

            var game = new Game("Test Game")
            {
                Id = Guid.NewGuid(),
                InstallDirectory = "C:\\Games\\Test",
                CoverImage = tempImage
            };

            var ok = service.AddOrUpdate(config, store, game);
            Assert.IsTrue(ok);

            var app = (JObject)((JArray)config["apps"])[0];

            // PNG should be used directly without conversion
            Assert.IsNotNull(app["image-path"]);
            Assert.AreEqual(tempImage, (string)app["image-path"]);
        }

        [TestMethod]
        public void AddOrUpdate_ConvertsJpgToPng()
        {
            var service = new SyncService(imageCacheDir: TestCacheDir);
            var config = new JObject { ["apps"] = new JArray() };
            var store = new ManagedStore();
            var tempImage = Path.Combine(TestDir, Guid.NewGuid().ToString("N") + ".jpg");
            using (var bmp = new Bitmap(1, 1))
            {
                bmp.Save(tempImage, ImageFormat.Jpeg);
            }

            var game = new Game("Test Game")
            {
                Id = Guid.NewGuid(),
                InstallDirectory = "C:\\Games\\Test",
                CoverImage = tempImage
            };

            var ok = service.AddOrUpdate(config, store, game);
            Assert.IsTrue(ok);

            var app = (JObject)((JArray)config["apps"])[0];
            var imgPath = (string)app["image-path"];

            // Should point to a converted file in the cache dir, named by game UUID
            Assert.IsNotNull(imgPath);
            Assert.IsTrue(imgPath.EndsWith(".png", StringComparison.OrdinalIgnoreCase));
            Assert.IsTrue(imgPath.StartsWith(TestCacheDir));
            Assert.IsTrue(imgPath.Contains(game.Id.ToString("N")));
            Assert.IsTrue(File.Exists(imgPath));

            // Validate the output is actually a valid PNG by checking magic bytes
            var header = new byte[8];
            using (var fs = File.OpenRead(imgPath))
            {
                fs.Read(header, 0, 8);
            }
            // PNG magic: 89 50 4E 47 0D 0A 1A 0A
            Assert.AreEqual(0x89, header[0]);
            Assert.AreEqual(0x50, header[1]); // P
            Assert.AreEqual(0x4E, header[2]); // N
            Assert.AreEqual(0x47, header[3]); // G
        }

        [TestMethod]
        public void AddOrUpdate_ConvertsJpg_CacheHit()
        {
            var service = new SyncService(imageCacheDir: TestCacheDir);
            var store = new ManagedStore();
            var tempImage = Path.Combine(TestDir, Guid.NewGuid().ToString("N") + ".jpg");
            using (var bmp = new Bitmap(1, 1))
            {
                bmp.Save(tempImage, ImageFormat.Jpeg);
            }

            var game = new Game("Test Game")
            {
                Id = Guid.NewGuid(),
                InstallDirectory = "C:\\Games\\Test",
                CoverImage = tempImage
            };

            // First call converts
            var config1 = new JObject { ["apps"] = new JArray() };
            service.AddOrUpdate(config1, store, game);
            var imgPath = (string)((JObject)((JArray)config1["apps"])[0])["image-path"];
            var firstWriteTime = File.GetLastWriteTimeUtc(imgPath);

            // Second call should use cache (same write time)
            var config2 = new JObject { ["apps"] = new JArray() };
            service.AddOrUpdate(config2, store, game);
            var imgPath2 = (string)((JObject)((JArray)config2["apps"])[0])["image-path"];
            Assert.AreEqual(imgPath, imgPath2);
            Assert.AreEqual(firstWriteTime, File.GetLastWriteTimeUtc(imgPath2));
        }

        [TestMethod]
        public void AddOrUpdate_CorruptImage_SkipsGracefully()
        {
            var service = new SyncService(imageCacheDir: TestCacheDir);
            var config = new JObject { ["apps"] = new JArray() };
            var store = new ManagedStore();
            var tempImage = Path.Combine(TestDir, Guid.NewGuid().ToString("N") + ".jpg");
            File.WriteAllText(tempImage, "not a real image");

            var game = new Game("Test Game")
            {
                Id = Guid.NewGuid(),
                InstallDirectory = "C:\\Games\\Test",
                CoverImage = tempImage
            };

            var ok = service.AddOrUpdate(config, store, game);
            Assert.IsTrue(ok);

            var app = (JObject)((JArray)config["apps"])[0];
            // Corrupt image should result in no image-path, not a crash
            Assert.IsNull(app["image-path"]);
        }

        [TestMethod]
        public void AddOrUpdate_SkipsRemoteCoverImages()
        {
            var service = new SyncService(imageCacheDir: TestCacheDir);
            var config = new JObject { ["apps"] = new JArray() };
            var store = new ManagedStore();
            var game = new Game("Test Game")
            {
                Id = Guid.NewGuid(),
                InstallDirectory = "C:\\Games\\Test",
                CoverImage = "https://example.com/cover.jpg"
            };

            var ok = service.AddOrUpdate(config, store, game);
            Assert.IsTrue(ok);

            var app = (JObject)((JArray)config["apps"])[0];
            Assert.IsNull(app["image-path"]);
        }

        [TestMethod]
        public void Remove_DeletesEntryAndUuid()
        {
            var service = new SyncService(imageCacheDir: TestCacheDir);
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

        [TestMethod]
        public void Remove_CleansCachedImage()
        {
            var service = new SyncService(imageCacheDir: TestCacheDir);
            var config = new JObject { ["apps"] = new JArray() };
            var store = new ManagedStore();
            var tempImage = Path.Combine(TestDir, Guid.NewGuid().ToString("N") + ".jpg");
            using (var bmp = new Bitmap(1, 1))
            {
                bmp.Save(tempImage, ImageFormat.Jpeg);
            }

            var game = new Game("Test Game")
            {
                Id = Guid.NewGuid(),
                InstallDirectory = "C:\\Games\\Test",
                CoverImage = tempImage
            };

            service.AddOrUpdate(config, store, game);
            var imgPath = (string)((JObject)((JArray)config["apps"])[0])["image-path"];
            Assert.IsTrue(File.Exists(imgPath));

            service.Remove(config, store, game);
            Assert.IsFalse(File.Exists(imgPath));
        }
    }
}
