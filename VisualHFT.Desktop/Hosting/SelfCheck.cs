using VisualHFT.Commons.Helpers;
using VisualHFT.Desktop.Hosting;
using VisualHFT.PluginManager;
using HostPlugins = VisualHFT.PluginManager.PluginManager;
using VisualHFT.TriggerEngine;

namespace VisualHFT.Desktop;

internal static class SelfCheck
{
    public static int Run()
    {
        var baseDir = AppContext.BaseDirectory;
        Directory.SetCurrentDirectory(baseDir);
        var config = Path.Combine(baseDir, "log4net.config");
        if (File.Exists(config))
            log4net.Config.XmlConfigurator.Configure(new FileInfo(config));

        LicenseManager.Instance.LoadFromKeygen();
        HostPlugins.LoadPlugins();
        var plugins = HostPlugins.AllPlugins;
        foreach (var plugin in plugins.OrderBy(p => p.Name))
            Console.WriteLine($"{plugin.PluginType,-16} {plugin.Status,-16} {plugin.Name}");

        var studies = HostPlugins.GetSelectableStudies();
        Console.WriteLine($"plugins={plugins.Count} selectableStudies={studies.Count}");

        TriggerEngineService.LoadAllRules();
        Console.WriteLine($"rules={TriggerEngineService.GetRules().Count}");
        Console.WriteLine("runtime=" + System.Runtime.InteropServices.RuntimeInformation.OSDescription);
        Console.WriteLine("framework=" + System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription);

        if (plugins.Count == 0)
        {
            Console.Error.WriteLine("No plugins loaded from " + baseDir);
            return 1;
        }

        return 0;
    }
}
