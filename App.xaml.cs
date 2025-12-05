using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using VisualHFT.TriggerEngine;

namespace VisualHFT
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            //Initialize logging
            log4net.Config.XmlConfigurator.Configure(new System.IO.FileInfo("log4net.config"));

            /*----------------------------------------------------------------------------------------------------------------------*/
            RenderOptions.ProcessRenderMode = RenderMode.Default; 
            /*----------------------------------------------------------------------------------------------------------------------*/


            //Launch the GC cleanup thread ==> *** Since using Object Pools, we improved a lot the memory prints. So We commented this out.
            Task.Run(async () => { await GCCleanupAsync(); });

            //Load Plugins
            Task.Run(async () =>
            {
                try
                {
                    await LoadPlugins();
                }
                catch (Exception ex)
                {
                    // Handle the exception
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        MessageBox.Show("ERROR LOADING Plugins: " + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    });
                }
            });

            Task.Run(async () =>
            {
                TriggerEngineService.LoadAllRules();
                await TriggerEngineService.StartBackgroundWorkerAsync(CancellationToken.None);
            });
             
        }
        protected override void OnExit(ExitEventArgs e)
        {
            PluginManager.PluginManager.UnloadPlugins();

            base.OnExit(e);
        }

        private static async Task LoadPlugins()
        {
            //Load the license manager and check if the user has access to the plugin
            LicenseManager.Instance.LoadFromKeygen();
            PluginManager.PluginManager.LoadPlugins();
            await PluginManager.PluginManager.StartPluginsAsync();
        }
        private static async Task GCCleanupAsync()
        {
            //due to the high volume of data do this periodically.(this will get fired every 5 secs)

            while (true)
            {
                await Task.Delay(35000);
                GC.Collect(0, GCCollectionMode.Forced, false); //force garbage collection
            }

        }
    }
}
