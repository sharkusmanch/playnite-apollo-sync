using ApolloSync.Models;
using Playnite.SDK;
using Playnite.SDK.Data;
using System;
using System.IO;

namespace ApolloSync.Services
{
    public interface IManagedStoreService
    {
        ManagedStore Load(string path);
        void Save(string path, ManagedStore store);
    }

    public class ManagedStoreService : IManagedStoreService
    {
        private static readonly ILogger logger = LogManager.GetLogger();

        public ManagedStore Load(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    return Serialization.FromJsonFile<ManagedStore>(path) ?? new ManagedStore();
                }
            }
            catch (Exception e)
            {
                logger.Error(e, "ApolloSync: Failed to load managed store");
            }
            return new ManagedStore();
        }

        public void Save(string path, ManagedStore store)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, Serialization.ToJson(store, true));
            }
            catch (Exception e)
            {
                logger.Error(e, "ApolloSync: Failed to save managed store");
            }
        }
    }
}
