using System;
using System.Collections.Generic;

namespace ApolloSync.Models
{
    public class ManagedStore
    {
        public Dictionary<Guid, Guid> GameToUuid { get; set; } = new Dictionary<Guid, Guid>();
    }
}

