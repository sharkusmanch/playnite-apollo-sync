using ApolloSync.Models;
using ApolloSync.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ApolloSync.Tests
{
    [TestClass]
    public class PlaylistOrderTests
    {
        private static string ToUuid(Guid id)
        {
            return id.ToString().ToUpperInvariant();
        }

        private static JObject App(string name, Guid uuid)
        {
            return new JObject
            {
                ["name"] = name,
                ["uuid"] = ToUuid(uuid),
                ["id"] = "1"
            };
        }

        private static List<string> Names(JArray apps)
        {
            return apps.OfType<JObject>().Select(a => (string)a["name"]).ToList();
        }

        private static ManagedStore Store(params Guid[] gameIds)
        {
            var store = new ManagedStore();
            foreach (var id in gameIds)
            {
                store.GameToUuid[id] = id;
            }
            return store;
        }

        [TestMethod]
        public void IsPlaylistPresetIncluded_MatchesOrdinalPlaylistName()
        {
            var playlistId = Guid.NewGuid();
            var otherId = Guid.NewGuid();
            Assert.IsTrue(PlaylistOrderService.IsPlaylistPresetIncluded(
                new[] { otherId, playlistId },
                id => id == playlistId ? "Playlist" : "Favorites"));
        }

        [TestMethod]
        public void IsPlaylistPresetIncluded_FalseWhenMissingOrWrongName()
        {
            var id = Guid.NewGuid();
            Assert.IsFalse(PlaylistOrderService.IsPlaylistPresetIncluded(
                new[] { id },
                _ => "Favorites"));
            Assert.IsFalse(PlaylistOrderService.IsPlaylistPresetIncluded(
                new[] { id },
                _ => null));
            Assert.IsFalse(PlaylistOrderService.IsPlaylistPresetIncluded(
                null,
                _ => "Playlist"));
            Assert.IsFalse(PlaylistOrderService.IsPlaylistPresetIncluded(
                new Guid[0],
                _ => "Playlist"));
        }

        [TestMethod]
        public void IsPlaylistPresetIncluded_DoesNotMatchLocalizedOrWrongCase()
        {
            var id = Guid.NewGuid();
            Assert.IsFalse(PlaylistOrderService.IsPlaylistPresetIncluded(
                new[] { id },
                _ => "playlist"));
            Assert.IsFalse(PlaylistOrderService.IsPlaylistPresetIncluded(
                new[] { id },
                _ => "Liste de lecture"));
        }

        [TestMethod]
        public void ParsePlaylistLines_TrimsDedupesAndSkipsInvalid()
        {
            var a = Guid.NewGuid();
            var b = Guid.NewGuid();
            var lines = new[]
            {
                "  " + a.ToString().ToLowerInvariant() + "  ",
                "not-a-guid",
                "",
                a.ToString(),
                b.ToString().ToUpperInvariant(),
                null
            };

            var parsed = PlaylistOrderService.ParsePlaylistLines(lines);
            CollectionAssert.AreEqual(new[] { a, b }, parsed);
        }

        [TestMethod]
        public void TryReadOrderedGameIds_MissingFileReturnsEmpty()
        {
            var path = Path.Combine(Path.GetTempPath(), "ApolloSyncTests", Guid.NewGuid().ToString("N"), "missing.txt");
            var result = PlaylistOrderService.TryReadOrderedGameIds(path);
            Assert.AreEqual(0, result.Count);
        }

        [TestMethod]
        public void TryReadOrderedGameIds_ReadsSharedFile()
        {
            var dir = Path.Combine(Path.GetTempPath(), "ApolloSyncTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "playlist.txt");
            try
            {
                var a = Guid.NewGuid();
                var b = Guid.NewGuid();
                File.WriteAllLines(path, new[] { a.ToString(), b.ToString() });

                var result = PlaylistOrderService.TryReadOrderedGameIds(path);
                CollectionAssert.AreEqual(new[] { a, b }, result);
            }
            finally
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, true);
                }
            }
        }

        [TestMethod]
        public void ReorderApps_UnmanagedFirst_ThenPinnedHybrid_ThenPlaylist_ThenOther()
        {
            var desktop = Guid.NewGuid();
            var pinnedPlaylistA = Guid.NewGuid();
            var pinnedPlaylistB = Guid.NewGuid();
            var pinnedOther = Guid.NewGuid();
            var playlistOnly = Guid.NewGuid();
            var otherManaged = Guid.NewGuid();

            // Shuffled managed order in apps; playlist order is B then A then playlistOnly
            var apps = new JArray
            {
                App("OtherManaged", otherManaged),
                App("Desktop", desktop), // unmanaged — not in store
                App("PinnedPlaylistA", pinnedPlaylistA),
                App("PlaylistOnly", playlistOnly),
                App("PinnedOther", pinnedOther),
                App("PinnedPlaylistB", pinnedPlaylistB),
            };

            var store = Store(pinnedPlaylistA, pinnedPlaylistB, pinnedOther, playlistOnly, otherManaged);
            var playlist = new List<Guid> { pinnedPlaylistB, pinnedPlaylistA, playlistOnly };
            var pinned = new HashSet<Guid> { pinnedPlaylistA, pinnedPlaylistB, pinnedOther };

            Assert.IsTrue(PlaylistOrderService.ReorderApps(apps, playlist, store, pinned));

            CollectionAssert.AreEqual(
                new[]
                {
                    "Desktop",
                    "PinnedPlaylistB",
                    "PinnedPlaylistA",
                    "PinnedOther",
                    "PlaylistOnly",
                    "OtherManaged"
                },
                Names(apps));
        }

        [TestMethod]
        public void ReorderApps_PinnedAndOnPlaylistAppearsOnlyInPinnedPlaylistBucket()
        {
            var both = Guid.NewGuid();
            var apps = new JArray
            {
                App("Both", both),
            };
            var store = Store(both);
            var playlist = new List<Guid> { both };
            var pinned = new HashSet<Guid> { both };

            Assert.IsFalse(PlaylistOrderService.ReorderApps(apps, playlist, store, pinned));
            Assert.AreEqual(1, apps.Count);
            Assert.AreEqual("Both", (string)apps[0]["name"]);
        }

        [TestMethod]
        public void ReorderApps_EmptyPlaylist_PinnedThenOther_RelativeOrder()
        {
            var pinned1 = Guid.NewGuid();
            var pinned2 = Guid.NewGuid();
            var other = Guid.NewGuid();
            var desktop = Guid.NewGuid();

            var apps = new JArray
            {
                App("Other", other),
                App("Pinned2", pinned2),
                App("Desktop", desktop),
                App("Pinned1", pinned1),
            };

            var store = Store(pinned1, pinned2, other);
            var changed = PlaylistOrderService.ReorderApps(apps, new List<Guid>(), store, new HashSet<Guid> { pinned1, pinned2 });
            Assert.IsTrue(changed);

            CollectionAssert.AreEqual(
                new[] { "Desktop", "Pinned2", "Pinned1", "Other" },
                Names(apps));
        }

        [TestMethod]
        public void ReorderApps_EmptyPlaylistNoPins_UnmanagedThenAllManagedRelative()
        {
            var a = Guid.NewGuid();
            var b = Guid.NewGuid();
            var desktop = Guid.NewGuid();

            var apps = new JArray
            {
                App("A", a),
                App("Desktop", desktop),
                App("B", b),
            };

            var store = Store(a, b);
            Assert.IsTrue(PlaylistOrderService.ReorderApps(apps, new List<Guid>(), store, new HashSet<Guid>()));

            CollectionAssert.AreEqual(new[] { "Desktop", "A", "B" }, Names(apps));
        }

        [TestMethod]
        public void ReorderApps_LowercasePlaylistGuidsMatchUppercaseAppsUuids()
        {
            var gameId = Guid.NewGuid();
            var apps = new JArray
            {
                App("Other", Guid.NewGuid()),
                App("Game", gameId),
            };
            // Put other managed first so reorder must move Game
            var other = Guid.Parse((string)apps[0]["uuid"]);
            var store = Store(other, gameId);

            // playlist list uses the Guid value (same as game id); Reorder compares via store + uppercase
            Assert.IsTrue(PlaylistOrderService.ReorderApps(
                apps,
                new List<Guid> { gameId },
                store,
                new HashSet<Guid>()));

            Assert.AreEqual("Game", (string)apps[0]["name"]);
            Assert.AreEqual("Other", (string)apps[1]["name"]);
        }

        [TestMethod]
        public void ReorderApps_NonIdentityStoreMapping()
        {
            var gameId = Guid.NewGuid();
            var apolloUuid = Guid.NewGuid();
            var apps = new JArray
            {
                App("Other", Guid.NewGuid()),
                App("Mapped", apolloUuid),
            };
            var other = Guid.Parse((string)apps[0]["uuid"]);

            var store = new ManagedStore();
            store.GameToUuid[other] = other;
            store.GameToUuid[gameId] = apolloUuid;

            Assert.IsTrue(PlaylistOrderService.ReorderApps(
                apps,
                new List<Guid> { gameId },
                store,
                new HashSet<Guid>()));

            Assert.AreEqual("Mapped", (string)apps[0]["name"]);
            Assert.AreEqual("Other", (string)apps[1]["name"]);
        }

        [TestMethod]
        public void ReorderApps_NullAppsReturnsFalse()
        {
            Assert.IsFalse(PlaylistOrderService.ReorderApps(
                null,
                new List<Guid>(),
                new ManagedStore(),
                new HashSet<Guid>()));
        }

        [TestMethod]
        public void ReorderApps_NullPlaylistAndPinnedTreatedAsEmpty()
        {
            var managed = Guid.NewGuid();
            var desktop = Guid.NewGuid();
            var apps = new JArray
            {
                App("Managed", managed),
                App("Desktop", desktop),
            };
            var store = Store(managed);

            Assert.IsTrue(PlaylistOrderService.ReorderApps(apps, null, store, null));
            CollectionAssert.AreEqual(new[] { "Desktop", "Managed" }, Names(apps));
        }

        [TestMethod]
        public void ReorderApps_UnchangedOrderReturnsFalse()
        {
            var a = Guid.NewGuid();
            var b = Guid.NewGuid();
            var apps = new JArray
            {
                App("Desktop", Guid.NewGuid()),
                App("A", a),
                App("B", b),
            };
            // Desktop not in store
            var store = Store(a, b);
            var playlist = new List<Guid> { a, b };

            Assert.IsFalse(PlaylistOrderService.ReorderApps(apps, playlist, store, new HashSet<Guid>()));
        }
    }
}
