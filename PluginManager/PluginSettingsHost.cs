using System.Windows.Controls;
using VisualHFT.Commons.Helpers;

namespace VisualHFT.PluginManager
{
    /// <summary>
    /// WPF settings dialog. The shared <see cref="PluginManager"/> does not reference WPF,
    /// so only the Windows host opens this window.
    /// </summary>
    public static class PluginSettingsHost
    {
        public static void Show(IPlugin plugin)
        {
            try
            {
                if (plugin == null)
                    return;

                var formSettings = new View.PluginSettings();
                plugin.CloseSettingWindow = () =>
                {
                    formSettings.Close();
                };

                if (plugin.GetUISettings() is not UserControl settings)
                {
                    plugin.CloseSettingWindow = null;
                    return;
                }
                formSettings.MainGrid.Children.Add(settings);
                formSettings.Title = $"{plugin.Name} Settings";
                formSettings.WindowStartupLocation = System.Windows.WindowStartupLocation.CenterOwner;
                formSettings.Topmost = true;
                formSettings.ShowInTaskbar = false;
                formSettings.ShowDialog();
            }
            catch (Exception ex)
            {
                if (plugin == null)
                    return;
                plugin.Status = ePluginStatus.STOPPED_FAILED;
                HelperNotificationManager.Instance.AddNotification(plugin.Name, "Plugin failed to load settings.", HelprNorificationManagerTypes.ERROR, HelprNorificationManagerCategories.PLUGINS, ex);
            }
        }
    }
}
