using VisualHFT.Commons.Helpers;

namespace VisualHFT.Commons.Model
{
    public class ErrorNotification
    {
        public int Id { get; set; }
        public string Title { get; set; }
        public string Message { get; set; }
        public DateTime Timestamp { get; set; }
        public bool IsRead { get; set; }
        public HelprNorificationManagerCategories Category { get; set; } // Optional: for grouping
        public HelprNorificationManagerTypes NotificationType { get; set; }
        public Exception Exception { get; set; }
        public string PluginID { get; set; }
    }
}