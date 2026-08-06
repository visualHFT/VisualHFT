namespace VisualHFT.UserSettings
{
    public enum SettingKey
    {
        APPLICATION_THEME_MODE,
        APPLICATION_INITIALIZATION_SIZE_WIDTH,
        APPLICATION_INITIALIZATION_SIZE_HEIGHT,
        TILE_STUDY,
        PLUGIN,
        KALSHI_LADDER_DEPTH_CHART,
        // Add more settings here
    }

}
namespace VisualHFT.PluginManager
{
    public enum ePluginStatus
    {
        LOADING,
        LOADED,
        STARTING,
        STARTED,
        STOPPED,
        STOPPED_FAILED,
        MALFUNCTIONING, //running with failures
        STOPPING
    }
}