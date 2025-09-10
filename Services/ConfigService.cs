using Playnite.SDK;
using Newtonsoft.Json.Linq;
using System;
using System.IO;

namespace ApolloSync.Services
{
    public interface IConfigService
    {
        JObject Load(string path);
        void Save(string path, JObject config);
    }

    public class ConfigService : IConfigService
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private const string ApolloDir = "Apollo";
        private const string SunshineDir = "Sunshine";

        public JObject Load(string path)
        {
            try
            {
                var resolvedPath = string.IsNullOrWhiteSpace(path) ? ResolveDefaultPath(preferExisting: true) : path;
                logger.Debug($"ConfigService.Load - Resolved path: {resolvedPath}");

                if (string.IsNullOrWhiteSpace(resolvedPath) || !File.Exists(resolvedPath))
                {
                    logger.Debug($"ConfigService.Load - File doesn't exist, creating default config");
                    var j = new JObject
                    {
                        ["apps"] = new JArray(),
                        ["env"] = new JObject(),
                        ["version"] = 2
                    };
                    return j;
                }

                var json = File.ReadAllText(resolvedPath);
                var config = JObject.Parse(json);
                var appsCount = ((JArray)config["apps"])?.Count ?? 0;

                logger.Info($"ConfigService.Load - Successfully loaded apps.json from: {resolvedPath} with {appsCount} apps");
                return config;
            }
            catch (Exception e)
            {
                logger.Error(e, "ApolloSync: Failed to load apps.json");
                return null;
            }
        }

        public void Save(string path, JObject config)
        {
            try
            {
                var resolvedPath = string.IsNullOrWhiteSpace(path) ? ResolveDefaultPath(preferExisting: false) : path;
                logger.Debug($"ConfigService.Save - Resolved path: {resolvedPath}");

                var dir = Path.GetDirectoryName(resolvedPath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                    logger.Debug($"ConfigService.Save - Created directory: {dir}");
                }

                var jsonContent = config.ToString(Newtonsoft.Json.Formatting.Indented);
                logger.Debug($"ConfigService.Save - Writing config with {((JArray)config["apps"])?.Count ?? 0} apps");

                File.WriteAllText(resolvedPath, jsonContent);
                logger.Info($"ConfigService.Save - Successfully saved apps.json to: {resolvedPath}");
            }
            catch (Exception ex)
            {
                logger.Error(ex, $"ConfigService.Save - Failed to save apps.json to path: {path}");
                throw;
            }
        }

        private static string ResolveDefaultPath(bool preferExisting)
        {
            try
            {
                var programW6432 = Environment.GetEnvironmentVariable("ProgramW6432");
                var programFiles = !string.IsNullOrWhiteSpace(programW6432)
                    ? programW6432
                    : Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

                var apolloPath = Path.Combine(programFiles, ApolloDir, "config", "apps.json");
                var sunshinePath = Path.Combine(programFiles, SunshineDir, "config", "apps.json");

                if (preferExisting)
                {
                    if (File.Exists(apolloPath))
                    {
                        return apolloPath;
                    }
                    if (File.Exists(sunshinePath))
                    {
                        return sunshinePath;
                    }
                }

                // Prefer Apollo when not checking for existence
                return apolloPath;
            }
            catch
            {
                return null;
            }
        }
    }
}
