using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Controls;
using VisualHFT.Commons.Helpers;
using VisualHFT.Commons.Studies;
using VisualHFT.DataRetriever;
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

    public static class PluginManager
    {
        private static List<IPlugin> ALL_PLUGINS = new List<IPlugin>();
        private readonly static object _locker = new object();
        private static readonly log4net.ILog log = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
        // Event declaration
        public static event EventHandler<PluginsLoadedEventArgs> OnLoaded;

        public static void LoadPlugins()
        {
            // 1. By default load all dll's in current Folder. 
            var pluginsDirectory = AppDomain.CurrentDomain.BaseDirectory; // This gets the directory where your WPF app is running
            lock (_locker)
            {
                LoadPluginsByDirectory(pluginsDirectory);
            }
        }
        public static List<IPlugin> AllPlugins { get { lock (_locker) return ALL_PLUGINS; } }

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
                    // Multi-study parents are IPlugin + IMultiStudy (not IStudy): emit children only.
                    if (plugin is IMultiStudy multi)
                    {
                        var parentId = plugin.GetPluginUniqueID();
                        var settings = plugin.Settings;
                        // Children share the parent's settings, so the config state (and its
                        // reason) is computed once and applied to every child.
                        var reason = settings is null ? "Plugin has no settings." : settings.GetConfigurationError();
                        var children = multi.Studies;
                        if (children == null)
                            continue;
                        foreach (var child in children)
                        {
                            if (child == null)
                                continue;
                            // Only metric-emitting children are matchable by a trigger rule.
                            if (!child.EmitsMetric)
                                continue;
                            result.Add(new StudyDescriptor
                            {
                                // Key carries the metric identity (TileTitle), so children stay
                                // distinct even if their plugin Name/Description collide.
                                Id = $"{parentId}|{child.TileTitle}",
                                DisplayName = child.TileTitle,
                                GroupName = multi.TileTitle,
                                // The CHILD's own tooltip (never the parent's) so pickers can
                                // surface what this specific metric is.
                                TileToolTip = child.TileToolTip ?? string.Empty,
                                ProviderName = settings?.Provider?.ProviderName ?? string.Empty,
                                Symbol = settings?.Symbol ?? string.Empty,
                                // Config-gate only — deliberately NOT plugin.Status: StartAsync sets
                                // STARTED unconditionally when lifecycle wiring finishes, configured or
                                // not (config matching is self-guarded per-event inside the handlers),
                                // so STARTED would rescue never-configured auto-started system plugins.
                                // A study reconfigured live IS reflected here without any liveness
                                // rescue, because GetConfigurationError keys on the same provider
                                // identity the emit path matches (StudyConfigPolicy).
                                IsConfigured = reason is null,
                                UnavailableReason = reason
                            });
                        }
                    }
                    else if (plugin is IStudy study)
                    {
                        // Only metric-emitting studies are matchable by a trigger rule.
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
                            // Config-gate only — NOT plugin.Status (see multi-study branch above).
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
            List<Task> startTasks = new List<Task>();
            lock (_locker)
            {
                if (ALL_PLUGINS.Count == 0) { return; }
                foreach (var plugin in ALL_PLUGINS)
                {
                    // Add a task for starting each plugin and handle exceptions within the task
                    startTasks.Add(Task.Run(() =>
                    {
                        try
                        {
                            StartPlugin(plugin);
                        }
                        catch (Exception ex)
                        {
                            plugin.Status = ePluginStatus.STOPPED_FAILED;
                            //SYSTEM ERROR LOGS
                            //---------------------
                            string msg = $"Plugin failed to Start.";
                            HelperNotificationManager.Instance.AddNotification(plugin.Name, msg, HelprNorificationManagerTypes.ERROR, HelprNorificationManagerCategories.PLUGINS, ex);
                        }
                    }));
                }
            }

            // Await all the tasks
            await Task.WhenAll(startTasks);
            // Trigger the OnLoaded event
            OnPluginsLoaded(new PluginsLoadedEventArgs(ALL_PLUGINS.ToList()));
        }
        public static void StartPlugin(IPlugin plugin)
        {
            if (plugin != null)
            {
                if (plugin.Status == ePluginStatus.MALFUNCTIONING || plugin.Status == ePluginStatus.STARTING || plugin.Status == ePluginStatus.STARTED)
                    return;

                if (plugin is IDataRetriever dataRetriever)
                    dataRetriever.StartAsync();
                else if (plugin is IStudy study)
                {
                    study.StartAsync();
                    study.OnCalculated += (sender, e) =>
                    {
                        TriggerEngineService.RegisterMetric(plugin.GetPluginUniqueID(), plugin.Name, plugin.Settings.Provider.ProviderName, plugin.Settings.Symbol, e.Value.ToDouble(), e.Timestamp);

                    };
                }
                else if (plugin is IMultiStudy mstudy)
                    mstudy.StartAsync();
            }
        }
        public static void StopPlugin(IPlugin plugin)
        {
            try
            {
                if (plugin != null)
                {
                    if (plugin.Status == ePluginStatus.STOPPED || plugin.Status == ePluginStatus.STOPPED_FAILED || plugin.Status == ePluginStatus.STARTING) { return; }

                    if (plugin is IDataRetriever dataRetriever)
                        dataRetriever.StopAsync();
                    else if (plugin is IStudy study)
                        study.StopAsync();
                    else if (plugin is IMultiStudy mstudy)
                        mstudy.StopAsync();

                    plugin.Status = ePluginStatus.STOPPED;
                }
            }
            catch (Exception ex)
            {
                plugin.Status = ePluginStatus.STOPPED_FAILED;
                //SYSTEM ERROR LOGS
                //---------------------
                string msg = $"Plugin failed to Stop.";
                HelperNotificationManager.Instance.AddNotification(plugin.Name, msg, HelprNorificationManagerTypes.ERROR, HelprNorificationManagerCategories.PLUGINS, ex);

            }
        }
        public static void SettingPlugin(IPlugin plugin)
        {
            UserControl _ucSettings = null;
            try
            {
                if (plugin != null)
                {
                    var formSettings = new View.PluginSettings();
                    plugin.CloseSettingWindow = () => {
                        formSettings.Close();
                    };

                    _ucSettings = plugin.GetUISettings() as UserControl;
                    if (_ucSettings == null)
                    {
                        plugin.CloseSettingWindow = null;
                        formSettings = null;
                        return;
                    }
                    formSettings.MainGrid.Children.Add(_ucSettings);
                    formSettings.Title = $"{plugin.Name} Settings";
                    formSettings.WindowStartupLocation = System.Windows.WindowStartupLocation.CenterOwner;
                    formSettings.Topmost = true;
                    formSettings.ShowInTaskbar = false;
                    formSettings.ShowDialog();
                }

            }
            catch (Exception ex)
            {
                plugin.Status = ePluginStatus.STOPPED_FAILED;
                //SYSTEM ERROR LOGS
                //---------------------
                string msg = $"Plugin failed to load settings.";
                HelperNotificationManager.Instance.AddNotification(plugin.Name, msg, HelprNorificationManagerTypes.ERROR, HelprNorificationManagerCategories.PLUGINS, ex);
            }
        }
        public static void UnloadPlugins()
        {
            lock (_locker)
            {
                if (ALL_PLUGINS.Count == 0) { return; }
                foreach (var plugin in ALL_PLUGINS.OfType<IDisposable>())
                {
                    plugin.Dispose();
                }
            }
        }
        private static void LoadPluginsByDirectory(string pluginsDirectory)
        {
            foreach (var file in Directory.GetFiles(pluginsDirectory, "*.dll"))
            {
                var assembly = Assembly.LoadFrom(file);
                foreach (var type in assembly.GetExportedTypes())
                {
                    if (!type.IsAbstract && type.GetInterfaces().Contains(typeof(IPlugin)))
                    {
                        try
                        {
                            var plugin = Activator.CreateInstance(type) as IPlugin;
                            if (string.IsNullOrEmpty(plugin.Name))
                                continue;
                            if (!LicenseManager.Instance.HasAccess(plugin.RequiredLicenseLevel))
                                continue;

                            plugin.Status = ePluginStatus.LOADING;

                            ALL_PLUGINS.Add(plugin);
                            log.Info("Plugin assemblies for: " + plugin.Name + " have loaded OK.");
                        }
                        catch (Exception ex)
                        {
                            //SYSTEM ERROR LOGS
                            //---------------------
                            string msg = $"Plugin's assemblies located in {file} have failed to load.";
                            HelperNotificationManager.Instance.AddNotification("Loading Plugins", msg, HelprNorificationManagerTypes.ERROR, HelprNorificationManagerCategories.PLUGINS, ex);
                        }
                    }
                }
            }

        }
        private static void OnPluginsLoaded(PluginsLoadedEventArgs e)
        {
            OnLoaded?.Invoke(null, e);
        }
    }
}
