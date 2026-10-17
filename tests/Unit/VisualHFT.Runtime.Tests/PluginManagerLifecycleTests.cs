using VisualHFT.Commons.Studies;
using VisualHFT.Enums;
using VisualHFT.Model;
using VisualHFT.PluginManager;
using HostPlugins = VisualHFT.PluginManager.PluginManager;
using VisualHFT.UserSettings;

namespace VisualHFT.Runtime.Tests;

public class PluginManagerLifecycleTests
{
    [Fact]
    public void LoadPluginsFrom_MissingDirectory_DoesNotThrow()
    {
        var missing = Path.Combine(Path.GetTempPath(), "visualhft-missing-" + Guid.NewGuid().ToString("N"));
        HostPlugins.LoadPluginsFrom(missing);
    }

    [Fact]
    public void LoadPluginsFrom_InvalidDll_DoesNotAbortTheScan()
    {
        var directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "visualhft-plugins-" + Guid.NewGuid().ToString("N")));
        try
        {
            File.WriteAllText(Path.Combine(directory.FullName, "not-an-assembly.dll"), "this is not a CLR image");
            File.WriteAllBytes(Path.Combine(directory.FullName, "empty.dll"), Array.Empty<byte>());
            var before = HostPlugins.AllPlugins.Count;
            HostPlugins.LoadPluginsFrom(directory.FullName);
            Assert.Equal(before, HostPlugins.AllPlugins.Count);
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Fact]
    public void Restart_DoesNotStackMetricHandlers()
    {
        var study = new FakeStudy();
        try
        {
            HostPlugins.StartPlugin(study);
            Assert.Equal(1, HostPlugins.MetricHandlerCount);

            HostPlugins.StopPlugin(study);
            Assert.Equal(0, HostPlugins.MetricHandlerCount);

            HostPlugins.StartPlugin(study);
            Assert.Equal(1, HostPlugins.MetricHandlerCount);
        }
        finally
        {
            if (study.Status != ePluginStatus.STOPPED)
                HostPlugins.StopPlugin(study);
        }
    }

    private sealed class FakeStudy : IPlugin, IStudy
    {
#pragma warning disable CS0067
        public event EventHandler<decimal>? OnAlertTriggered;
        public event EventHandler<BaseStudyModel>? OnCalculated;
#pragma warning restore CS0067
        public string Name { get; set; } = "Fake";
        public string Version { get; set; } = "1";
        public string Description { get; set; } = "";
        public string Author { get; set; } = "";
        public ePluginType PluginType => ePluginType.STUDY;
        public ISetting Settings { get; set; } = null!;
        public ePluginStatus Status { get; set; } = ePluginStatus.LOADED;
        public Action CloseSettingWindow { get; set; } = null!;
        public eLicenseLevel RequiredLicenseLevel => eLicenseLevel.COMMUNITY;
        public string TileTitle { get; set; } = "Fake";
        public string TileToolTip { get; set; } = "";
        public bool EmitsMetric => true;
        public bool IsChartButtonVisible { get; set; }
        public bool IsSettingsButtonVisisble { get; set; }
        public bool IsFooterVisible { get; set; }

        public string GetPluginUniqueID() => "fake-study";
        public object GetUISettings() => new();
        public object GetCustomUI() => new();
        public Task StartAsync()
        {
            Status = ePluginStatus.STARTED;
            return Task.CompletedTask;
        }
        public Task StopAsync()
        {
            Status = ePluginStatus.STOPPED;
            return Task.CompletedTask;
        }
        public void Dispose()
        {
        }
    }
}
