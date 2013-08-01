using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace NowinWebServer
{
    using OwinApp = Func<IDictionary<string, object>, Task>;
    using WebSocketAccept = Action<IDictionary<string, object>, Func<IDictionary<string, object>, Task>>;
    using WebSocketSendAsync =
         Func
         <
             ArraySegment<byte> /* data */,
             int /* messageType */,
             bool /* endOfMessage */,
             CancellationToken /* cancel */,
             Task
         >;
    using WebSocketReceiveAsync =
        Func
        <
            ArraySegment<byte> /* data */,
            CancellationToken /* cancel */,
            Task
            <
                Tuple
                <
                    int /* messageType */,
                    bool /* endOfMessage */,
                    int /* count */
                >
            >
        >;
    using WebSocketReceiveTuple =
        Tuple
        <
            int /* messageType */,
            bool /* endOfMessage */,
            int /* count */
        >;
    using WebSocketCloseAsync =
        Func
        <
            int /* closeStatus */,
            string /* closeDescription */,
            CancellationToken /* cancel */,
            Task
        >;

    public class OwinHandler : IHttpLayerHandler
    {
        readonly OwinApp _app;

        readonly OwinEnvironment _environment;
        internal readonly Dictionary<string, string[]> ReqHeaders;
        internal readonly Dictionary<string, string[]> RespHeaders;
        IDictionary<string, string[]> _overwrittenResponseHeaders;
        bool _inWebSocket;
        OwinApp _webSocketFunc;

        public IHttpLayerCallback Callback { set; internal get; }

        public WebSocketAccept WebSocketAcceptFunc
        {
            get { return WebSocketAcceptMethod; }
        }

        public OwinHandler(OwinApp app)
        {
            _app = app;
            _environment = new OwinEnvironment(this);
            ReqHeaders = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            RespHeaders = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        }

        public void Dispose()
        {
        }

        public void PrepareForRequest()
        {
            _inWebSocket = false;
            _webSocketFunc = null;
            _environment.Reset();
            ReqHeaders.Clear();
            RespHeaders.Clear();
        }

        public void AddRequestHeader(string name, string value)
        {
            string[] values;
            if (ReqHeaders.TryGetValue(name, out values))
            {
                Array.Resize(ref values, values.Length + 1);
                values[values.Length - 1] = value;
                ReqHeaders[name] = values;
            }
            else
            {
                ReqHeaders.Add(name, new[] { value });
            }
        }

        public void HandleRequest()
        {
            Callback.ResponseStatusCode = 200; // Default status code
            _overwrittenResponseHeaders = RespHeaders;
            if (!Callback.IsWebSocketReq)
                _environment.RemoveWebSocketAcceptFunc();
            var task = _app(_environment);
            if (task.IsCompleted)
            {
                if (_inWebSocket) return;
                if (task.IsFaulted || task.IsCanceled)
                {
                    Callback.ResponseStatusCode = 500;
                    Callback.ResponseReasonPhase = null;
                }
                Callback.ResponseFinished();
                return;
            }
            task.ContinueWith((t, o) =>
                {
                    if (((OwinHandler)o)._inWebSocket) return;
                    var callback = ((OwinHandler) o).Callback;
                    if (t.IsFaulted || t.IsCanceled)
                    {
                        callback.ResponseStatusCode = 500;
                        callback.ResponseReasonPhase = null;
                    }
                    callback.ResponseFinished();
                }, this);
        }

        public void PrepareResponseHeaders()
        {
            string[] connectionValues;
            var headers = _overwrittenResponseHeaders;
            if (headers == null)
            {
                return;
            }
            if (headers.TryGetValue("Connection", out connectionValues))
            {
                headers.Remove("Connection");
                if (connectionValues.Length != 1)
                {
                    Callback.ResponseStatusCode = 500;
                    return;
                }
                var v = connectionValues[0];
                if (v.Equals("Close", StringComparison.InvariantCultureIgnoreCase))
                    Callback.KeepAlive = false;
                else if (v.Equals("Keep-alive", StringComparison.InvariantCultureIgnoreCase))
                    Callback.KeepAlive = true;
            }
            string[] contentLengthValues;
            if (headers.TryGetValue("Content-Length", out contentLengthValues))
            {
                headers.Remove("Content-Length");
                if (contentLengthValues.Length != 1)
                {
                    Callback.ResponseStatusCode = 500;
                    return;
                }
                ulong temp;
                if (!ulong.TryParse(contentLengthValues[0], out temp))
                {
                    Callback.ResponseStatusCode = 500;
                    return;
                }
                Callback.ResponseContentLength = temp;
            }
            headers.Remove("Transfer-Encoding");
            foreach (var header in headers)
            {
                if (header.Value.Length == 1)
                {
                    Callback.AddResponseHeader(header.Key, header.Value[0]);
                }
                else
                {
                    Callback.AddResponseHeader(header.Key, header.Value);
                }
            }
        }

        public void OverwriteRespHeaders(IDictionary<string, string[]> value)
        {
            _overwrittenResponseHeaders = value;
        }

        void WebSocketAcceptMethod(IDictionary<string, object> dictionary, Func<IDictionary<string, object>, Task> func)
        {
            // TODO handle dictionary parameter
            _webSocketFunc = func;
            _inWebSocket = true;
            Callback.UpgradeToWebSocket();
        }

        public void UpgradedToWebSocket(bool success)
        {
            if (!success)
            {
                Callback.ResponseStatusCode = 500;
                Callback.ResponseFinished();
                return;
            }
            var webSocketEnv = new Dictionary<string, object>();
            webSocketEnv.Add("websocket.SendAsync", (WebSocketSendAsync)WebSocketSendAsyncMethod);
            webSocketEnv.Add("websocket.ReceiveAsync", (WebSocketReceiveAsync)WebSocketReceiveAsyncMethod);
            webSocketEnv.Add("websocket.CloseAsync", (WebSocketCloseAsync)WebSocketCloseAsyncMethod);
            webSocketEnv.Add("websocket.Version", "1.0");
            webSocketEnv.Add("websocket.CallCancelled", Callback.CallCancelled);
            try
            {
                var task = _webSocketFunc(webSocketEnv);
                task.ContinueWith((t, o) => ((OwinHandler)o).Callback.ResponseFinished(), this);
            }
            catch
            {
                Callback.ResponseFinished();
            }
        }

        Task WebSocketSendAsyncMethod(ArraySegment<byte> data, int messageType, bool endOfMessage, CancellationToken cancel)
        {
            throw new NotImplementedException();
        }

        Task<WebSocketReceiveTuple> WebSocketReceiveAsyncMethod(ArraySegment<byte> data, CancellationToken cancel)
        {
            throw new NotImplementedException();
        }

        Task WebSocketCloseAsyncMethod(int closeStatus, string closeDescription, CancellationToken cancel)
        {
            throw new NotImplementedException();
        }

    }
}