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
        ArraySegment<byte> _webSocketReceiveSegment;
        TaskCompletionSource<WebSocketReceiveTuple> _webSocketReceiveTcs;
        WebSocketReceiveState _webSocketReceiveState;
        ulong _webSocketFrameLen;
        byte _webSocketMask0;
        byte _webSocketMask1;
        byte _webSocketMask2;
        byte _webSocketMask3;
        bool _webSocketFrameLast;
        byte _webSocketFrameOpcode;
        int _webSocketReceiveCount;
        int _maskIndex;

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
                    var callback = ((OwinHandler)o).Callback;
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
            _webSocketReceiveState = WebSocketReceiveState.Header;
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
            var webSocketEnv = new Dictionary<string, object>
                {
                    {"websocket.SendAsync", (WebSocketSendAsync) WebSocketSendAsyncMethod},
                    {"websocket.ReceiveAsync", (WebSocketReceiveAsync) WebSocketReceiveAsyncMethod},
                    {"websocket.CloseAsync", (WebSocketCloseAsync) WebSocketCloseAsyncMethod},
                    {"websocket.Version", "1.0"},
                    {"websocket.CallCancelled", Callback.CallCancelled}
                };
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

        public void FinishReceiveData(bool success)
        {
            if (!success)
            {
                _webSocketReceiveTcs.SetCanceled();
                return;
            }
            ParseWebSocketReceivedData();
        }

        Task WebSocketSendAsyncMethod(ArraySegment<byte> data, int messageType, bool endOfMessage, CancellationToken cancel)
        {
            var buf = Callback.Buffer;
            var o = Callback.SendDataOffset;
            buf[o] = (byte) ((endOfMessage ? 0x80 : 0) + messageType);
            buf[o + 1] = (byte) data.Count;
            Array.Copy(data.Array,data.Offset,buf,o+2,data.Count);
            return Callback.SendData(buf, o, 2 + data.Count);
        }

        Task<WebSocketReceiveTuple> WebSocketReceiveAsyncMethod(ArraySegment<byte> data, CancellationToken cancel)
        {
            _webSocketReceiveSegment = data;
            var tcs = new TaskCompletionSource<WebSocketReceiveTuple>();
            _webSocketReceiveTcs = tcs;
            _webSocketReceiveCount = 0;
            ParseWebSocketReceivedData();
            return tcs.Task;
        }

        Task WebSocketCloseAsyncMethod(int closeStatus, string closeDescription, CancellationToken cancel)
        {
            // TODO Close crap
            return Task.Delay(0);
        }

        void ParseWebSocketReceivedData()
        {
            if (Callback.ReceiveDataLength == 0)
            {
                Callback.StartReceiveData();
                return;
            }
            if (_webSocketReceiveTcs == null) return;
            if (_webSocketReceiveState == WebSocketReceiveState.Header)
            {
                var len = ParseHeader(Callback.Buffer, Callback.ReceiveDataOffset, Callback.ReceiveDataLength);
                if (len > 0)
                {
                    Callback.ConsumeReceiveData(len);
                    _webSocketReceiveState = WebSocketReceiveState.Body;
                    _maskIndex = 0;
                }
                else if (len < 0)
                {
                    _webSocketReceiveState = WebSocketReceiveState.Error;
                }
            }
            if (_webSocketReceiveState == WebSocketReceiveState.Body)
            {
                var len = (int)Math.Min(_webSocketFrameLen, (ulong)_webSocketReceiveSegment.Count);
                if (Callback.ReceiveDataLength < len) len = Callback.ReceiveDataLength;
                Unmask(Callback.Buffer, Callback.ReceiveDataOffset, _webSocketReceiveSegment.Array,
                       _webSocketReceiveSegment.Offset, len);
                Callback.ConsumeReceiveData(len);
                _webSocketFrameLen -= (ulong)len;
                _webSocketReceiveCount += len;
                _webSocketReceiveSegment = new ArraySegment<byte>(_webSocketReceiveSegment.Array, _webSocketReceiveSegment.Offset + len, _webSocketReceiveSegment.Count - len);
                if (_webSocketFrameLen==0)
                {
                    var tcs = _webSocketReceiveTcs;
                    _webSocketReceiveTcs = null;
                    tcs.SetResult(new WebSocketReceiveTuple(_webSocketFrameOpcode, _webSocketFrameLast, _webSocketReceiveCount));
                    _webSocketReceiveState = WebSocketReceiveState.Header;
                }
                else if (_webSocketReceiveSegment.Count==0)
                {
                    var tcs = _webSocketReceiveTcs;
                    _webSocketReceiveTcs = null;
                    tcs.SetResult(new WebSocketReceiveTuple(_webSocketFrameOpcode, false, _webSocketReceiveCount));
                }
                if (Callback.ReceiveDataLength == 0)
                {
                    Callback.StartReceiveData();
                }
            }
            else if (_webSocketReceiveState == WebSocketReceiveState.Error)
            {
                var tcs = _webSocketReceiveTcs;
                _webSocketReceiveTcs = null;
                tcs.SetCanceled();
            }
        }

        void Unmask(byte[] src, int srcOfs, byte[] dst, int dstOfs, int len)
        {
            while (len-- > 0)
            {
                byte b;
                if (_maskIndex == 0) b = _webSocketMask0;
                else if (_maskIndex == 1) b = _webSocketMask1;
                else if (_maskIndex == 2) b = _webSocketMask2;
                else b = _webSocketMask3;
                dst[dstOfs] = (byte)(src[srcOfs] ^ b);
                srcOfs++;
                dstOfs++;
                _maskIndex = (_maskIndex + 1) & 3;
            }
        }

        int ParseHeader(byte[] buffer, int offset, int length)
        {
            var b0 = buffer[offset];
            if ((b0 & 0x70) != 0) return -1;
            if (!ValidWebSocketOpcode((byte)(b0 & 0xf))) return -1;
            if (length < 2) return 0;
            var b = buffer[offset + 1];
            if ((b & 0x80) != 0x80) return -1;
            b = (byte)(b & 0x7f);
            _webSocketFrameLast = (b0 & 0x80) != 0;
            _webSocketFrameOpcode = (byte)(b0 & 0xf);
            if (b == 126)
            {
                if (length < 8) return 0;
                _webSocketFrameLen = (ulong)((buffer[offset + 2] << 8) + buffer[offset + 3]);
                ReadMask(buffer, offset + 4);
                return 8;
            }
            if (b == 127)
            {
                if (length < 14) return 0;
                _webSocketFrameLen = (ulong)((buffer[offset + 2] << 56) +
                    (buffer[offset + 3] << 48) +
                    (buffer[offset + 4] << 40) +
                    (buffer[offset + 5] << 32) +
                    (buffer[offset + 6] << 24) +
                    (buffer[offset + 7] << 16) +
                    (buffer[offset + 8] << 8) +
                    buffer[offset + 9]);
                ReadMask(buffer, offset + 10);
                return 14;
            }
            if (length < 6) return 0;
            _webSocketFrameLen = b;
            ReadMask(buffer, offset + 2);
            return 6;
        }

        void ReadMask(byte[] buffer, int offset)
        {
            _webSocketMask0 = buffer[offset];
            _webSocketMask1 = buffer[offset + 1];
            _webSocketMask2 = buffer[offset + 2];
            _webSocketMask3 = buffer[offset + 3];
        }

        bool ValidWebSocketOpcode(byte i)
        {
            return i <= 2 || i >= 8 && i <= 10;
        }

        enum WebSocketReceiveState
        {
            Header,
            Body,
            Error
        }
    }
}