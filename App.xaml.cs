using System;
using System.Runtime;
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
            /*  This is to avoid errors when rendering too much in short times
             *
             *  Exception thrown: 'System.Runtime.InteropServices.COMException' in PresentationCore.dll
             *  An unhandled exception of type 'System.Runtime.InteropServices.COMException' occurred in PresentationCore.dll
             *  UCEERR_RENDERTHREADFAILURE (0x88980406)
             *  
             */
            //RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly; 
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
                    Application.Current.Dispatcher.BeginInvoke(() =>
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

        private async Task LoadPlugins()
        {
            //Load the license manager and check if the user has access to the plugin
            LicenseManager.Instance.LoadFromKeygen();


            PluginManager.PluginManager.AllPluginsReloaded = false;
            await PluginManager.PluginManager.LoadPlugins();
            await PluginManager.PluginManager.StartPlugins();
            PluginManager.PluginManager.AllPluginsReloaded = true;
        }
        private async Task GCCleanupAsync()
        {
            //due to the high volume of data do this periodically.(this will get fired every 5 secs)

            while (true)
            {
                await Task.Delay(5000);
                GC.Collect(0, GCCollectionMode.Forced, false); //force garbage collection
            };

        }
    }
}
