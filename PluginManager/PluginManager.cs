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
    public static class PluginManager
    {
        private static List<IPlugin> ALL_PLUGINS = new List<IPlugin>();
        private static object _locker = new object();
        private static readonly log4net.ILog log = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        public static async Task LoadPlugins()
        {
            // 1. By default load all dll's in current Folder. 
            var pluginsDirectory = AppDomain.CurrentDomain.BaseDirectory; // This gets the directory where your WPF app is running
            await Task.Run(() =>
            {
                lock (_locker)
                {
                    LoadPluginsByDirectory(pluginsDirectory);
                }
            });
        }
        public static List<IPlugin> AllPlugins { get { lock (_locker) return ALL_PLUGINS; } }
        public static bool AllPluginsReloaded { get; internal set; }

        public static async Task StartPlugins()
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
                        TriggerEngineService.RegisterMetric(plugin.GetPluginUniqueID(), plugin.Name,plugin.Settings.Provider.ProviderName,plugin.Settings.Symbol, e.Value.ToDouble(), e.Timestamp);

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

    }
}
