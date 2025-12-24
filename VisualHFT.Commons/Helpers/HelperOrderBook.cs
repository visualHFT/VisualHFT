using System.Collections.Immutable;
using VisualHFT.Model;

namespace VisualHFT.Helpers
{


    public sealed class HelperOrderBook : IOrderBookHelper
    {

        private ImmutableArray<Action<OrderBook>> _subscribers = ImmutableArray<Action<OrderBook>>.Empty;

        private static readonly log4net.ILog log = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        private static readonly HelperOrderBook instance = new HelperOrderBook();

        public event Action<VisualHFT.Commons.Model.ErrorEventArgs> OnException;

        public static HelperOrderBook Instance => instance;


        private HelperOrderBook()
        {

        }
        ~HelperOrderBook()
        { }



        /// <summary>
        /// Subscribes the.Limit Order Book realtime stream.
        /// Note:
        ///     - must be very careful not to block this call, and make sure to TRANSFORM the object into its minimal need to update the UI.
        ///     - the UI update must be handled in another thread, without using the object coming from this subscription (must be decoupled).
        ///     
        /// </summary>
        /// <param name="subscriber">The subscriber.</param>
        public void Subscribe(Action<OrderBook> subscriber)
        {
            // No lock needed - ImmutableInterlocked is thread-safe
            ImmutableInterlocked.Update(ref _subscribers, subs => subs.Add(subscriber));
        }
        public void Unsubscribe(Action<OrderBook> subscriber)
        {
            ImmutableInterlocked.Update(ref _subscribers, subs => subs.Remove(subscriber));
        }
        public void Reset()
        {
            ImmutableInterlocked.InterlockedExchange(ref _subscribers, ImmutableArray<Action<OrderBook>>.Empty);
        }

        private void DispatchToSubscribers(OrderBook book)
        {
            var subscribers = _subscribers;
            foreach (var subscriber in subscribers)
            {
                try
                {
                    subscriber(book);
                }
                catch (Exception ex)
                {
                    Task.Run(() =>
                    {
                        log.Error(ex);
                        OnException?.Invoke(new VisualHFT.Commons.Model.ErrorEventArgs(ex, subscriber.Target));
                    });
                    throw;
                }
            }
        }
        public void UpdateData(OrderBook data)
        {
            DispatchToSubscribers(data);
        }
        public void UpdateData(IEnumerable<OrderBook> data)
        {
            foreach (var e in data)
            {
                DispatchToSubscribers(e);
            }
        }
    }
}
