using System.Reflection;
using VisualHFT.Commons.Helpers;
using VisualHFT.Commons.Studies;
using VisualHFT.DataRetriever;
using VisualHFT.Model;
using VisualHFT.TriggerEngine;

namespace VisualHFT.PluginManager
{
    public class PluginsLoadedEventArgs : EventArgs
    {
        public List<IPlugin> LoadedPlugins { get; }
        public int SuccessfullyLoadedCount { get; }
        public int TotalAttemptedCount { get; }
        public DateTime LoadedAt { get; }

        public PluginsLoadedEventArgs(List<IPlugin> loadedPlugins)
        {
            LoadedPlugins = loadedPlugins;
            LoadedAt = DateTime.Now;
        }
    }

    /// <summary>
    /// Plugin discovery and lifecycle shared by the WPF host and the Avalonia host.
    /// Settings dialogs stay in the WPF project. This type does not reference a UI stack.
    /// </summary>
    public static class PluginManager
    {
        private static readonly List<IPlugin> ALL_PLUGINS = new();
        private static readonly object _locker = new();
        private static readonly object _loadGate = new();
        private static readonly Dictionary<IStudy, EventHandler<BaseStudyModel>> _handlers = new();
        private static readonly Dictionary<IPlugin, List<IStudy>> _attached = new();
        private static readonly log4net.ILog log = log4net.LogManager.GetLogger(typeof(PluginManager));

        public static event EventHandler<PluginsLoadedEventArgs>? OnLoaded;

        public static void LoadPlugins()
        {
            LoadPluginsFrom(AppContext.BaseDirectory);
        }

        /// <summary>
        /// Scans <paramref name="pluginsDirectory"/> for plugin assemblies.
        /// One bad DLL does not abort the scan. A second call does not construct a type that is already loaded.
        /// </summary>
        internal static void LoadPluginsFrom(string pluginsDirectory)
        {
            if (string.IsNullOrWhiteSpace(pluginsDirectory) || !Directory.Exists(pluginsDirectory))
                return;

            lock (_loadGate)
            {
                string[] files;
                try
                {
                    files = Directory.GetFiles(pluginsDirectory, "*.dll");
                }
                catch (Exception ex)
                {
                    log.Error("Plugin directory could not be read: " + pluginsDirectory, ex);
                    return;
                }

                foreach (var file in files)
                    LoadAssembly(file);
            }
        }

        internal static int MetricHandlerCount
        {
            get
            {
                lock (_locker)
                    return _handlers.Count;
            }
        }

        public static List<IPlugin> AllPlugins
        {
            get
            {
                lock (_locker)
                    return ALL_PLUGINS.ToList();
            }
        }

        /// <summary>
        /// Curated, flattened list of selectable study-metrics for any "pick a study"
        /// surface. Single studies map to one descriptor; multi-study children map to one
        /// each (grouped under the parent's title). Multi-study parents are never selectable
        /// on their own. Non-study plugins (data retrievers, etc.) are excluded.
        /// Read-only: building this list does not start or wire any plugin.
        /// </summary>
        public static IReadOnlyList<StudyDescriptor> GetSelectableStudies()
        {
            var result = new List<StudyDescriptor>();
            lock (_locker)
            {
                foreach (var plugin in ALL_PLUGINS)
                {
                    if (plugin is IMultiStudy multi)
                    {
                        var parentId = plugin.GetPluginUniqueID();
                        var settings = plugin.Settings;
                        var reason = settings is null ? "Plugin has no settings." : settings.GetConfigurationError();
                        var children = multi.Studies;
                        if (children == null)
                            continue;
                        foreach (var child in children)
                        {
                            if (child == null)
                                continue;
                            if (!child.EmitsMetric)
                                continue;
                            result.Add(new StudyDescriptor
                            {
                                Id = $"{parentId}|{child.TileTitle}",
                                DisplayName = child.TileTitle,
                                GroupName = multi.TileTitle,
                                TileToolTip = child.TileToolTip ?? string.Empty,
                                ProviderName = settings?.Provider?.ProviderName ?? string.Empty,
                                Symbol = settings?.Symbol ?? string.Empty,
                                IsConfigured = reason is null,
                                UnavailableReason = reason
                            });
                        }
                    }
                    else if (plugin is IStudy study)
                    {
                        if (!study.EmitsMetric)
                            continue;
                        var settings = plugin.Settings;
                        var reason = settings is null ? "Plugin has no settings." : settings.GetConfigurationError();
                        result.Add(new StudyDescriptor
                        {
                            Id = plugin.GetPluginUniqueID(),
                            DisplayName = study.TileTitle,
                            GroupName = null,
                            TileToolTip = study.TileToolTip ?? string.Empty,
                            ProviderName = settings?.Provider?.ProviderName ?? string.Empty,
                            Symbol = settings?.Symbol ?? string.Empty,
                            IsConfigured = reason is null,
                            UnavailableReason = reason
                        });
                    }
                }
            }
            return result;
        }

