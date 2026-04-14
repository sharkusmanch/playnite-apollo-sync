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
            var tmpPath = Path.Combine(Path.GetTempPath(), "apollosync_store_" + Path.GetRandomFileName() + ".tmp");
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var jsonContent = Serialization.ToJson(store, true);
                try
                {
                    using (var fs = new FileStream(tmpPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        var bytes = System.Text.Encoding.UTF8.GetBytes(jsonContent);
                        fs.Write(bytes, 0, bytes.Length);
                    }
                    File.Copy(tmpPath, path, overwrite: true);
                }
                finally
                {
                    try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
                }
            }
            catch (Exception e)
            {
                logger.Error(e, "ApolloSync: Failed to save managed store");
            }
        }
    }
}
