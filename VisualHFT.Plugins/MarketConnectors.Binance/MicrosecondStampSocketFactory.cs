using System;
using System.IO.Pipelines;
using CryptoExchange.Net.Objects.Sockets;
using CryptoExchange.Net.Sockets.Default;
using CryptoExchange.Net.Sockets.Default.Interfaces;
using CryptoExchange.Net.Sockets.HighPerf.Interfaces;
using Microsoft.Extensions.Logging;

namespace MarketConnectors.Binance
{
    /// <summary>
    /// Binance's spot market-data stream stamps events in milliseconds unless the connection URL
    /// carries <c>timeUnit=MICROSECOND</c>. The client library connects to "{stream address}/stream"
    /// and has no option for that parameter, so this factory adds it to the URI of every combined-stream
    /// connection before the wrapped factory opens it. The library's timestamp converter reads the
    /// longer (16-digit) value as microseconds, so trade times keep their sub-millisecond part.
    /// </summary>
    internal sealed class MicrosecondStampSocketFactory : IWebsocketFactory
    {
        private const string StreamPath = "/stream";
        private const string TimeUnitKey = "timeUnit=";
        private const string TimeUnitParameter = "timeUnit=MICROSECOND";

        private readonly IWebsocketFactory _inner;

        public MicrosecondStampSocketFactory()
            : this(new WebsocketFactory())
        {
        }

        public MicrosecondStampSocketFactory(IWebsocketFactory inner)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        }

        public IWebsocket CreateWebsocket(ILogger logger, SocketConnection connection, WebSocketParameters parameters)
        {
            // WebSocketParameters.Uri has a public setter, so the URI is rewritten in place.
            parameters.Uri = WithMicrosecondStamps(parameters.Uri);
            return _inner.CreateWebsocket(logger, connection, parameters);
        }

        public IHighPerfWebsocket CreateHighPerfWebsocket(ILogger logger, WebSocketParameters parameters, PipeWriter pipeWriter)
        {
            parameters.Uri = WithMicrosecondStamps(parameters.Uri);
            return _inner.CreateHighPerfWebsocket(logger, parameters, pipeWriter);
        }

        /// <summary>
        /// Adds <c>timeUnit=MICROSECOND</c> to a combined-stream URI ("/stream"). Any other URI, or one
        /// that already carries a time unit, is returned unchanged (reconnects ask the factory again).
        /// </summary>
        internal static Uri WithMicrosecondStamps(Uri uri)
        {
            if (!uri.AbsolutePath.EndsWith(StreamPath, StringComparison.Ordinal))
                return uri;

            string query = uri.Query;
            if (query.Contains(TimeUnitKey, StringComparison.OrdinalIgnoreCase))
                return uri;

            var builder = new UriBuilder(uri)
            {
                Query = query.Length > 1 ? query.Substring(1) + "&" + TimeUnitParameter : TimeUnitParameter
            };
            return builder.Uri;
        }
    }
}