        public static async Task StartPluginsAsync()
        {
            List<IPlugin> snapshot;
            lock (_locker)
            {
                if (ALL_PLUGINS.Count == 0)
                    return;
                snapshot = ALL_PLUGINS.ToList();
            }

            var startTasks = snapshot.Select(plugin => Task.Run(() =>
            {
                try
                {
                    StartPlugin(plugin);
                }
                catch (Exception ex)
                {
                    plugin.Status = ePluginStatus.STOPPED_FAILED;
                    DetachPlugin(plugin);
                    HelperNotificationManager.Instance.AddNotification(plugin.Name, "Plugin failed to Start.", HelprNorificationManagerTypes.ERROR, HelprNorificationManagerCategories.PLUGINS, ex);
                }
            })).ToList();

            await Task.WhenAll(startTasks);
            OnPluginsLoaded(new PluginsLoadedEventArgs(AllPlugins));
        }

        public static void StartPlugin(IPlugin plugin)
        {
            if (!CanStart(plugin))
                return;

            try
            {
                if (plugin is IDataRetriever dataRetriever)
                    dataRetriever.StartAsync();
                else if (plugin is IStudy study)
                {
                    AttachStudy(plugin, study, plugin.GetPluginUniqueID(), plugin.Name);
                    study.StartAsync();
                }
                else if (plugin is IMultiStudy multi)
                {
                    multi.StartAsync();
                    AttachChildren(plugin, multi);
                }
            }
            catch
            {
                DetachPlugin(plugin);
                throw;
            }
        }

        public static async Task StartPluginAsync(IPlugin plugin)
        {
            if (!CanStart(plugin))
                return;

            try
            {
                if (plugin is IDataRetriever dataRetriever)
                    await dataRetriever.StartAsync();
                else if (plugin is IStudy study)
                {
                    AttachStudy(plugin, study, plugin.GetPluginUniqueID(), plugin.Name);
                    await study.StartAsync();
                }
                else if (plugin is IMultiStudy multi)
                {
                    await multi.StartAsync();
                    AttachChildren(plugin, multi);
                }
            }
            catch
            {
                DetachPlugin(plugin);
                throw;
            }
        }

        public static async Task RestartPluginAsync(IPlugin plugin)
        {
            await StopPluginAsync(plugin);
            await StartPluginAsync(plugin);
        }

        public static void StopPlugin(IPlugin plugin)
        {
            try
            {
                if (plugin == null)
                    return;
                if (plugin.Status == ePluginStatus.STOPPED || plugin.Status == ePluginStatus.STOPPED_FAILED || plugin.Status == ePluginStatus.STARTING)
                    return;

                DetachPlugin(plugin);
                if (plugin is IDataRetriever dataRetriever)
                    dataRetriever.StopAsync();
                else if (plugin is IStudy study)
                    study.StopAsync();
                else if (plugin is IMultiStudy mstudy)
                    mstudy.StopAsync();

                plugin.Status = ePluginStatus.STOPPED;
            }
            catch (Exception ex)
            {
                plugin.Status = ePluginStatus.STOPPED_FAILED;
                HelperNotificationManager.Instance.AddNotification(plugin.Name, "Plugin failed to Stop.", HelprNorificationManagerTypes.ERROR, HelprNorificationManagerCategories.PLUGINS, ex);
            }
        }

        public static async Task StopPluginAsync(IPlugin plugin)
        {
            if (plugin == null)
                return;

            var waits = 0;
            while (plugin.Status == ePluginStatus.STARTING && waits++ < 80)
                await Task.Delay(50);

            try
            {
                if (plugin.Status is ePluginStatus.STOPPED or ePluginStatus.STOPPED_FAILED)
                    return;

                DetachPlugin(plugin);
                if (plugin is IDataRetriever dataRetriever)
                    await dataRetriever.StopAsync();
                else if (plugin is IStudy study)
                    await study.StopAsync();
                else if (plugin is IMultiStudy multi)
                    await multi.StopAsync();

                plugin.Status = ePluginStatus.STOPPED;
            }
            catch (Exception ex)
            {
                plugin.Status = ePluginStatus.STOPPED_FAILED;
                HelperNotificationManager.Instance.AddNotification(plugin.Name, "Plugin failed to Stop.", HelprNorificationManagerTypes.ERROR, HelprNorificationManagerCategories.PLUGINS, ex);
            }
        }

