using System.Security.Cryptography;
using System.Text;
using VisualHFT.Commons.Studies;
using VisualHFT.Enums;
using VisualHFT.PluginManager;
using VisualHFT.UserSettings;

namespace VisualHFT.Commons.PluginManager
{
    public abstract class BasePluginMultiStudy : IMultiStudy, VisualHFT.PluginManager.IPlugin, IDisposable
    {
        protected static readonly log4net.ILog log = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
        protected bool _disposed = false; // to track whether the object has been disposed


        public abstract string Name { get; set; }
        public abstract string Version { get; set; }
        public abstract string Description { get; set; }
        public abstract string Author { get; set; }
        public abstract ISetting Settings { get; set; }
        public abstract ePluginStatus Status { get; set; }
        public abstract Action CloseSettingWindow { get; set; }
        public abstract string TileTitle { get; set; }
        public abstract string TileToolTip { get; set; }
        public ePluginType PluginType
        {
            get { return ePluginType.MULTI_STUDY; }
        }
        public abstract List<IStudy> Studies { get; set; }
        protected abstract void LoadSettings();
        protected abstract void SaveSettings();
        protected abstract void InitializeDefaultSettings();
        public virtual eLicenseLevel RequiredLicenseLevel => eLicenseLevel.COMMUNITY; // Default to Community, override in derived classes if needed


        public BasePluginMultiStudy()
        {
            // Ensure settings are loaded before starting
            LoadSettings();
            if (Settings == null)
                throw new InvalidOperationException($"{Name} plugin settings has not been loaded.");

            Status = ePluginStatus.LOADED;
        }

        public virtual async Task StartAsync()
        {
            Status = ePluginStatus.STARTING;
            log.Info($"{this.Name} is starting.");
        }

        public virtual async Task StopAsync()
        {
            Status = ePluginStatus.STOPPED;
            log.Info($"{this.Name} has stopped.");
        }


        protected void SaveToUserSettings(ISetting settings)
        {
            UserSettings.SettingsManager.Instance.SetSetting(SettingKey.TILE_STUDY, GetPluginUniqueID(), settings);
        }
        protected T LoadFromUserSettings<T>() where T : class
        {
            // The stored value is the JObject read from disk until the first save in this process;
            // after that it is the saved settings object itself. Read both, and always return a new
            // copy, so a second instance never shares (and changes) the first instance's settings.
            var stored = UserSettings.SettingsManager.Instance.GetSetting<object>(SettingKey.TILE_STUDY, GetPluginUniqueID());
            if (stored == null)
            {
                return null;
            }
            var token = stored as Newtonsoft.Json.Linq.JToken ?? Newtonsoft.Json.Linq.JToken.FromObject(stored);
            return (token as Newtonsoft.Json.Linq.JObject)?.ToObject<T>();
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                _disposed = true;
                if (disposing)
                {
                    // First stop all studies
                    if (Studies != null)
                    {
                        foreach (var study in Studies)
                        {
                            try
                            {
                                // Stop the study first, then dispose
                                Task.Run(() => study.StopAsync()).GetAwaiter().GetResult();
                                study.Dispose();
                            }
                            catch (Exception ex)
                            {
                                log.Warn($"Error disposing study in {Name}: {ex.Message}");
                                // Continue disposing other studies
                            }
                        }
                        Studies.Clear();
                        Studies = null;
                    }
                }
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        public string GetPluginUniqueID()
        {
            // Get the fully qualified name of the assembly
            string assemblyName = GetType().Assembly.FullName;

            // Concatenate the attributes
            string combinedAttributes = $"{Name}{Author}{Version}{Description}{assemblyName}";

            // Compute the SHA256 hash
            using (SHA256 sha256 = SHA256.Create())
            {
                byte[] bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(combinedAttributes));
                StringBuilder builder = new StringBuilder();
                for (int i = 0; i < bytes.Length; i++)
                {
                    builder.Append(bytes[i].ToString("x2"));
                }
                return builder.ToString();
            }
        }
        public abstract object GetUISettings(); //using object type because this csproj doesn't support UI
        public virtual object GetCustomUI()
        {
            return null;
        }

    }
}
