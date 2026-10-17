using Prism.Commands;
using Prism.Mvvm;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using VisualHFT.PluginManager;

namespace VisualHFT.ViewModel
{
    public class vmPluginManager : BindableBase, IDisposable
    {
        // We use a timer to check the plugins statuses. 
        // TODO: we could improve this having an event every time a plugin's status changes.
        // The timespan we've chosen to check this is 30,000 milliseconds (10 sec)
        private int _MILLISECONDS_CHECK = 10000;
        private readonly System.Timers.Timer _timer_check;

        private ObservableCollection<IPlugin> _plugins;

        public ObservableCollection<IPlugin> Plugins
        {
            get { return _plugins; }
            set { SetProperty(ref _plugins, value); }
        }

        public ICommand StartPluginCommand { get; private set; }
        public ICommand StopPluginCommand { get; private set; }
        public ICommand ConfigurePluginCommand { get; private set; }

        public vmPluginManager()
        {
            _plugins = new ObservableCollection<IPlugin>(PluginManager.PluginManager.AllPlugins);
            RaisePropertyChanged(nameof(Plugins));

            StartPluginCommand = new DelegateCommand<IPlugin>(StartPlugin, CanStartPlugin);
            StopPluginCommand = new DelegateCommand<IPlugin>(StopPlugin, CanStopPlugin);
            ConfigurePluginCommand = new DelegateCommand<IPlugin>(ConfigurePlugin);

            _timer_check = new System.Timers.Timer(_MILLISECONDS_CHECK);
            _timer_check.Elapsed += _timer_check_Elapsed;
            _timer_check.Start();

        }

        private void _timer_check_Elapsed(object sender, System.Timers.ElapsedEventArgs e)
        {
            RefreshButtonsStatus();
            Application.Current.Dispatcher.BeginInvoke(RefreshPluginsStatus);
        }
        private bool CanStartPlugin(IPlugin plugin)
        {
            return plugin.Status == ePluginStatus.LOADED
                   || plugin.Status == ePluginStatus.STOPPED
                   || plugin.Status == ePluginStatus.STOPPED_FAILED;


        }
        private bool CanStopPlugin(IPlugin plugin)
        {
            return plugin.Status == ePluginStatus.STARTING
                   || plugin.Status == ePluginStatus.MALFUNCTIONING
                   || plugin.Status == ePluginStatus.STARTED;
        }
        private void StartPlugin(IPlugin plugin)
        {
            PluginManager.PluginManager.StartPlugin(plugin);
            RefreshPluginsStatus();
            RefreshButtonsStatus();
        }

        private void StopPlugin(IPlugin plugin)
        {
            PluginManager.PluginManager.StopPlugin(plugin);
            RefreshPluginsStatus();
            RefreshButtonsStatus();
        }

        private void ConfigurePlugin(IPlugin plugin)
        {
            PluginManager.PluginSettingsHost.Show(plugin);
            RefreshButtonsStatus();
        }

        private void RefreshButtonsStatus()
        {
            // Refresh the CanExecute status
            (StartPluginCommand as DelegateCommand<IPlugin>).RaiseCanExecuteChanged();
            (StopPluginCommand as DelegateCommand<IPlugin>).RaiseCanExecuteChanged();
        }
        private void RefreshPluginsStatus()
        {
            _plugins = new ObservableCollection<IPlugin>(PluginManager.PluginManager.AllPlugins);
            RaisePropertyChanged(nameof(Plugins));
        }

        public void Dispose()
        {
            // Any cleanup logic if needed
            _timer_check.Dispose();
        }
    }

}