        public static void UnloadPlugins()
        {
            List<IPlugin> snapshot;
            lock (_locker)
                snapshot = ALL_PLUGINS.ToList();

            foreach (var plugin in snapshot)
            {
                try
                {
                    DetachPlugin(plugin);
                }
                catch (Exception ex)
                {
                    log.Error("Failed to detach " + plugin.GetType().Name, ex);
                }

                if (plugin is not IDisposable disposable)
                    continue;
                try
                {
                    disposable.Dispose();
                }
                catch (Exception ex)
                {
                    log.Error("Failed to dispose " + plugin.GetType().Name, ex);
                }
            }
        }

        private static bool CanStart(IPlugin plugin)
        {
            if (plugin == null)
                return false;
            return plugin.Status != ePluginStatus.MALFUNCTIONING
                && plugin.Status != ePluginStatus.STARTING
                && plugin.Status != ePluginStatus.STARTED;
        }

        private static void LoadAssembly(string file)
        {
            Assembly assembly;
            try
            {
                assembly = Assembly.LoadFrom(file);
            }
            catch (Exception ex) when (ex is BadImageFormatException or FileLoadException)
            {
                return;
            }
            catch (Exception ex)
            {
                log.Warn("Skipped assembly " + Path.GetFileName(file), ex);
                return;
            }

            Type[] types;
            try
            {
                types = assembly.GetExportedTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types.Where(type => type != null).Cast<Type>().ToArray();
            }
            catch (Exception ex)
            {
                log.Warn("Skipped assembly " + Path.GetFileName(file), ex);
                return;
            }

            foreach (var type in types)
            {
                if (type == null || type.IsAbstract || !typeof(IPlugin).IsAssignableFrom(type))
                    continue;

                try
                {
                    if (Activator.CreateInstance(type) is not IPlugin plugin || string.IsNullOrEmpty(plugin.Name))
                        continue;
                    if (!LicenseManager.Instance.HasAccess(plugin.RequiredLicenseLevel))
                    {
                        (plugin as IDisposable)?.Dispose();
                        continue;
                    }

                    plugin.Status = ePluginStatus.LOADING;
                    var added = false;
                    lock (_locker)
                    {
                        if (ALL_PLUGINS.All(loaded => loaded.GetType() != type))
                        {
                            ALL_PLUGINS.Add(plugin);
                            added = true;
                        }
                    }
                    if (!added)
                    {
                        (plugin as IDisposable)?.Dispose();
                        continue;
                    }
                    log.Info("Plugin assemblies for: " + plugin.Name + " have loaded OK.");
                }
                catch (Exception ex)
                {
                    HelperNotificationManager.Instance.AddNotification("Loading Plugins", $"Plugin's assemblies located in {file} have failed to load.", HelprNorificationManagerTypes.ERROR, HelprNorificationManagerCategories.PLUGINS, ex);
                }
            }
        }

        private static void AttachChildren(IPlugin plugin, IMultiStudy multi)
        {
            var parentId = plugin.GetPluginUniqueID();
            var children = multi.Studies;
            if (children == null)
                return;
            foreach (var child in children)
            {
                if (child == null || !child.EmitsMetric)
                    continue;
                var title = child.TileTitle ?? string.Empty;
                AttachStudy(plugin, child, parentId + "|" + title, title);
            }
        }

        private static void AttachStudy(IPlugin plugin, IStudy study, string metricId, string metricName)
        {
            if (study == null)
                return;

            lock (_locker)
            {
                DetachStudyUnlocked(study);
                var id = metricId ?? string.Empty;
                var name = metricName ?? string.Empty;
                EventHandler<BaseStudyModel> handler = (_, model) => PublishMetric(plugin, id, name, model);
                _handlers[study] = handler;
                study.OnCalculated += handler;
                if (!_attached.TryGetValue(plugin, out var list))
                {
                    list = new List<IStudy>();
                    _attached[plugin] = list;
                }
                list.Add(study);
            }
        }

        private static void PublishMetric(IPlugin plugin, string metricId, string metricName, BaseStudyModel? model)
        {
            if (model == null)
                return;
            var settings = plugin.Settings;
            TriggerEngineService.RegisterMetric(
                metricId,
                metricName,
                settings?.Provider?.ProviderName ?? string.Empty,
                settings?.Symbol ?? string.Empty,
                model.Value.ToDouble(),
                model.Timestamp);
        }

        private static void DetachPlugin(IPlugin plugin)
        {
            lock (_locker)
            {
                if (!_attached.TryGetValue(plugin, out var list))
                    return;
                foreach (var study in list)
                    DetachStudyUnlocked(study);
                _attached.Remove(plugin);
            }
        }

        private static void DetachStudyUnlocked(IStudy study)
        {
            if (study == null)
                return;
            if (!_handlers.TryGetValue(study, out var handler))
                return;
            study.OnCalculated -= handler;
            _handlers.Remove(study);
        }

        private static void OnPluginsLoaded(PluginsLoadedEventArgs e)
        {
            OnLoaded?.Invoke(null, e);
        }
    }
}
