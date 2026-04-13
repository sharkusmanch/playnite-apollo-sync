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

        // ── Lock file ─────────────────────────────────────────────────────────────

        [TestMethod]
        public void LockFile_CreatedAndDeletedInTempDir()
        {
            // GetLockFilePath must return a path inside %TEMP% so that unprivileged
            // writes always succeed regardless of where apps.json lives.
            var gameId = Guid.NewGuid();
            var lockPath = SyncService.GetLockFilePath(gameId);
            var tempDir = System.IO.Path.GetTempPath().TrimEnd('\\', '/');

            Assert.IsTrue(
                lockPath.StartsWith(tempDir, StringComparison.OrdinalIgnoreCase),
                $"Lock file should be inside %TEMP% but was: {lockPath}");
        }

        [TestMethod]
        public void LockFile_FileStreamCreate_OverwritesStaleFile()
        {
            // Verify that FileMode.Create (used by OnGameStarted) succeeds when a
            // stale lock file from a crashed session already exists at the path.
            var lockPath = System.IO.Path.Combine(TestDir, "stale.lock");
            System.IO.File.WriteAllText(lockPath, "stale");

            // Should not throw — FileMode.Create overwrites.
            using (new System.IO.FileStream(lockPath, System.IO.FileMode.Create,
                       System.IO.FileAccess.Write, System.IO.FileShare.None)) { }

            Assert.IsTrue(System.IO.File.Exists(lockPath));
            Assert.AreEqual(0, new System.IO.FileInfo(lockPath).Length, "Stale content should be replaced");
        }

        [TestMethod]
        public void GetLockFilePath_IsConsistentForSameGame()
        {
            var gameId = Guid.NewGuid();
            var path1 = SyncService.GetLockFilePath(gameId);
            var path2 = SyncService.GetLockFilePath(gameId);

            Assert.AreEqual(path1, path2);
            Assert.IsTrue(path1.EndsWith($"apollosync-{gameId:N}.lock"),
                "Lock file name should embed the game ID");
        }

        [TestMethod]
        public void GetLockFilePath_DifferentGames_DifferentPaths()
        {
            var path1 = SyncService.GetLockFilePath(Guid.NewGuid());
            var path2 = SyncService.GetLockFilePath(Guid.NewGuid());
            Assert.AreNotEqual(path1, path2);
        }

        [TestMethod]
        public void AddOrUpdate_CmdIsSignalFileWrapperScript()
        {
            var service = new SyncService(imageCacheDir: TestCacheDir);
            var config = new JObject { ["apps"] = new JArray() };
            var store = new ManagedStore();
            var game = new Game("Test Game") { Id = Guid.NewGuid() };

            service.AddOrUpdate(config, store, game);

            var app = (JObject)((JArray)config["apps"])[0];
            var cmd = (string)app["cmd"];

            Assert.IsNotNull(cmd);
            Assert.IsTrue(cmd.StartsWith("powershell.exe"), "cmd should invoke powershell");
            Assert.IsTrue(cmd.Contains("-EncodedCommand"), "cmd should use -EncodedCommand to avoid quoting issues");

            // Decode and verify the embedded script is correct
            var encodedPart = cmd.Split(new[] { "-EncodedCommand " }, StringSplitOptions.None)[1].Trim();
            var decoded = System.Text.Encoding.Unicode.GetString(Convert.FromBase64String(encodedPart));

            var lockFileName = $"apollosync-{game.Id:N}.lock";
            Assert.IsTrue(decoded.Contains(lockFileName),
                "Decoded script should reference this game's lock file");
            Assert.IsTrue(decoded.Contains(game.Id.ToString()),
                "Decoded script should reference the game ID for the launch command");
            Assert.IsTrue(decoded.Contains("Test-Path"),
                "Decoded script should poll for the lock file");
        }

        [TestMethod]
        public void AddOrUpdate_Update_RemovesOrphanedDetachedField()
        {
            // Arrange: an entry that was written before the detached→cmd migration
            var service = new SyncService(imageCacheDir: TestCacheDir);
            var store = new ManagedStore();
            var gameId = Guid.NewGuid();
            var uuidStr = gameId.ToString().ToUpperInvariant();

            var config = new JObject
            {
                ["apps"] = new JArray
                {
                    new JObject
                    {
                        ["name"] = "Old Game",
                        ["uuid"] = uuidStr,
                        ["id"] = "12345",
                        ["detached"] = new JArray($"playnite://play/{gameId}")
                    }
                }
            };

            var game = new Game("Old Game") { Id = gameId };
            store.GameToUuid[gameId] = gameId;

            // Act: update the entry via the sync service
            var ok = service.AddOrUpdate(config, store, game);

            // Assert
            Assert.IsTrue(ok);
            var apps = (JArray)config["apps"];
            Assert.AreEqual(1, apps.Count, "Should not create a duplicate");
            var app = (JObject)apps[0];
            Assert.IsNotNull(app["cmd"], "cmd field should be set");
            Assert.IsNull(app["detached"], "detached field should be removed after migration");
        }

        [TestMethod]
        public void AddOrUpdate_Update_ClearsImagePathWhenCoverRemoved()
        {
            // Arrange: add a game with a cover image
            var service = new SyncService(imageCacheDir: TestCacheDir);
            var config = new JObject { ["apps"] = new JArray() };
            var store = new ManagedStore();
            var tempImage = Path.Combine(TestDir, Guid.NewGuid().ToString("N") + ".png");
            using (var bmp = new System.Drawing.Bitmap(1, 1))
                bmp.Save(tempImage, System.Drawing.Imaging.ImageFormat.Png);

            var game = new Game("Cover Game")
            {
                Id = Guid.NewGuid(),
                CoverImage = tempImage
            };
            service.AddOrUpdate(config, store, game);

            var app = (JObject)((JArray)config["apps"])[0];
            Assert.IsNotNull(app["image-path"], "Precondition: image-path should be set after first add");

            // Act: update the same game but with no cover
            game.CoverImage = null;
            service.AddOrUpdate(config, store, game);

            // Assert: image-path should be cleared from the existing entry
            app = (JObject)((JArray)config["apps"])[0];
            Assert.IsNull(app["image-path"], "image-path should be removed when game has no cover");
            Assert.AreEqual(1, ((JArray)config["apps"]).Count, "Should still be one entry");
        }

        [TestMethod]
        public void AddOrUpdate_Update_PreservesCustomFields()
        {
            // Custom fields that the user may have set in apps.json should survive a sync update
            var service = new SyncService(imageCacheDir: TestCacheDir);
            var store = new ManagedStore();
            var gameId = Guid.NewGuid();
            var uuidStr = gameId.ToString().ToUpperInvariant();

            var config = new JObject
            {
                ["apps"] = new JArray
                {
                    new JObject
                    {
                        ["name"] = "My Game",
                        ["uuid"] = uuidStr,
                        ["id"] = "99",
                        ["cmd"] = "old cmd",
                        ["custom-field"] = "user value"
                    }
                }
            };

            var game = new Game("My Game") { Id = gameId };
            store.GameToUuid[gameId] = gameId;

            service.AddOrUpdate(config, store, game);

            var app = (JObject)((JArray)config["apps"])[0];
            Assert.AreEqual("user value", (string)app["custom-field"], "Custom fields should be preserved");
            Assert.AreEqual("99", (string)app["id"], "id should be preserved");
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
