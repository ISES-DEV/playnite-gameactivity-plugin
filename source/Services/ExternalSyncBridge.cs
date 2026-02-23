using GameActivity.Models;
using Playnite.SDK;
using Playnite.SDK.Data;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace GameActivity.Services
{
    public static class ExternalSyncBridge
    {
        private static readonly object ImportLock = new object();

        public static string ExportSessionsJson()
        {
            try
            {
                var sessions = ExportSessionsInternal();
                return Serialization.ToJson(sessions);
            }
            catch
            {
                return "[]";
            }
        }

        public static string ImportSessionsJson(string sessionsJson)
        {
            var result = new ExternalImportResult();
            try
            {
                var sessions = Serialization.FromJson<List<ExternalSessionDto>>(sessionsJson) ?? new List<ExternalSessionDto>();
                ImportSessionsInternal(sessions, result);
            }
            catch (Exception ex)
            {
                result.Errors++;
                result.Error = ex.Message;
            }

            return Serialization.ToJson(result);
        }

        private static List<ExternalSessionDto> ExportSessionsInternal()
        {
            var pluginDatabase = GameActivity.PluginDatabase;
            if (pluginDatabase == null || !pluginDatabase.IsLoaded)
            {
                return new List<ExternalSessionDto>();
            }

            lock (ImportLock)
            {
                var result = new List<ExternalSessionDto>();
                var configs = pluginDatabase.LocalSystem?.GetConfigurations() ?? new List<CommonPluginsShared.SystemConfiguration>();

                foreach (var gameActivities in pluginDatabase.GetListGameActivity() ?? Enumerable.Empty<GameActivities>())
                {
                    var game = gameActivities?.Game;
                    if (game == null || game.Id == Guid.Empty)
                    {
                        continue;
                    }

                    foreach (var activity in gameActivities.Items ?? Enumerable.Empty<Activity>())
                    {
                        if (activity == null || !activity.DateSession.HasValue)
                        {
                            continue;
                        }

                        var configName =
                            activity.IdConfiguration >= 0 &&
                            activity.IdConfiguration < configs.Count
                                ? configs[activity.IdConfiguration]?.Name
                                : null;

                        result.Add(new ExternalSessionDto
                        {
                            GameId = game.Id.ToString(),
                            SourceId = (activity.SourceID == Guid.Empty ? (game.SourceId ?? Guid.Empty) : activity.SourceID).ToString(),
                            PlatformIds = (activity.PlatformIDs ?? game.PlatformIds ?? new List<Guid>()).Select(x => x.ToString()).ToList(),
                            IdConfiguration = activity.IdConfiguration,
                            ConfigurationName = configName,
                            GameActionName = activity.GameActionName,
                            DateSessionUtc = activity.DateSession.Value.ToUniversalTime(),
                            ElapsedSeconds = activity.ElapsedSeconds
                        });
                    }
                }

                return result;
            }
        }

        private static void ImportSessionsInternal(IEnumerable<ExternalSessionDto> sessions, ExternalImportResult result)
        {
            var pluginDatabase = GameActivity.PluginDatabase;
            if (pluginDatabase == null || !pluginDatabase.IsLoaded)
            {
                result.Errors++;
                result.Error = "Plugin database is not loaded.";
                return;
            }

            lock (ImportLock)
            {
                foreach (var session in sessions ?? Enumerable.Empty<ExternalSessionDto>())
                {
                    try
                    {
                        if (!Guid.TryParse(session.GameId, out var gameId))
                        {
                            result.Skipped++;
                            continue;
                        }

                        var game = API.Instance.Database.Games.Get(gameId);
                        if (game == null)
                        {
                            result.Skipped++;
                            continue;
                        }

                        var gameActivities = pluginDatabase.Get(game);
                        if (gameActivities == null)
                        {
                            result.Skipped++;
                            continue;
                        }

                        var sourceId = ParseGuidOrDefault(session.SourceId, game.SourceId);
                        var platformIds = ParseGuids(session.PlatformIds, game.PlatformIds);
                        var dateSession = session.DateSessionUtc.ToUniversalTime();
                        var actionName = string.IsNullOrWhiteSpace(session.GameActionName)
                            ? ResourceProvider.GetString("LOCGameActivityDefaultAction")
                            : session.GameActionName;

                        var idConfiguration = ResolveConfigurationIndex(pluginDatabase, session.ConfigurationName, session.IdConfiguration);
                        var existing = gameActivities.Items.FirstOrDefault(x =>
                            x.DateSession.HasValue &&
                            x.DateSession.Value == dateSession &&
                            x.IdConfiguration == idConfiguration &&
                            string.Equals(x.GameActionName ?? string.Empty, actionName, StringComparison.Ordinal));

                        if (existing == null)
                        {
                            gameActivities.Items.Add(new Activity
                            {
                                IdConfiguration = idConfiguration,
                                GameActionName = actionName,
                                DateSession = dateSession,
                                SourceID = sourceId,
                                PlatformIDs = platformIds,
                                ElapsedSeconds = session.ElapsedSeconds
                            });
                            result.Applied++;
                        }
                        else if (session.ElapsedSeconds > existing.ElapsedSeconds)
                        {
                            existing.ElapsedSeconds = session.ElapsedSeconds;
                            result.Updated++;
                        }
                        else
                        {
                            result.Skipped++;
                        }

                        _ = gameActivities.ItemsDetails.Items.TryAdd(dateSession, new List<ActivityDetailsData>());
                        pluginDatabase.Update(gameActivities);
                    }
                    catch (Exception ex)
                    {
                        result.Errors++;
                        result.Error = ex.Message;
                    }
                }
            }
        }

        private static Guid ParseGuidOrDefault(string value, Guid fallback)
        {
            return Guid.TryParse(value, out var parsed) ? parsed : fallback;
        }

        private static List<Guid> ParseGuids(IEnumerable<string> values, IEnumerable<Guid> fallback)
        {
            var parsed = new List<Guid>();
            foreach (var item in values ?? Enumerable.Empty<string>())
            {
                if (Guid.TryParse(item, out var id))
                {
                    parsed.Add(id);
                }
            }

            if (parsed.Count > 0)
            {
                return parsed;
            }

            return (fallback ?? Enumerable.Empty<Guid>()).ToList();
        }

        private static int ResolveConfigurationIndex(ActivityDatabase pluginDatabase, string configurationName, int fallbackId)
        {
            var configs = pluginDatabase?.LocalSystem?.GetConfigurations() ?? new List<CommonPluginsShared.SystemConfiguration>();

            if (!string.IsNullOrWhiteSpace(configurationName))
            {
                var trimmed = configurationName.Trim();
                var existing = configs.FindIndex(x => string.Equals(x?.Name, trimmed, StringComparison.OrdinalIgnoreCase));
                if (existing >= 0)
                {
                    return existing;
                }

                configs.Add(new CommonPluginsShared.SystemConfiguration
                {
                    Name = trimmed
                });
                SaveConfigurations(pluginDatabase, configs);
                return configs.Count - 1;
            }

            if (fallbackId >= 0 && fallbackId < configs.Count)
            {
                return fallbackId;
            }

            return Math.Max(0, pluginDatabase?.LocalSystem?.GetIdConfiguration() ?? 0);
        }

        private static void SaveConfigurations(ActivityDatabase pluginDatabase, List<CommonPluginsShared.SystemConfiguration> configs)
        {
            try
            {
                var root = pluginDatabase?.Paths?.PluginUserDataPath;
                if (string.IsNullOrWhiteSpace(root))
                {
                    return;
                }

                Directory.CreateDirectory(root);
                var path = Path.Combine(root, "Configurations.json");
                File.WriteAllText(path, Serialization.ToJson(configs));
            }
            catch
            {
            }
        }
    }
}
