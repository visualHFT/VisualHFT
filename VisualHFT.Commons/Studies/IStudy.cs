using VisualHFT.Model;

namespace VisualHFT.Commons.Studies
{
    public interface IStudy : IDisposable
    {
        public event EventHandler<decimal> OnAlertTriggered;
        public event EventHandler<BaseStudyModel> OnCalculated;

        Task StartAsync();
        Task StopAsync();
        string TileTitle { get; set; }
        string TileToolTip { get; set; }
        object GetCustomUI();   //Allow to setup own UI for the plugin
        //using object type because this csproj doesn't support UI
        bool IsChartButtonVisible { get; set; }
        bool IsSettingsButtonVisisble { get; set; }
        bool IsFooterVisible { get; set; }
    }

}