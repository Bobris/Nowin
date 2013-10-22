using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Nowin
{
    class Transport2HttpHandler : ITransportLayerHandler, IHttpLayerCallback
    {
        readonly IHttpLayerHandler _next;
        public readonly int StartBufferOffset;
        public readonly int ReceiveBufferSize;
        public readonly int ResponseBodyBufferOffset;
        readonly int _constantsOffset;
        readonly byte[] _buffer;
        public int ReceiveBufferPos;
        int _receiveBufferFullness;
        bool _waitingForRequest;
        bool _isHttp10;
        bool _isKeepAlive;
        public bool ShouldSend100Continue;
        public ulong RequestContentLength;
        public bool RequestIsChunked;
        bool _responseHeadersSend;
        readonly bool _isSsl;
        readonly string _serverName;
        readonly IDateHeaderValueProvider _dateProvider;
        readonly IIpIsLocalChecker _ipIsLocalChecker;
        readonly ReqRespStream _reqRespStream;
        volatile TaskCompletionSource<bool> _tcsSend;
        CancellationTokenSource _cancellation;
        int _responseHeaderPos;
        bool _lastPacket;
        bool _responseIsChunked;
        ulong _responseContentLength;
        IPEndPoint _remoteEndPoint;
        IPEndPoint _localEndPoint;
        string _requestPath;
        string _requestQueryString;
        string _requestMethod;
        string _requestScheme;
        string _requestProtocol;
        string _remoteIpAddress;
        string _remotePort;
        bool _isLocal;
        bool _knownIsLocal;
        string _localIpAddress;
        string _localPort;
        int _statusCode;
        string _reasonPhase;
        readonly List<KeyValuePair<string, object>> _responseHeaders = new List<KeyValuePair<string, object>>();
        readonly ThreadLocal<char[]> _charBuffer;
        readonly int _handlerId;

        [Flags]
        enum WebSocketReqConditions
        {
            Start = 0,

            GetMethod = 1,
            UpgradeWebSocket = 2,
            ConnectionUpgrade = 4,
            Version13 = 8,
            ValidKey = 16,

            AllSatisfied = 31
        }

        WebSocketReqConditions _webSocketReqCondition;
        string _webSocketKey;
        bool _isWebSocket;
        bool _startedReceiveData;
        int _disconnecting;
        bool _serverNameOverwrite;
        bool _dateOverwrite;

        public Transport2HttpHandler(IHttpLayerHandler next, bool isSsl, string serverName, IDateHeaderValueProvider dateProvider, IIpIsLocalChecker ipIsLocalChecker, byte[] buffer, int startBufferOffset, int receiveBufferSize, int constantsOffset, ThreadLocal<char[]> charBuffer, int handlerId)
        {
            _next = next;
            StartBufferOffset = startBufferOffset;
            ReceiveBufferSize = receiveBufferSize;
            ResponseBodyBufferOffset = StartBufferOffset + ReceiveBufferSize * 2 + 8;
            _constantsOffset = constantsOffset;
            _charBuffer = charBuffer;
            _handlerId = handlerId;
            _buffer = buffer;
            _isSsl = isSsl;
            _serverName = serverName;
            _dateProvider = dateProvider;
            _ipIsLocalChecker = ipIsLocalChecker;
            _cancellation = new CancellationTokenSource();
            _reqRespStream = new ReqRespStream(this);
            _next.Callback = this;
        }

        public int ReceiveBufferDataLength
        {
            get { return _receiveBufferFullness - StartBufferOffset - ReceiveBufferPos; }
        }

        void ParseRequest(byte[] buffer, int startBufferOffset, int posOfReqEnd)
        {
            _isWebSocket = false;
            _next.PrepareForRequest();
            posOfReqEnd -= 2;
            _responseHeaders.Clear();
            _webSocketReqCondition = WebSocketReqConditions.Start;
            if (_cancellation.IsCancellationRequested)
                _cancellation = new CancellationTokenSource();
            _responseIsChunked = false;
            _responseContentLength = ulong.MaxValue;
            var pos = startBufferOffset;
            _requestMethod = ParseHttpMethod(buffer, ref pos);
            _requestScheme = _isSsl ? "https" : "http";
            string reqHost;
            ParseHttpPath(buffer, ref pos, out _requestPath, out _requestQueryString, ref _requestScheme, out reqHost);
            ParseHttpProtocol(buffer, ref pos, out _requestProtocol);
            if (!SkipCrLf(buffer, ref pos)) throw new Exception("Request line does not end with CRLF");
            _isKeepAlive = !_isHttp10;
            ShouldSend100Continue = false;
            RequestContentLength = 0;
            RequestIsChunked = false;
            if (!ParseHttpHeaders(buffer, pos, posOfReqEnd)) throw new Exception("Request headers cannot be parsed");
        }

        bool ParseHttpHeaders(byte[] buffer, int pos, int posOfReqEnd)
        {
            var name = "";
            while (pos < posOfReqEnd)
            {
                int start;
                var newHeaderKey = false;
                if (!IsSpaceOrTab(buffer[pos]))
                {
                    start = pos;
                    SkipTokenChars(buffer, ref pos);
                    if (buffer[pos] != ':') return false;
                    name = StringFromLatin1(buffer, start, pos);
                    newHeaderKey = true;
                }
                pos++;
                SkipSpacesOrTabs(buffer, ref pos);
                start = pos;
                SkipToCR(buffer, ref pos);
                var value = StringFromLatin1(buffer, start, pos);
                if (newHeaderKey)
                    ProcessRequestHeader(name, value);
                else
                    _next.AddRequestHeader(name, value);
                SkipCrLf(buffer, ref pos);
            }
            return true;
        }

        void ProcessRequestHeader(string name, string value)
        {
            if (name.Equals("Connection", StringComparison.OrdinalIgnoreCase))
            {
                if (_isHttp10)
                {
                    if (value.Equals("Keep-Alive", StringComparison.OrdinalIgnoreCase))
                    {
                        _isKeepAlive = true;
                    }
                }
                else
                {
                    if (value.Equals("Close", StringComparison.OrdinalIgnoreCase))
                    {
                        _isKeepAlive = false;
                    }
                    else if (value.IndexOf("Upgrade", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        _webSocketReqCondition |= WebSocketReqConditions.ConnectionUpgrade;
                    }
                }
            }
            else if (name.Equals("Expect", StringComparison.OrdinalIgnoreCase))
            {
                if (value.Equals("100-Continue", StringComparison.OrdinalIgnoreCase))
                {
                    ShouldSend100Continue = true;
                }
            }
            else if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                if (!ulong.TryParse(value, out RequestContentLength))
                {
                    throw new InvalidDataException(string.Format("Wrong request content length: {0}", value));
                }
            }
            else if (name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
            {
                if (value.Equals("chunked", StringComparison.OrdinalIgnoreCase))
                {
                    RequestIsChunked = true;
                    RequestContentLength = ulong.MaxValue;
                }
            }
            else if (name.Equals("Upgrade", StringComparison.OrdinalIgnoreCase))
            {
                if (value.Equals("websocket", StringComparison.OrdinalIgnoreCase))
                {
                    _webSocketReqCondition |= WebSocketReqConditions.UpgradeWebSocket;
                }
            }
            else if (name.Equals("Sec-WebSocket-Version", StringComparison.OrdinalIgnoreCase))
            {
                if (value == "13")
                {
                    _webSocketReqCondition |= WebSocketReqConditions.Version13;
                }
            }
            else if (name.Equals("Sec-WebSocket-Key", StringComparison.OrdinalIgnoreCase))
            {
                _webSocketReqCondition |= WebSocketReqConditions.ValidKey;
                _webSocketKey = value;
            }
            _next.AddRequestHeader(name, value);
        }

        void SkipToCR(byte[] buffer, ref int pos)
        {
            while (buffer[pos] != 13) pos++;
        }

        static void SkipTokenChars(byte[] buffer, ref int pos)
        {
            while (true)
            {
                var ch = buffer[pos];
                if (ch <= 32) break;
                if (ch == ':') break;
                pos++;
            }
        }

        static void SkipSpacesOrTabs(byte[] buffer, ref int pos)
        {
            while (IsSpaceOrTab(buffer[pos])) pos++;
        }

        static bool IsSpaceOrTab(byte ch)
        {
            return ch == 32 || ch == 9;
        }

        static bool SkipCrLf(byte[] buffer, ref int pos)
        {
            if (buffer[pos] == 13 && buffer[pos + 1] == 10)
            {
                pos += 2;
                return true;
            }
            return false;
        }

        void ParseHttpProtocol(byte[] buffer, ref int pos, out string reqProtocol)
        {
            if (buffer[pos] == 'H' && buffer[pos + 1] == 'T' && buffer[pos + 2] == 'T' && buffer[pos + 3] == 'P' && buffer[pos + 4] == '/' && buffer[pos + 5] == '1' && buffer[pos + 6] == '.' && buffer[pos + 8] == 13)
            {
                switch (buffer[pos + 7])
                {
                    case (byte)'0':
                        {
                            reqProtocol = "HTTP/1.0";
                            pos += 8;
                            _isHttp10 = true;
                            return;
                        }
                    case (byte)'1':
                        {
                            reqProtocol = "HTTP/1.1";
                            pos += 8;
                            _isHttp10 = false;
                            return;
                        }
                }
                reqProtocol = StringFromLatin1(buffer, pos, pos + 8);
                pos += 8;
                _isHttp10 = false;
                return;
            }
            var p = pos;
            SearchForFirstSpaceOrEndOfLine(buffer, ref p);
            reqProtocol = StringFromLatin1(buffer, pos, p);
            throw new InvalidDataException(string.Format("Unsupported request protocol: {0}", reqProtocol));
        }

        void ParseHttpPath(byte[] buffer, ref int pos, out string reqPath, out string reqQueryString, ref string reqScheme, out string reqHost)
        {
            var start = pos;
            var p = start;
            reqHost = null;
            if (buffer[p] == '/')
            {
                p++;
                switch (SearchForFirstSpaceOrQuestionMarkOrEndOfLine(buffer, ref p))
                {
                    case (byte)' ':
                        reqPath = ParsePath(buffer, start, p);
                        reqQueryString = "";
                        pos = p + 1;
                        return;
                    case 13:
                        reqPath = ParsePath(buffer, start, p);
                        reqQueryString = "";
                        pos = p;
                        return;
                    case (byte)'?':
                        reqPath = ParsePath(buffer, start, p);
                        p++;
                        start = p;
                        switch (SearchForFirstSpaceOrEndOfLine(buffer, ref p))
                        {
                            case (byte)' ':
                                reqQueryString = StringFromLatin1(buffer, start, p);
                                pos = p + 1;
                                return;
                            case 13:
                                reqQueryString = StringFromLatin1(buffer, start, p);
                                pos = p;
                                return;
                            default:
                                throw new InvalidOperationException();
                        }
                    default:
                        throw new InvalidOperationException();
                }
            }
            if (buffer[p] == '*' && buffer[p + 1] == ' ')
            {
                reqPath = "*";
                reqQueryString = "";
                pos = p + 2;
                return;
            }
            throw new NotImplementedException();
        }

        char[] GetCharBuffer()
        {
            return _charBuffer.Value;
        }

        string ParsePath(byte[] buffer, int start, int end)
        {
            var chs = GetCharBuffer();
            var used = 0;
            while (start < end)
            {
                var ch = buffer[start++];
                if (ch == '%')
                {
                    ch = buffer[start++];
                    var v1 = ParseHexChar(ch);
                    if (v1 < 0)
                    {
                        chs[used++] = '%';
                        chs[used++] = (char)ch;
                        continue;
                    }
                    var v2 = ParseHexChar(buffer[start++]);
                    if (v2 < 0)
                    {
                        chs[used++] = '%';
                        chs[used++] = (char)ch;
                        chs[used++] = (char)buffer[start - 1];
                        continue;
                    }
                    chs[used++] = (char)(v1 * 16 + v2);
                }
                else
                {
                    chs[used++] = (char)ch;
                }
            }
            return new string(chs, 0, used);
        }

        public static int ParseHexChar(byte ch)
        {
            if (ch >= '0' && ch <= '9') return ch - '0';
            if (ch >= 'A' && ch <= 'F') return ch - 'A' + 10;
            if (ch >= 'a' && ch <= 'f') return ch - 'a' + 10;
            return -1;
        }

        static byte SearchForFirstSpaceOrEndOfLine(byte[] buffer, ref int p)
        {
            while (true)
            {
                var ch = buffer[p];
                if (ch == ' ' || ch == 13) return ch;
                p++;
            }
        }

        static byte SearchForFirstSpaceOrQuestionMarkOrEndOfLine(byte[] buffer, ref int p)
        {
            while (true)
            {
                var ch = buffer[p];
                if (ch == ' ' || ch == '?' || ch == 13) return ch;
                p++;
            }
        }

        string ParseHttpMethod(byte[] buffer, ref int pos)
        {
            var p = pos;
            var start = p;
            switch (buffer[p])
            {
                case (byte)'G':
                    if (buffer[p + 1] == 'E' && buffer[p + 2] == 'T' && buffer[p + 3] == ' ')
                    {
                        pos = p + 4;
                        _webSocketReqCondition |= WebSocketReqConditions.GetMethod;
                        return "GET";
                    }
                    break;
                case (byte)'P':
                    if (buffer[p + 1] == 'O' && buffer[p + 2] == 'S' && buffer[p + 3] == 'T' && buffer[p + 4] == ' ')
                    {
                        pos = p + 5;
                        return "POST";
                    }
                    if (buffer[p + 1] == 'U' && buffer[p + 2] == 'T' && buffer[p + 3] == ' ')
                    {
                        pos = p + 4;
                        return "PUT";
                    }
                    break;
                case (byte)'H':
                    if (buffer[p + 1] == 'E' && buffer[p + 2] == 'A' && buffer[p + 3] == 'D' && buffer[p + 4] == ' ')
                    {
                        pos = p + 5;
                        return "HEAD";
                    }
                    break;
                case (byte)'D':
                    if (buffer[p + 1] == 'E' && buffer[p + 2] == 'L' && buffer[p + 3] == 'E' && buffer[p + 4] == 'T' && buffer[p + 5] == 'E' && buffer[p + 6] == ' ')
                    {
                        pos = p + 7;
                        return "DELETE";
                    }
                    break;
                case (byte)'T':
                    if (buffer[p + 1] == 'R' && buffer[p + 2] == 'A' && buffer[p + 3] == 'C' && buffer[p + 4] == 'E' && buffer[p + 5] == ' ')
                    {
                        pos = p + 6;
                        return "TRACE";
                    }
                    break;
                case (byte)'O':
                    if (buffer[p + 1] == 'P' && buffer[p + 2] == 'T' && buffer[p + 3] == 'I' && buffer[p + 4] == 'O' && buffer[p + 5] == 'N' && buffer[p + 6] == 'S' && buffer[p + 7] == ' ')
                    {
                        pos = p + 8;
                        return "OPTIONS";
                    }
                    break;
                case (byte)' ':
                    {
                        pos = p + 1;
                        return "";
                    }
                case 13:
                    {
                        return "";
                    }
            }
            p++;
            while (true)
            {
                var b = buffer[p];

                if (b == (byte)' ')
                {
                    pos = p + 1;
                    break;
                }
                if (b == 13)
                {
                    pos = p;
                    break;
                }
                p++;
            }
            return StringFromLatin1(buffer, start, p);
        }

        string StringFromLatin1(byte[] buffer, int start, int end)
        {
            var len = end - start;
            var chs = GetCharBuffer();
            for (var i = 0; i < len; i++)
            {
                chs[i] = (char)buffer[start + i];
            }
            return new string(chs, 0, len);
        }

        void FillResponse(bool finished)
        {
            PrepareResponseHeaders();
            var status = _statusCode;
            if (status < 200 || status > 999)
            {
                status = 500;
            }
            if (finished)
            {
                if (_responseContentLength != ulong.MaxValue && _responseContentLength != _reqRespStream.ResponseLength)
                {
                    status = 500;
                }
                _responseContentLength = _reqRespStream.ResponseLength;
            }
            _responseHeaderPos = 0;
            HeaderAppend("HTTP/1.1 ");
            HeaderAppendHttpStatus(status);
            if (_reasonPhase != null)
            {
                HeaderAppend(" ");
                HeaderAppend(_reasonPhase);
            }
            HeaderAppendCrLf();
            if (status == 500)
            {
                _isKeepAlive = false;
            }
            if (_responseContentLength != ulong.MaxValue)
            {
                HeaderAppend("Content-Length: ");
                HeaderAppend(_responseContentLength.ToString(CultureInfo.InvariantCulture));
                HeaderAppendCrLf();
            }
            else
            {
                if (_isHttp10)
                    _isKeepAlive = false;
                else
                {
                    HeaderAppend("Transfer-Encoding: chunked\r\n");
                    _responseIsChunked = true;
                }
            }
            if (_isHttp10 && _isKeepAlive)
            {
                HeaderAppend("Connection: keep-alive\r\n");
            }
            if (!_isKeepAlive)
            {
                HeaderAppend("Connection: close\r\n");
            }
            if (!_serverNameOverwrite)
            {
                HeaderAppend("Server: ");
                HeaderAppend(_serverName);
                HeaderAppendCrLf();
            }
            if (!_dateOverwrite)
            {
                HeaderAppend("Date: ");
                HeaderAppend(_dateProvider.Value);
                HeaderAppendCrLf();
            }
            foreach (var header in _responseHeaders)
            {
                if (header.Value is String)
                {
                    HeaderAppend(header.Key);
                    HeaderAppend(": ");
                    HeaderAppend((String)header.Value);
                    HeaderAppendCrLf();
                }
                else
                {
                    foreach (var value in (IEnumerable<string>)header.Value)
                    {
                        HeaderAppend(header.Key);
                        HeaderAppend(": ");
                        HeaderAppend(value);
                        HeaderAppendCrLf();
                    }
                }
            }
            _responseHeaders.Clear();
            HeaderAppendCrLf();
        }

        void HeaderAppendHttpStatus(int status)
        {
            // It always fits so skip buffer size check
            var j = StartBufferOffset + ReceiveBufferSize + _responseHeaderPos;
            _buffer[j++] = (byte)('0' + status / 100);
            _buffer[j++] = (byte)('0' + status / 10 % 10);
            _buffer[j] = (byte)('0' + status % 10);
            _responseHeaderPos += 3;
        }

        void HeaderAppendCrLf()
        {
            if (_responseHeaderPos > ReceiveBufferSize - 2)
            {
                _responseHeaderPos += 2;
                return;
            }
            var i = StartBufferOffset + ReceiveBufferSize + _responseHeaderPos;
            _buffer[i] = 13;
            _buffer[i + 1] = 10;
            _responseHeaderPos += 2;
        }

        void HeaderAppend(string text)
        {
            if (_responseHeaderPos > ReceiveBufferSize - text.Length)
            {
                _responseHeaderPos += text.Length;
                return;
            }
            var j = StartBufferOffset + ReceiveBufferSize + _responseHeaderPos;
            foreach (var ch in text)
            {
                _buffer[j++] = (byte)ch;
            }
            _responseHeaderPos += text.Length;
        }

        void NormalizeReceiveBuffer()
        {
            if (ReceiveBufferPos == 0) return;
            Array.Copy(_buffer, StartBufferOffset + ReceiveBufferPos, _buffer, StartBufferOffset, _receiveBufferFullness - StartBufferOffset - ReceiveBufferPos);
            _receiveBufferFullness -= ReceiveBufferPos;
            ReceiveBufferPos = 0;
        }

        void SendHttpResponseAndPrepareForNext()
        {
            var offset = _reqRespStream.ResponseStartOffset;
            var len = _reqRespStream.ResponseLocalPos;
            if (!_responseHeadersSend)
            {
                FillResponse(true);
                if (_responseHeaderPos > ReceiveBufferSize)
                {
                    SendInternalServerError();
                    return;
                }
                OptimallyMergeTwoRegions(_buffer, StartBufferOffset + ReceiveBufferSize, _responseHeaderPos, ref offset, ref len);
                _responseHeadersSend = true;
            }
            else
            {
                if (_responseContentLength != ulong.MaxValue && _reqRespStream.ResponseLength != _responseContentLength)
                {
                    CloseConnection();
                    return;
                }
                if (_responseIsChunked)
                {
                    if (len != 0)
                    {
                        WrapInChunk(_buffer, ref offset, ref len);
                    }
                    AppendZeroChunk(_buffer, offset, ref len);
                }
            }
            var tcs = _tcsSend;
            if (tcs != null)
            {
                tcs.Task.ContinueWith(_ =>
                    {
                        _lastPacket = true;
                        Callback.StartSend(_buffer, offset, len);
                    });
                return;
            }
            _lastPacket = true;
            Callback.StartSend(_buffer, offset, len);
        }

        static void AppendZeroChunk(byte[] buffer, int offset, ref int len)
        {
            offset += len;
            buffer[offset++] = (byte)'0';
            buffer[offset++] = 13;
            buffer[offset++] = 10;
            buffer[offset++] = 13;
            buffer[offset] = 10;
            len += 5;
        }

        async Task DrainRequestStreamAsync()
        {
            while (true)
            {
                var len = await _reqRespStream.ReadAsync(_buffer, StartBufferOffset + ReceiveBufferSize, ReceiveBufferSize);
                if (len < ReceiveBufferSize) return;
            }
        }

        void SendInternalServerError()
        {
            var tcs = _tcsSend;
            if (tcs != null)
            {
                tcs.Task.ContinueWith(_ => SendInternalServerError());
                return;
            }
            _isKeepAlive = false;
            _lastPacket = true;
            try
            {
                Callback.StartSend(Server.Status500InternalServerError, 0, Server.Status500InternalServerError.Length);
            }
            catch (Exception)
            {
                CloseConnection();
            }
        }

        static int FindRequestEnd(byte[] buffer, int start, int end)
        {
            var pos = start;
            while (pos < end)
            {
                var ch = buffer[pos++];
                if (ch != 13) continue;
                if (pos >= end) break;
                ch = buffer[pos++];
                if (ch != 10) continue;
                if (pos >= end) break;
                ch = buffer[pos++];
                if (ch != 13) continue;
                if (pos >= end) break;
                ch = buffer[pos++];
                if (ch != 10) continue;
                return pos;
            }
            return -1;
        }

        void ResetForNextRequest()
        {
            _waitingForRequest = true;
            _responseHeadersSend = false;
            _lastPacket = false;
            _reqRespStream.Reset();
        }

        public Task WriteAsync(byte[] buffer, int startOffset, int len)
        {
            if (!_responseHeadersSend)
            {
                if (_buffer != buffer) throw new InvalidOperationException();
                FillResponse(false);
                if (_responseHeaderPos > ReceiveBufferSize) throw new ArgumentException(string.Format("Response headers are longer({0}) than buffer({1})", _responseHeaderPos, ReceiveBufferSize));
                if (_responseIsChunked && len != 0)
                {
                    WrapInChunk(_buffer, ref startOffset, ref len);
                }
                OptimallyMergeTwoRegions(_buffer, StartBufferOffset + ReceiveBufferSize, _responseHeaderPos, ref startOffset, ref len);
                _responseHeadersSend = true;
            }
            else if (_responseIsChunked)
            {
                if (_buffer != buffer) throw new InvalidOperationException();
                if (len == 0) return Task.Delay(0);
                WrapInChunk(_buffer, ref startOffset, ref len);
            }
            if (_responseContentLength != ulong.MaxValue && _reqRespStream.ResponseLength > _responseContentLength)
            {
                CloseConnection();
                throw new ArgumentOutOfRangeException("len", "Cannot send more bytes than specified in Content-Length header");
            }
            var tcs = _tcsSend;
            if (tcs != null)
            {
                return tcs.Task.ContinueWith(_ =>
                {
                    tcs = new TaskCompletionSource<bool>();
                    Thread.MemoryBarrier();
                    if (_tcsSend != null)
                    {
                        throw new InvalidOperationException("Want to start send but previous is still sending");
                    }
                    _tcsSend = tcs;
                    Callback.StartSend(buffer, startOffset, len);
                });
            }
            tcs = new TaskCompletionSource<bool>();
            Thread.MemoryBarrier();
            _tcsSend = tcs;
            Callback.StartSend(buffer, startOffset, len);
            return tcs.Task;
        }

        static void WrapInChunk(byte[] buffer, ref int startOffset, ref int len)
        {
            var l = (uint)len;
            var o = (uint)startOffset;
            buffer[o + l] = 13;
            buffer[o + l + 1] = 10;
            buffer[--o] = 10;
            buffer[--o] = 13;
            len += 4;
            do
            {
                var h = l & 15;
                if (h < 10) h += '0'; else h += 'A' - 10;
                buffer[--o] = (byte)h;
                len++;
                l /= 16;
            } while (l > 0);
            startOffset = (int)o;
        }

        static void OptimallyMergeTwoRegions(byte[] buffer, int o1, int l1, ref int o2, ref int l2)
        {
            if (l1 < l2)
            {
                Array.Copy(buffer, o1, buffer, o2 - l1, l1);
                o2 -= l1;
            }
            else
            {
                Array.Copy(buffer, o2, buffer, o1 + l1, l2);
                o2 = o1;
            }
            l2 += l1;
        }

        public void Send100Continue()
        {
            var tcs = new TaskCompletionSource<bool>();
            Thread.MemoryBarrier();
            _tcsSend = tcs;
            Callback.StartSend(_buffer, _constantsOffset, Server.Status100Continue.Length);
        }

        public void StartNextReceive()
        {
            NormalizeReceiveBuffer();
            var count = StartBufferOffset + ReceiveBufferSize - _receiveBufferFullness;
            if (count > 0)
            {
                Callback.StartReceive(_buffer, _receiveBufferFullness, count);
            }
        }

        public void Dispose()
        {
        }

        public ITransportLayerCallback Callback { set; private get; }

        public void PrepareAccept()
        {
            _disconnecting = 0;
            Callback.StartAccept(_buffer, StartBufferOffset, ReceiveBufferSize);
        }

        public void FinishAccept(byte[] buffer, int offset, int length, IPEndPoint remoteEndPoint, IPEndPoint localEndPoint)
        {
            ResetForNextRequest();
            ReceiveBufferPos = 0;
            _remoteEndPoint = remoteEndPoint;
            _knownIsLocal = false;
            _remoteIpAddress = null;
            _remotePort = null;
            if (!localEndPoint.Equals(_localEndPoint))
            {
                _localEndPoint = localEndPoint;
                _localIpAddress = null;
                _localPort = null;
            }
            _receiveBufferFullness = StartBufferOffset;
            if (length == 0)
            {
                StartNextReceive();
                return;
            }
            FinishReceive(buffer, offset, length);
        }

        public void FinishReceive(byte[] buffer, int offset, int length)
        {
            if (length == -1)
            {
                if (_waitingForRequest)
                {
                    CloseConnection();
                }
                else
                {
                    if (_startedReceiveData)
                    {
                        _startedReceiveData = false;
                        _next.FinishReceiveData(false);
                    }
                    _cancellation.Cancel();
                    _reqRespStream.ConnectionClosed();
                }
                return;
            }

            TraceSources.CoreDebug.TraceInformation("======= Offset {0}, Length {1}", offset - StartBufferOffset, length);
            TraceSources.CoreDebug.TraceInformation(Encoding.UTF8.GetString(buffer, offset, length));

            _receiveBufferFullness = offset + length;
            if (_waitingForRequest)
            {
                NormalizeReceiveBuffer();
                var posOfReqEnd = FindRequestEnd(_buffer, StartBufferOffset, _receiveBufferFullness);
                if (posOfReqEnd < 0)
                {
                    var count = StartBufferOffset + ReceiveBufferSize - _receiveBufferFullness;
                    if (count > 0)
                    {
                        StartNextReceive();
                        return;
                    }
                    SendInternalServerError();
                    return;
                }
                _waitingForRequest = false;
                try
                {
                    ReceiveBufferPos = posOfReqEnd - StartBufferOffset;
                    ParseRequest(_buffer, StartBufferOffset, posOfReqEnd);
                    _next.HandleRequest();
                }
                catch (Exception)
                {
                    ResponseStatusCode = 500;
                    ResponseReasonPhase = null;
                    ResponseFinished();
                }
            }
            else
            {
                if (_startedReceiveData)
                {
                    _startedReceiveData = false;
                    _next.FinishReceiveData(true);
                }
                else if (_reqRespStream.ProcessDataAndShouldReadMore())
                {
                    StartNextReceive();
                }
            }
        }

        public void FinishSend(Exception exception)
        {
            if (exception == null)
            {
                var tcs = _tcsSend;
                _tcsSend = null;
                if (tcs != null)
                {
                    tcs.SetResult(true);
                }
                else if (_isWebSocket)
                {
                    _isWebSocket = false;
                    _next.UpgradedToWebSocket(true);
                }
            }
            else
            {
                var tcs = _tcsSend;
                _tcsSend = null;
                _isKeepAlive = false;
                if (tcs != null)
                {
                    tcs.SetException(exception);
                }
                else if (_isWebSocket)
                {
                    _isWebSocket = false;
                    _next.UpgradedToWebSocket(false);
                }
            }
            if (_lastPacket)
            {
                _lastPacket = false;
                if (_isKeepAlive)
                {
                    ResetForNextRequest();
                    StartNextReceive();
                }
                else
                {
                    CloseConnection();
                }
            }
        }

        public CancellationToken CallCancelled
        {
            get { return _cancellation.Token; }
        }

        public Stream ReqRespBody
        {
            get { return _reqRespStream; }
        }

        public string RequestPath
        {
            get { return _requestPath; }
        }

        public string RequestQueryString
        {
            get { return _requestQueryString; }
        }

        public string RequestMethod
        {
            get { return _requestMethod; }
        }

        public string RequestScheme
        {
            get { return _requestScheme; }
        }

        public string RequestProtocol
        {
            get { return _requestProtocol; }
        }

        public string RemoteIpAddress
        {
            get { return _remoteIpAddress ?? (_remoteIpAddress = _remoteEndPoint.Address.ToString()); }
        }

        public string RemotePort
        {
            get { return _remotePort ?? (_remotePort = _remoteEndPoint.Port.ToString(CultureInfo.InvariantCulture)); }
        }

        public string LocalIpAddress
        {
            get { return _localIpAddress ?? (_localIpAddress = _localEndPoint.Address.ToString()); }
        }

        public string LocalPort
        {
            get { return _localPort ?? (_localPort = _localEndPoint.Port.ToString(CultureInfo.InvariantCulture)); }
        }

        public bool IsLocal
        {
            get
            {
                if (!_knownIsLocal) _isLocal = _ipIsLocalChecker.IsLocal(_remoteEndPoint.Address);
                return _isLocal;
            }
        }

        public bool IsWebSocketReq
        {
            get { return _webSocketReqCondition == WebSocketReqConditions.AllSatisfied; }
        }

        public int ResponseStatusCode
        {
            set { _statusCode = value; }
        }

        public string ResponseReasonPhase
        {
            set { _reasonPhase = value; }
        }

        public ulong ResponseContentLength
        {
            set { _responseContentLength = value; }
        }

        public bool KeepAlive
        {
            set { _isKeepAlive = value; }
        }

        public void AddResponseHeader(string name, string value)
        {
            CheckForHeaderOverwrite(name);
            _responseHeaders.Add(new KeyValuePair<string, object>(name, value));
        }

        public void AddResponseHeader(string name, IEnumerable<string> values)
        {
            CheckForHeaderOverwrite(name);
            _responseHeaders.Add(new KeyValuePair<string, object>(name, values));
        }

        void CheckForHeaderOverwrite(string name)
        {
            if (name.Length == 4 && name.Equals("Date", StringComparison.OrdinalIgnoreCase))
            {
                _dateOverwrite = true;
            }
            else if (name.Length == 6 && name.Equals("Server", StringComparison.OrdinalIgnoreCase))
            {
                _serverNameOverwrite = true;
            }
        }

        public void UpgradeToWebSocket()
        {
            if (_responseHeadersSend)
            {
                _isKeepAlive = false;
                CloseConnection();
                return;
            }
            PrepareResponseHeaders();
            _isKeepAlive = false;
            _responseHeaderPos = 0;
            HeaderAppend("HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: ");
            var sha1 = new SHA1Managed();
            var hash = sha1.ComputeHash(Encoding.ASCII.GetBytes(_webSocketKey + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11"));
            HeaderAppend(Convert.ToBase64String(hash));
            HeaderAppendCrLf();
            foreach (var header in _responseHeaders)
            {
                if (header.Value is String)
                {
                    HeaderAppend(header.Key);
                    HeaderAppend(": ");
                    HeaderAppend((String)header.Value);
                    HeaderAppendCrLf();
                }
                else
                {
                    foreach (var value in (IEnumerable<string>)header.Value)
                    {
                        HeaderAppend(header.Key);
                        HeaderAppend(": ");
                        HeaderAppend(value);
                        HeaderAppendCrLf();
                    }
                }
            }
            _responseHeaders.Clear();
            HeaderAppendCrLf();
            if (_responseHeaderPos > ReceiveBufferSize)
            {
                SendInternalServerError();
                throw new ArgumentOutOfRangeException();
            }
            _isWebSocket = true;
            Callback.StartSend(_buffer, StartBufferOffset + ReceiveBufferSize, _responseHeaderPos);
        }

        void PrepareResponseHeaders()
        {
            _dateOverwrite = false;
            _serverNameOverwrite = false;
            _next.PrepareResponseHeaders();
        }

        public void ResponseFinished()
        {
            if (_statusCode == 500 || _cancellation.IsCancellationRequested)
            {
                _cancellation.Cancel();
                if (!_responseHeadersSend)
                    SendInternalServerError();
                else
                {
                    _isKeepAlive = false;
                    CloseConnection();
                }
                return;
            }
            if (_reqRespStream.RequestPosition != RequestContentLength)
            {
                DrainRequestStreamAsync().ContinueWith((t, o) =>
                {
                    if (t.IsFaulted || t.IsCanceled)
                    {
                        ResponseStatusCode = 500;
                        ((Transport2HttpHandler)o).ResponseFinished();
                        return;
                    }
                    ((Transport2HttpHandler)o).SendHttpResponseAndPrepareForNext();
                }, this);
                return;
            }
            SendHttpResponseAndPrepareForNext();
        }

        public void CloseConnection()
        {
            if (Interlocked.CompareExchange(ref _disconnecting, 1, 0) == 0)
                Callback.StartDisconnect();
        }

        public bool HeadersSend
        {
            get { return _responseHeadersSend; }
        }

        public byte[] Buffer
        {
            get { return _buffer; }
        }

        public int ReceiveDataOffset
        {
            get { return StartBufferOffset + ReceiveBufferPos; }
        }

        public int ReceiveDataLength
        {
            get { return _receiveBufferFullness - StartBufferOffset - ReceiveBufferPos; }
        }

        public void ConsumeReceiveData(int count)
        {
            ReceiveBufferPos += count;
        }

        public void StartReceiveData()
        {
            _startedReceiveData = true;
            StartNextReceive();
        }

        public int SendDataOffset
        {
            get { return StartBufferOffset + ReceiveBufferSize; }
        }

        public int SendDataLength
        {
            get { return ReceiveBufferSize * 2 + Transport2HttpFactory.AdditionalSpace; }
        }

        public Task SendData(byte[] buffer, int offset, int length)
        {
            var tcs = new TaskCompletionSource<bool>();
            _tcsSend = tcs;
            Callback.StartSend(buffer, offset, length);
            return tcs.Task;
        }

        public bool CanUseDirectWrite()
        {
            return !_responseIsChunked && _responseHeadersSend;
        }
    }
}
