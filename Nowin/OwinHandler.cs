using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Nowin
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
        readonly IDictionary<string, object> _owinCapabilities;
        readonly int _handlerId;

        readonly OwinEnvironment _environment;
        internal readonly Dictionary<string, string[]> ReqHeaders;
        internal readonly Dictionary<string, string[]> RespHeaders;
        IDictionary<string, string[]> _overwrittenResponseHeaders;
        bool _inWebSocket;
        OwinApp _webSocketFunc;
        ArraySegment<byte> _webSocketReceiveSegment;
        TaskCompletionSource<WebSocketReceiveTuple> _webSocketReceiveTcs;

        enum WebSocketReceiveState
        {
            Header,
            Body,
            Closing,
            Close,
            Error
        }

        volatile WebSocketReceiveState _webSocketReceiveState;
        ulong _webSocketFrameLen;
        byte _webSocketMask0;
        byte _webSocketMask1;
        byte _webSocketMask2;
        byte _webSocketMask3;
        bool _webSocketFrameLast;
        bool _webSocketNextSendIsStartOfMessage;
        byte _webSocketFrameOpcode;
        int _webSocketReceiveCount;
        int _maskIndex;
        int _webSocketReceiving;
        int _webSocketSendBufferUsedSize;
        readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1);
        readonly Dictionary<string, object> _webSocketEnv;
        TaskCompletionSource<object> _webSocketTcsReceivedClose;
        readonly List<KeyValuePair<Action<object>, object>> _onHeadersList = new List<KeyValuePair<Action<object>, object>>();
        public readonly Action<Action<object>, object> OnSendingHeadersAction;

        public IHttpLayerCallback Callback { set; internal get; }

        public WebSocketAccept WebSocketAcceptFunc
        {
            get { return WebSocketAcceptMethod; }
        }

        public object Capabilities
        {
            get { return _owinCapabilities; }
        }

        public OwinHandler(OwinApp app, IDictionary<string, object> owinCapabilities, int handlerId)
        {
            _app = app;
            _owinCapabilities = owinCapabilities;
            _handlerId = handlerId;
            _environment = new OwinEnvironment(this);
            ReqHeaders = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            RespHeaders = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            OnSendingHeadersAction = OnSendingHeadersMethod;
            _webSocketEnv = new Dictionary<string, object>
                {
                    {"websocket.SendAsync", (WebSocketSendAsync) WebSocketSendAsyncMethod},
                    {"websocket.ReceiveAsync", (WebSocketReceiveAsync) WebSocketReceiveAsyncMethod},
                    {"websocket.CloseAsync", (WebSocketCloseAsync) WebSocketCloseAsyncMethod},
                    {"websocket.Version", "1.0"},
                    {"websocket.CallCancelled", null}
                };
        }

        void OnSendingHeadersMethod(Action<object> action, object state)
        {
            if (Callback.HeadersSend) throw new InvalidOperationException("Headers already sent");
            _onHeadersList.Add(new KeyValuePair<Action<object>, object>(action, state));
        }

        public void Dispose()
        {
        }

        public void PrepareForRequest()
        {
            TraceSources.CoreDebug.TraceInformation("ID{0,-5} PrepareForRequest", _handlerId);
            _inWebSocket = false;
            _webSocketFunc = null;
            _environment.Reset();
            _onHeadersList.Clear();
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
            Callback.ResponseReasonPhase = null;
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
            for (int i = _onHeadersList.Count - 1; i >= 0; i--)
            {
                var p = _onHeadersList[i];
                p.Key(p.Value);
            }
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
            TraceSources.CoreDebug.TraceInformation("ID{0,-5} WebSocketAcceptMethod", _handlerId);
            if (dictionary != null)
            {
                object value;
                if (dictionary.TryGetValue("websocket.SubProtocol", out value) && value is string)
                {
                    RespHeaders.Remove("Sec-WebSocket-Protocol");
                    RespHeaders.Add("Sec-WebSocket-Protocol", new[] { (string)value });
                }
            }
            _webSocketFunc = func;
            _inWebSocket = true;
            _webSocketReceiveState = WebSocketReceiveState.Header;
            _webSocketNextSendIsStartOfMessage = true;
            _webSocketSendBufferUsedSize = 0;
            Callback.UpgradeToWebSocket();
        }

        public void UpgradedToWebSocket(bool success)
        {
            TraceSources.CoreDebug.TraceInformation("ID{0,-5} UpgradedToWebSocket {1}", _handlerId, success);
            if (!success)
            {
                Callback.ResponseStatusCode = 500;
                Callback.ResponseFinished();
                return;
            }
            _webSocketEnv["websocket.CallCancelled"] = Callback.CallCancelled;
            _webSocketEnv.Remove("websocket.ClientCloseStatus");
            _webSocketEnv.Remove("websocket.ClientCloseDescription");
            try
            {
                var task = _webSocketFunc(_webSocketEnv);
                task.ContinueWith((t, o) => ((OwinHandler)o).Callback.CloseConnection(), this);
            }
            catch
            {
                Callback.CloseConnection();
            }
        }

        public void FinishReceiveData(bool success)
        {
            _webSocketReceiving = 0;
            if (!success)
            {
                _webSocketReceiveState = WebSocketReceiveState.Close;
                var tcs = _webSocketReceiveTcs;
                if (tcs != null) tcs.SetCanceled();
                var tcs2 = _webSocketTcsReceivedClose;
                if (tcs2 != null) tcs2.TrySetResult(null);
                return;
            }
            ParseWebSocketReceivedData();
        }

        async Task WebSocketSendAsyncMethod(ArraySegment<byte> data, int messageType, bool endOfMessage, CancellationToken cancel)
        {
            TraceSources.CoreDebug.TraceInformation("ID{0,-5} WebSocketSendAsyncMethod Len:{1} MessageType:{2} EndOfMessage:{3}", _handlerId, data.Count, messageType, endOfMessage);
            if (!_webSocketNextSendIsStartOfMessage)
            {
                messageType = 0;
            }
            await _sendLock.WaitAsync(cancel);
            try
            {
                var buf = Callback.Buffer;
                var maxlen = Math.Min(Callback.SendDataLength - 4, 65535);
                var last = false;
                var o = Callback.SendDataOffset + 4;
                do
                {
                    if (_webSocketReceiveState == WebSocketReceiveState.Close)
                    {
                        throw new OperationCanceledException("Connection is already closed");
                    }
                    var l = _webSocketSendBufferUsedSize + data.Count;
                    if (l > maxlen) l = maxlen; else last = true;
                    Array.Copy(data.Array, data.Offset, buf, o + _webSocketSendBufferUsedSize, l - _webSocketSendBufferUsedSize);
                    data = new ArraySegment<byte>(data.Array, data.Offset + l - _webSocketSendBufferUsedSize, data.Count - l + _webSocketSendBufferUsedSize);
                    if (data.Count == 0 && last && !endOfMessage)
                    {
                        _webSocketSendBufferUsedSize = l;
                        return;
                    }
                    _webSocketSendBufferUsedSize = 0;
                    int headerSize;
                    if (l < 126)
                    {
                        buf[o - 1] = (byte)l;
                        headerSize = 2;
                    }
                    else
                    {
                        buf[o - 3] = 126;
                        buf[o - 2] = (byte)(l / 256);
                        buf[o - 1] = (byte)(l % 256);
                        headerSize = 4;
                    }
                    buf[o - headerSize] = (byte)(((last & endOfMessage) ? 0x80 : 0) + messageType);
                    messageType = 0;
                    await Callback.SendData(buf, o - headerSize, headerSize + l);
                } while (!last);
                _webSocketNextSendIsStartOfMessage = endOfMessage;
            }
            finally
            {
                _sendLock.Release();
            }
        }

        Task<WebSocketReceiveTuple> WebSocketReceiveAsyncMethod(ArraySegment<byte> data, CancellationToken cancel)
        {
            TraceSources.CoreDebug.TraceInformation("ID{0,-5} WebSocketReceiveAsyncMethod buffer:{1}", _handlerId, data.Count);
            _webSocketReceiveSegment = data;
            var tcs = new TaskCompletionSource<WebSocketReceiveTuple>();
            if (_webSocketReceiveState == WebSocketReceiveState.Close || _webSocketReceiveState == WebSocketReceiveState.Closing)
            {
                tcs.SetCanceled();
                return tcs.Task;
            }
            _webSocketReceiveTcs = tcs;
            _webSocketReceiveCount = 0;
            ParseWebSocketReceivedData();
            return tcs.Task;
        }

        async Task WebSocketCloseAsyncMethod(int closeStatus, string closeDescription, CancellationToken cancel)
        {
            if (_webSocketReceiveState == WebSocketReceiveState.Close)
            {
                TraceSources.CoreDebug.TraceInformation("ID{0,-5} Ignoring WebSocketCloseAsync closeStatus:{1} desc:{2}", _handlerId, closeStatus, closeDescription);
                return;
            }
            TraceSources.CoreDebug.TraceInformation("ID{0,-5} WebSocketCloseAsync closeStatus:{1} desc:{2}", _handlerId, closeStatus, closeDescription);
            await _sendLock.WaitAsync(cancel);
            try
            {
                var buf = Callback.Buffer;
                var maxlen = Math.Min(Callback.SendDataLength - 4, 65535);
                var l = 2;
                var o = Callback.SendDataOffset + 4;
                buf[o] = (byte)(closeStatus / 256);
                buf[o + 1] = (byte)(closeStatus % 256);
                if (Encoding.UTF8.GetByteCount(closeDescription) > maxlen)
                {
                    closeDescription = "";
                }
                l += Encoding.UTF8.GetBytes(closeDescription, 0, closeDescription.Length, buf, o + 2);
                int headerSize;
                if (l < 126)
                {
                    buf[o - 1] = (byte)l;
                    headerSize = 2;
                }
                else
                {
                    buf[o - 3] = 126;
                    buf[o - 2] = (byte)(l / 256);
                    buf[o - 1] = (byte)(l % 256);
                    headerSize = 4;
                }
                buf[o - headerSize] = 0x88; // Final frame of close
                _webSocketTcsReceivedClose = new TaskCompletionSource<object>();
                await Callback.SendData(buf, o - headerSize, headerSize + l);
                if (_webSocketReceiveState == WebSocketReceiveState.Close || _webSocketReceiveState == WebSocketReceiveState.Closing)
                {
                    _webSocketTcsReceivedClose.TrySetResult(null);
                }
                await _webSocketTcsReceivedClose.Task;
            }
            finally
            {
                _sendLock.Release();
            }
        }

        void ParseWebSocketReceivedData()
        {
            if (Callback.ReceiveDataLength == 0)
            {
                StartReciveDataIfNotAlreadyReceiving();
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
                if (_webSocketFrameOpcode == 0x8)
                {
                    if (Callback.ReceiveDataLength < (int)_webSocketFrameLen)
                    {
                        StartReciveDataIfNotAlreadyReceiving();
                        return;
                    }
                    var buf = Callback.Buffer;
                    var o = Callback.ReceiveDataOffset;
                    Unmask(buf, o, buf, o, (int)_webSocketFrameLen);
                    if (_webSocketFrameLen >= 2)
                    {
                        _webSocketEnv.Add("websocket.ClientCloseStatus", buf[o] * 256 + buf[o + 1]);
                        _webSocketEnv.Add("websocket.ClientCloseDescription",
                                          new String(Encoding.UTF8.GetChars(buf, o + 2, (int)_webSocketFrameLen - 2)));
                    }
                    else
                    {
                        _webSocketEnv.Add("websocket.ClientCloseStatus", 0);
                        _webSocketEnv.Add("websocket.ClientCloseDescription", "");
                    }
                    TraceSources.CoreDebug.TraceInformation(
                        "ID{0,-5} Received WebSocketClose Status:{1} Desc:{2}",
                        _handlerId,
                        _webSocketEnv["websocket.ClientCloseStatus"],
                        _webSocketEnv["websocket.ClientCloseDescription"]);

                    Callback.ConsumeReceiveData((int)_webSocketFrameLen);
                    _webSocketReceiveState = WebSocketReceiveState.Closing;
                    var tcs = _webSocketReceiveTcs;
                    if (tcs != null)
                    {
                        _webSocketReceiveTcs = null;
                        tcs.SetResult(new WebSocketReceiveTuple(0x8, true, 0));
                    }
                    var tcs2 = _webSocketTcsReceivedClose;
                    if (tcs2 != null)
                    {
                        tcs2.TrySetResult(null);
                    }
                    return;
                }
                var len = (int)Math.Min(_webSocketFrameLen, (ulong)_webSocketReceiveSegment.Count);
                if (Callback.ReceiveDataLength < len) len = Callback.ReceiveDataLength;
                Unmask(Callback.Buffer, Callback.ReceiveDataOffset, _webSocketReceiveSegment.Array,
                       _webSocketReceiveSegment.Offset, len);
                Callback.ConsumeReceiveData(len);
                _webSocketFrameLen -= (ulong)len;
                _webSocketReceiveCount += len;
                _webSocketReceiveSegment = new ArraySegment<byte>(_webSocketReceiveSegment.Array, _webSocketReceiveSegment.Offset + len, _webSocketReceiveSegment.Count - len);
                if (_webSocketFrameLen == 0)
                {
                    var tcs = _webSocketReceiveTcs;
                    if (tcs != null)
                    {
                        _webSocketReceiveTcs = null;
                        TraceSources.CoreDebug.TraceInformation("ID{0,-5} Received WebSocketFrame Opcode:{1} Last:{2} Length:{3}", _handlerId, _webSocketFrameOpcode, _webSocketFrameLast, _webSocketReceiveCount);
                        tcs.SetResult(new WebSocketReceiveTuple(_webSocketFrameOpcode, _webSocketFrameLast, _webSocketReceiveCount));
                    }
                    _webSocketReceiveState = WebSocketReceiveState.Header;
                }
                else if (_webSocketReceiveSegment.Count == 0)
                {
                    var tcs = _webSocketReceiveTcs;
                    if (tcs != null)
                    {
                        _webSocketReceiveTcs = null;
                        TraceSources.CoreDebug.TraceInformation("ID{0,-5} Received WebSocketFrame Opcode:{1} Last:{2} Length:{3}", _handlerId, _webSocketFrameOpcode, false, _webSocketReceiveCount);
                        tcs.SetResult(new WebSocketReceiveTuple(_webSocketFrameOpcode, false, _webSocketReceiveCount));
                    }
                }
                if (Callback.ReceiveDataLength == 0)
                {
                    StartReciveDataIfNotAlreadyReceiving();
                }
            }
            else if (_webSocketReceiveState == WebSocketReceiveState.Error)
            {
                var tcs = _webSocketReceiveTcs;
                if (tcs != null)
                {
                    _webSocketReceiveTcs = null;
                    tcs.SetCanceled();
                }
            }
        }

        void StartReciveDataIfNotAlreadyReceiving()
        {
            if (Interlocked.CompareExchange(ref _webSocketReceiving, 1, 0) == 0)
                Callback.StartReceiveData();
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
    }
}