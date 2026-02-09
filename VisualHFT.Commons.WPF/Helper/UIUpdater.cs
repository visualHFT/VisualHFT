using System;
using System.Windows;

namespace VisualHFT.Helpers
{
    public class UIUpdater : IDisposable
    {
        private bool _disposed = false;
        private FrameParticipant _participant;
        private Action _updateAction;
        private bool _isActionRunning = false;
        private static readonly log4net.ILog log = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        public UIUpdater(Action updateAction, double debounceTimeInMilliseconds = 30)
        {
            // Simple UI thread check - throw exception if not on UI thread
            if (Application.Current == null || !Application.Current.Dispatcher.CheckAccess())
            {
                throw new InvalidOperationException("UIUpdater must be created on the UI thread");
            }

            debounceTimeInMilliseconds = Math.Max(debounceTimeInMilliseconds, 1);
            _updateAction = updateAction;

            // Register with the global FrameCoordinator instead of creating own DispatcherTimer
            _participant = FrameCoordinator.Instance.Register(OnTick, debounceTimeInMilliseconds);
        }

        ~UIUpdater()
        {
            Dispose(false);
        }

        private void OnTick()
        {
            if (_isActionRunning)
            {
                return;
            }

            _isActionRunning = true;
            try
            {
                _updateAction();
            }
            catch (Exception ex)
            {
                log.Error(ex);
            }
            finally
            {
                _isActionRunning = false;
            }
        }

        public void Stop()
        {
            if (_participant != null)
            {
                _participant.IsActive = false;
            }
        }

        public void Start()
        {
            if (_participant != null && !_isActionRunning)
            {
                _participant.IsActive = true;
            }
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    FrameCoordinator.Instance.Unregister(_participant);
                    _participant = null;
                }
                _disposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
    }
}
