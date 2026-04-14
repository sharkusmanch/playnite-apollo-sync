using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace ApolloSync.Models
{
    public class ManagedStore
    {
        public ConcurrentDictionary<Guid, Guid> GameToUuid { get; set; } = new ConcurrentDictionary<Guid, Guid>();
        public HashSet<Guid> ManuallyRemoved { get; set; } = new HashSet<Guid>();
    }
}

