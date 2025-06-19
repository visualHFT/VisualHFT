using System.Collections.ObjectModel;
using VisualHFT.Commons.Model;
using static log4net.Appender.FileAppender;

namespace VisualHFT.Commons.Helpers
{
    public enum HelprNorificationManagerCategories
    {
        NONE,
        CORE,
        PLUGINS,
        TRIGGER_ENGINE,
    }
    public enum HelprNorificationManagerTypes
    {
        ERROR,
        WARNING,
        TRIGGER_ACTION
    }

    public class HelperNotificationManager
    {
        private static readonly Lazy<HelperNotificationManager> _instance =
            new Lazy<HelperNotificationManager>(() => new HelperNotificationManager());

        private readonly ObservableCollection<ErrorNotification> _notifications;
        private int _nextId;
        private object _lock = new object();

        private HelperNotificationManager()
        {
            _notifications = new ObservableCollection<ErrorNotification>();
            _nextId = 1;
        }

        public static HelperNotificationManager Instance => _instance.Value;

        public event EventHandler<ErrorNotificationEventArgs> NotificationAdded;

        public void AddNotification(string title, string message, HelprNorificationManagerTypes notificationType,
            HelprNorificationManagerCategories category = HelprNorificationManagerCategories.NONE, Exception exception = null, string PluginID=null)
        {
            var notification = new ErrorNotification
            {
                Id = _nextId++,
                Title = title,
                Message = message,
                Exception = exception,
                Timestamp = DateTime.Now,
                IsRead = false,
                Category = category,
                NotificationType = notificationType,
                PluginID=PluginID
            };
            lock (_lock)
            {
                _notifications.Add(notification);
            }
            OnNotificationAdded(notification);
        }

        protected virtual void OnNotificationAdded(ErrorNotification notification)
        {
            NotificationAdded?.Invoke(this, new ErrorNotificationEventArgs(notification));
        }

        public ObservableCollection<ErrorNotification> GetAllNotifications()
        {
            lock (_lock)
                return _notifications;
        }
        

        public void MarkAsRead(int id)
        {
            lock (_lock)
            {
                var notification = _notifications.FirstOrDefault(n => n.Id == id);
                if (notification != null)
                {
                    notification.IsRead = true;
                }
            }
        }

        public void ClearNotifications()
        {
            lock (_lock)
                _notifications.Clear();
        }

        public void ClearReadNotifications()
        {
            lock (_lock)
            {
                for (int i = _notifications.Count - 1; i >= 0; i--)
                {
                    if (_notifications[i].IsRead)
                    {
                        _notifications.RemoveAt(i);
                    }
                }
            }
        }
    }

    public class ErrorNotificationEventArgs : EventArgs
    {
        public ErrorNotificationEventArgs(ErrorNotification notification)
        {
            Notification = notification;
        }

        public ErrorNotification Notification { get; }
    }
}