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

        // ── Cover image normalisation ─────────────────────────────────────────────

        /// <summary>Writes real JPEG bytes to a file with whatever name is asked for.</summary>
        private static string WriteJpegNamed(string fileName, int width = 100, int height = 100)
        {
            var path = Path.Combine(TestDir, fileName);
            using (var bmp = new Bitmap(width, height))
            {
                bmp.Save(path, ImageFormat.Jpeg);
            }
            return path;
        }

        private static string WritePngNamed(string fileName, int width = 100, int height = 100)
        {
            var path = Path.Combine(TestDir, fileName);
            using (var bmp = new Bitmap(width, height))
            {
                bmp.Save(path, ImageFormat.Png);
            }
            return path;
        }

        private static Game GameWithCover(string coverPath)
        {
            return new Game("Test Game")
            {
                Id = Guid.NewGuid(),
                InstallDirectory = "C:\\Games\\Test",
                CoverImage = coverPath
            };
        }

        private static void AssertIsRealPng(string path)
        {
            var header = new byte[8];
            using (var fs = File.OpenRead(path))
            {
                fs.Read(header, 0, 8);
            }
            Assert.AreEqual(0x89, header[0], "not a PNG: byte 0");
            Assert.AreEqual(0x50, header[1], "not a PNG: byte 1 (P)");
            Assert.AreEqual(0x4E, header[2], "not a PNG: byte 2 (N)");
            Assert.AreEqual(0x47, header[3], "not a PNG: byte 3 (G)");
        }

        [TestMethod]
        public void AddOrUpdate_ConvertsJpegDisguisedAsPngFile()
        {
            // The reported bug: Playnite names cover files after the source URL, and metadata
            // providers routinely serve JPEG bytes from a .png URL. Trusting the extension handed
            // Apollo a JPEG it could not decode, so the game showed no artwork in Moonlight at all.
            var service = new SyncService(imageCacheDir: TestCacheDir);
            var config = new JObject { ["apps"] = new JArray() };
            var store = new ManagedStore();
            var jpegNamedPng = WriteJpegNamed(Guid.NewGuid().ToString("N") + ".png");

            var game = GameWithCover(jpegNamedPng);
            Assert.IsTrue(service.AddOrUpdate(config, store, game));

            var imgPath = (string)((JObject)((JArray)config["apps"])[0])["image-path"];
            Assert.IsNotNull(imgPath);
            Assert.AreNotEqual(jpegNamedPng, imgPath,
                "The mislabelled source must not be handed to Apollo as-is");
            Assert.IsTrue(imgPath.StartsWith(TestCacheDir), "Expected a converted file in the cache");
            AssertIsRealPng(imgPath);
        }

        [TestMethod]
        public void AddOrUpdate_NormalizesGenuinePngCoversToo()
        {
            // Even a real PNG needs the canvas normalised, so nothing is passed through untouched.
            var service = new SyncService(imageCacheDir: TestCacheDir);
            var config = new JObject { ["apps"] = new JArray() };
            var store = new ManagedStore();
            var realPng = WritePngNamed(Guid.NewGuid().ToString("N") + ".png", 1024, 1024);

            var game = GameWithCover(realPng);
            Assert.IsTrue(service.AddOrUpdate(config, store, game));

            var imgPath = (string)((JObject)((JArray)config["apps"])[0])["image-path"];
            Assert.AreNotEqual(realPng, imgPath, "Covers are normalised, not passed through");
            AssertIsRealPng(imgPath);

            // Asserting the dimensions too, so a ConvertToPng reduced to File.Copy would fail
            // this rather than sail through on the magic-byte check.
            using (var produced = Image.FromFile(imgPath))
            {
                Assert.AreEqual(600, produced.Width);
                Assert.AreEqual(800, produced.Height);
            }
        }

        [TestMethod]
        public void AddOrUpdate_ProducesCoverOnTheAspectRatioMoonlightExpects()
        {
            // Moonlight's grid force-scales every cover to 200x267 with QML's default Stretch
            // fill, so a square source arrives squeezed by 25% unless it is normalised first.
            // Literal numbers, not the CoverWidth/CoverHeight constants: comparing the output
            // against the very constant that produced it cannot fail.
            var service = new SyncService(imageCacheDir: TestCacheDir);
            var config = new JObject { ["apps"] = new JArray() };
            var store = new ManagedStore();
            var squareCover = WritePngNamed(Guid.NewGuid().ToString("N") + ".png", 1024, 1024);

            var game = GameWithCover(squareCover);
            Assert.IsTrue(service.AddOrUpdate(config, store, game));

            var imgPath = (string)((JObject)((JArray)config["apps"])[0])["image-path"];
            using (var produced = Image.FromFile(imgPath))
            {
                Assert.AreEqual(600, produced.Width);
                Assert.AreEqual(800, produced.Height);
                Assert.AreEqual(0.75, (double)produced.Width / produced.Height, 0.001);
            }
        }

        [TestMethod]
        public void AddOrUpdate_ActuallyDrawsTheCoverLetterboxedOntoTheCanvas()
        {
            // The geometry is well covered as a pure function, but nothing verified that the
            // conversion USES it. Without this, deleting the DrawImage call ships a fully
            // transparent 600x800 image for every game — blank artwork in Moonlight, which is
            // the exact failure this whole change exists to fix — with every other test green.
            var service = new SyncService(imageCacheDir: TestCacheDir);
            var config = new JObject { ["apps"] = new JArray() };
            var store = new ManagedStore();

            var coverPath = Path.Combine(TestDir, Guid.NewGuid().ToString("N") + ".png");
            using (var bmp = new Bitmap(1024, 1024))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Red);
                }
                bmp.Save(coverPath, ImageFormat.Png);
            }

            var game = GameWithCover(coverPath);
            Assert.IsTrue(service.AddOrUpdate(config, store, game));

            var imgPath = (string)((JObject)((JArray)config["apps"])[0])["image-path"];
            using (var produced = new Bitmap(imgPath))
            {
                // A 1024x1024 source fits the 600 width and is centred vertically, so rows
                // 100..699 hold the cover and everything outside stays transparent padding.
                var centre = produced.GetPixel(300, 400);
                Assert.AreEqual(255, centre.A, "the cover was never drawn onto the canvas");
                Assert.IsTrue(centre.R > 200 && centre.G < 60 && centre.B < 60,
                    $"expected the red cover at the centre, got {centre}");

                var padding = produced.GetPixel(300, 10);
                Assert.AreEqual(0, padding.A,
                    "the source was stretched to fill the canvas instead of being letterboxed");

                // The halo fix: edge pixels of the drawn area must be fully opaque, not blended
                // with the transparent canvas by the interpolation kernel.
                Assert.AreEqual(255, produced.GetPixel(0, 400).A, "translucent halo on the left edge");
                Assert.AreEqual(255, produced.GetPixel(599, 400).A, "translucent halo on the right edge");
                Assert.AreEqual(255, produced.GetPixel(300, 101).A, "translucent halo on the top edge");
            }
        }

        [TestMethod]
        public void AddOrUpdate_ConversionFailureLeavesExistingArtworkAlone()
        {
            // A cover that cannot be decoded must not clear box art that is already there.
            // TryGetCoverImagePath returns null for both "no cover" and "conversion failed", and
            // only the first is a reason to remove the field. Every cover now goes through
            // conversion, so this path is reachable where it never used to be.
            var service = new SyncService(imageCacheDir: TestCacheDir);
            var config = new JObject { ["apps"] = new JArray() };
            var store = new ManagedStore();

            var goodCover = WritePngNamed(Guid.NewGuid().ToString("N") + ".png");
            var game = GameWithCover(goodCover);
            Assert.IsTrue(service.AddOrUpdate(config, store, game));

            var app = (JObject)((JArray)config["apps"])[0];
            var originalPath = (string)app["image-path"];
            Assert.IsNotNull(originalPath);

            // The cover is replaced by something GDI+ cannot decode.
            var brokenCover = Path.Combine(TestDir, Guid.NewGuid().ToString("N") + ".png");
            File.WriteAllBytes(brokenCover, new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04, 0x05 });
            game.CoverImage = brokenCover;

            Assert.IsTrue(service.AddOrUpdate(config, store, game));

            Assert.AreEqual(originalPath, (string)app["image-path"],
                "A failed conversion must not strip the artwork that is already exported");
        }

        [TestMethod]
        public void AddOrUpdate_RemovingTheCoverStillClearsImagePath()
        {
            // The inverse of the above: a game that genuinely has no cover any more must lose
            // its image-path, or the entry points at art the game no longer has.
            var service = new SyncService(imageCacheDir: TestCacheDir);
            var config = new JObject { ["apps"] = new JArray() };
            var store = new ManagedStore();

            var game = GameWithCover(WritePngNamed(Guid.NewGuid().ToString("N") + ".png"));
            Assert.IsTrue(service.AddOrUpdate(config, store, game));

            var app = (JObject)((JArray)config["apps"])[0];
            Assert.IsNotNull(app["image-path"]);

            game.CoverImage = null;
            Assert.IsTrue(service.AddOrUpdate(config, store, game));

            Assert.IsNull(app["image-path"]);
        }

        // ── Letterbox geometry ────────────────────────────────────────────────────

        [TestMethod]
        public void GetLetterboxBounds_SquareSourceFillsWidthAndCentresVertically()
        {
            var r = SyncService.GetLetterboxBounds(1024, 1024, 600, 800);

            Assert.AreEqual(600, r.Width, "A square cover should fit the canvas width");
            Assert.AreEqual(600, r.Height, "and stay square — not stretch to the canvas height");
            Assert.AreEqual(0, r.X);
            Assert.AreEqual(100, r.Y, "padding split evenly top and bottom");
        }

        [TestMethod]
        public void GetLetterboxBounds_TallerThanCanvasFillsHeightAndCentresHorizontally()
        {
            // SteamGridDB's 600x900 (2:3) is taller than the 3:4 frame.
            var r = SyncService.GetLetterboxBounds(600, 900, 600, 800);

            Assert.AreEqual(800, r.Height);
            Assert.AreEqual(533, r.Width);
            Assert.AreEqual(0, r.Y);
            Assert.AreEqual(33, r.X);
        }

        [TestMethod]
        public void GetLetterboxBounds_ExactAspectRatioFillsTheCanvas()
        {
            var r = SyncService.GetLetterboxBounds(300, 400, 600, 800);

            Assert.AreEqual(new Rectangle(0, 0, 600, 800), r);
        }

        [TestMethod]
        public void GetLetterboxBounds_LandscapeSourceIsNotCropped()
        {
            var r = SyncService.GetLetterboxBounds(1920, 1080, 600, 800);

            Assert.AreEqual(600, r.Width);
            Assert.AreEqual(338, r.Height);
            Assert.IsTrue(r.Height <= 800);
        }

        [TestMethod]
        public void GetLetterboxBounds_PreservesTheSourceAspectRatio()
        {
            // Real cover shapes: SteamGridDB square and 2:3, Steam capsules, IGDB, plus a
            // landscape hero image in case someone uses one as a cover.
            foreach (var size in new[]
            {
                new Size(1024, 1024), new Size(600, 900), new Size(342, 482),
                new Size(1920, 1080), new Size(2160, 2160), new Size(264, 352)
            })
            {
                var r = SyncService.GetLetterboxBounds(size.Width, size.Height, 600, 800);
                var sourceRatio = (double)size.Width / size.Height;
                var drawnRatio = (double)r.Width / r.Height;

                // Tolerance covers rounding to whole pixels at these canvas sizes.
                Assert.AreEqual(sourceRatio, drawnRatio, sourceRatio * 0.01,
                    $"{size.Width}x{size.Height} was distorted");
            }
        }

        [TestMethod]
        public void GetLetterboxBounds_SubPixelDimensionClampsToOneAndAcceptsTheDistortion()
        {
            // A source so thin that fitting it would round its width to zero. Aspect ratio cannot
            // be honoured at integer pixels here, and a zero-width rectangle would draw nothing
            // at all, so the trade is deliberate: clamp to 1px and keep the image visible.
            // Not reachable from real cover art — documented so the clamp is not "fixed" later.
            var r = SyncService.GetLetterboxBounds(1, 3000, 600, 800);

            Assert.AreEqual(1, r.Width);
            Assert.AreEqual(800, r.Height);
            Assert.IsTrue(r.Right <= 600 && r.Bottom <= 800);
        }

        [TestMethod]
        public void GetLetterboxBounds_NeverExceedsTheCanvas()
        {
            foreach (var size in new[]
            {
                new Size(1024, 1024), new Size(601, 801), new Size(599, 799), new Size(3, 4000)
            })
            {
                var r = SyncService.GetLetterboxBounds(size.Width, size.Height, 600, 800);

                Assert.IsTrue(r.X >= 0 && r.Y >= 0, $"{size} placed off-canvas");
                Assert.IsTrue(r.Right <= 600 && r.Bottom <= 800, $"{size} overflowed the canvas");
            }
        }

        [TestMethod]
        public void GetLetterboxBounds_DegenerateSourceFallsBackToTheFullCanvas()
        {
            // A zero dimension would divide by zero; GDI+ can report this for damaged images.
            Assert.AreEqual(new Rectangle(0, 0, 600, 800), SyncService.GetLetterboxBounds(0, 0, 600, 800));
            Assert.AreEqual(new Rectangle(0, 0, 600, 800), SyncService.GetLetterboxBounds(100, 0, 600, 800));
        }

        // ── Opting out of cover management ────────────────────────────────────────

        [TestMethod]
        public void AddOrUpdate_WithCoverManagementOff_DoesNotSetImagePath()
        {
            var service = new SyncService(imageCacheDir: TestCacheDir, manageCoverImages: () => false);
            var config = new JObject { ["apps"] = new JArray() };
            var store = new ManagedStore();
            var game = GameWithCover(WritePngNamed(Guid.NewGuid().ToString("N") + ".png"));

            Assert.IsTrue(service.AddOrUpdate(config, store, game));

            var app = (JObject)((JArray)config["apps"])[0];
            Assert.IsNull(app["image-path"]);
            Assert.IsNotNull(app["cmd"], "everything else about the entry is still synced");
            Assert.AreEqual("Test Game", (string)app["name"]);
        }

        [TestMethod]
        public void AddOrUpdate_WithCoverManagementOff_LeavesUserSetArtworkAlone()
        {
            // The point of the opt-out: someone curating box art in Apollo's own UI must not have
            // it overwritten or cleared by the next sync.
            var service = new SyncService(imageCacheDir: TestCacheDir, manageCoverImages: () => false);
            var config = new JObject { ["apps"] = new JArray() };
            var store = new ManagedStore();
            var game = GameWithCover(WritePngNamed(Guid.NewGuid().ToString("N") + ".png"));

            Assert.IsTrue(service.AddOrUpdate(config, store, game));
            var app = (JObject)((JArray)config["apps"])[0];

            // The user sets their own artwork in Apollo.
            app["image-path"] = "D:\\MyArtwork\\custom.png";

            // A later sync updates the entry.
            game.Name = "Test Game Renamed";
            Assert.IsTrue(service.AddOrUpdate(config, store, game));

            Assert.AreEqual("D:\\MyArtwork\\custom.png", (string)app["image-path"],
                "User-set artwork must survive a sync when cover management is off");
            Assert.AreEqual("Test Game Renamed", (string)app["name"], "other fields still update");
        }

        [TestMethod]
        public void AddOrUpdate_WithCoverManagementOn_StillOwnsImagePath()
        {
            // The inverse: with management on, our value wins over whatever is there.
            var service = new SyncService(imageCacheDir: TestCacheDir, manageCoverImages: () => true);
            var config = new JObject { ["apps"] = new JArray() };
            var store = new ManagedStore();
            var game = GameWithCover(WritePngNamed(Guid.NewGuid().ToString("N") + ".png"));

            Assert.IsTrue(service.AddOrUpdate(config, store, game));
            var app = (JObject)((JArray)config["apps"])[0];
            app["image-path"] = "D:\\MyArtwork\\custom.png";

            Assert.IsTrue(service.AddOrUpdate(config, store, game));

            Assert.AreNotEqual("D:\\MyArtwork\\custom.png", (string)app["image-path"]);
            Assert.IsTrue(((string)app["image-path"]).StartsWith(TestCacheDir));
        }

        [TestMethod]
        public void ManageCoverImages_DefaultsToEnabled()
        {
            // Persisted only once written, so a flipped default would silently disable artwork
            // for every existing user on upgrade.
            Assert.IsTrue(new ApolloSyncSettings().ManageCoverImages);
        }

        [TestMethod]
        public void AddOrUpdate_CoverManagementIsReadPerCall()
        {
            // Read through a delegate, not captured at construction: the user can toggle the
            // setting while the plugin is loaded and the next sync must honour it.
            var enabled = false;
            var service = new SyncService(imageCacheDir: TestCacheDir, manageCoverImages: () => enabled);
            var config = new JObject { ["apps"] = new JArray() };
            var store = new ManagedStore();
            var game = GameWithCover(WritePngNamed(Guid.NewGuid().ToString("N") + ".png"));

            service.AddOrUpdate(config, store, game);
            var app = (JObject)((JArray)config["apps"])[0];
            Assert.IsNull(app["image-path"]);

            enabled = true;
            service.AddOrUpdate(config, store, game);

            Assert.IsNotNull(app["image-path"], "toggling the setting on must take effect immediately");
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
