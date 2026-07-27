using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace ApolloSync.Models
{
    public class ManagedStore
    {
        public ConcurrentDictionary<Guid, Guid> GameToUuid { get; set; } = new ConcurrentDictionary<Guid, Guid>();

        // Games the user explicitly removed from Apollo management. Recorded explicitly rather
        // than inferred from "in the store but missing from apps.json", because the startup
        // reconciliation prunes exactly those entries and would erase the signal.
        //
        // Access goes through the methods below rather than exposing the set: it is read from
        // the background sync thread and written from UI-thread handlers, and those two are not
        // serialized against each other by _configLock (the sync reads outside the lock).
        private readonly HashSet<Guid> _manuallyRemoved = new HashSet<Guid>();
        private readonly object _manuallyRemovedLock = new object();

        /// <summary>True if the user explicitly removed this game from Apollo management.</summary>
        public bool IsManuallyRemoved(Guid gameId)
        {
            lock (_manuallyRemovedLock)
            {
                return _manuallyRemoved.Contains(gameId);
            }
        }

        /// <summary>Records that the user explicitly removed this game.</summary>
        public void MarkManuallyRemoved(Guid gameId)
        {
            lock (_manuallyRemovedLock)
            {
                _manuallyRemoved.Add(gameId);
            }
        }

        /// <summary>Clears the manual-removal record, e.g. when the user exports the game again.</summary>
        public void ClearManualRemoval(Guid gameId)
        {
            lock (_manuallyRemovedLock)
            {
                _manuallyRemoved.Remove(gameId);
            }
        }

        /// <summary>Snapshot for persistence and for iteration without holding the lock.</summary>
        public List<Guid> ManuallyRemovedSnapshot()
        {
            lock (_manuallyRemovedLock)
            {
                return _manuallyRemoved.ToList();
            }
        }

        /// <summary>Replaces the whole set, used when loading from settings.</summary>
        public void ResetManuallyRemoved(IEnumerable<Guid> gameIds)
        {
            lock (_manuallyRemovedLock)
            {
                _manuallyRemoved.Clear();
                if (gameIds != null)
                {
                    foreach (var id in gameIds)
                    {
                        _manuallyRemoved.Add(id);
                    }
                }
            }
        }

        public int ManuallyRemovedCount
        {
            get { lock (_manuallyRemovedLock) { return _manuallyRemoved.Count; } }
        }
    }
}
