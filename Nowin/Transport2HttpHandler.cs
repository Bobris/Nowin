using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
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
        int _receiveBufferFullness;
        bool _waitingForRequest;
        bool _isHttp10;
        bool _isKeepAlive;
        bool _isMethodHead;
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
        int _acceptCounter;
        bool _afterReceiveContinueWithAccept;
        bool _clientClosedConnection;
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
        readonly object _receiveProcessingLock = new object();
        
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
        bool _receiving;
        int _disconnecting;
        bool _serverNameOverwrite;
        bool _dateOverwrite;
        bool _startedReceiveRequestData;

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

        internal void StartNextRequestDataReceive()
        {
            _startedReceiveRequestData = true;
            StartNextReceive();
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
            _isMethodHead = false;
            _requestMethod = ParseHttpMethod(buffer, ref pos);
            _requestScheme = _isSsl ? "https" : "http";
            string reqHost;
            ParseHttpPath(buffer, ref pos, out _requestPath, out _requestQueryString, out reqHost);
            ParseHttpProtocol(buffer, ref pos, out _requestProtocol);
            if (!SkipCrLf(buffer, ref pos)) throw new Exception("Request line does not end with CRLF");
            _isKeepAlive = !_isHttp10;
            ShouldSend100Continue = false;
            RequestContentLength = 0;
            RequestIsChunked = false;
            if (!ParseHttpHeaders(buffer, pos, posOfReqEnd))
                throw new Exception("Request headers cannot be parsed");
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
                    throw new InvalidDataException($"Wrong request content length: {value}");
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
            throw new InvalidDataException($"Unsupported request protocol: {reqProtocol}");
        }

        void ParseHttpPath(byte[] buffer, ref int pos, out string reqPath, out string reqQueryString, out string reqHost)
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
                                reqQueryString = ParsePath(buffer, start, p);
                                pos = p + 1;
                                return;
                            case 13:
                                reqQueryString = ParsePath(buffer, start, p);
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
            var cchacc = 0;
            var cchlen = 0;
            while (start < end)
            {
                var ch = buffer[start++];
                int cch;
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

                    if (v1 < 8) // Leave ASCII encoded
                    {
                        chs[used++] = '%';
                        chs[used++] = (char)buffer[start - 2];
                        chs[used++] = (char)buffer[start - 1];
                        continue;
                    }
                    cch = ((byte)v1 << 4) + v2;
                }
                else
                {
                    cch = ch;
                }
                if (cchlen == 0)
                {
                    if (cch < 194 || cch >= 245)
                    {
                        chs[used++] = (char)cch;
                    }
                    else if (cch < 224)
                    {
                        cchlen = 1;
                        cchacc = cch - 192;
                    }
                    else if (cch < 240)
                    {
                        cchlen = 2;
                        cchacc = cch - 224;
                    }
                    else
                    {
                        cchlen = 3;
                        cchacc = cch - 240;
                    }
                }
                else
                {
                    cchlen--;
                    cchacc = cchacc * 64 + (cch & 63);
                    if (cchlen == 0)
                    {
                        if (cchacc < 0x10000)
                        {
                            chs[used++] = (char)cchacc;
                        }
                        else
                        {
                            cchacc -= 0x10000;
                            chs[used++] = (char)(0xD800 + (cchacc >> 10));
                            chs[used++] = (char)(0xDC00 + (cchacc & 0x3ff));
                        }
                    }
                }
            }
            if (cchlen>0)
            {
                chs[used++] = '?';
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
                        _isMethodHead = true;
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

                if (b == ' ')
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
            if (finished && !_isMethodHead)
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
            if (_serverName!=null && !_serverNameOverwrite)
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
                var str = header.Value as string;
                if (str != null)
                {
                    HeaderAppend(header.Key);
                    HeaderAppend(": ");
                    HeaderAppend(str);
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
            Array.Copy(_buffer, StartBufferOffset + ReceiveBufferPos, _buffer, StartBufferOffset, ReceiveDataLength);
            _receiveBufferFullness -= ReceiveBufferPos;
            ReceiveBufferPos = 0;
        }

        void SendHttpResponseAndPrepareForNext()
        {
            var offset = _reqRespStream.ResponseStartOffset;
            var len = _reqRespStream.ResponseLocalPos;
            if (_isMethodHead)
                len = 0;
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

        void SendInternalServerError(string response = "500 Internal Server Error")
        {
            var tcs = _tcsSend;
            if (tcs != null)
            {
                tcs.Task.ContinueWith(_ => SendInternalServerError(response));
                return;
            }
            _isKeepAlive = false;
            _lastPacket = true;
            try
            {
                response = "HTTP/1.1 " + response + "\r\nServer: " + _serverName + "\r\nDate: " + _dateProvider.Value + "\r\nContent-Length: 0\r\n\r\n";
                var resbytes = Encoding.UTF8.GetBytes(response);
                Callback.StartSend(resbytes, 0, resbytes.Length);
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
                if (_responseHeaderPos > ReceiveBufferSize) throw new ArgumentException(
                    $"Response headers are longer({_responseHeaderPos}) than buffer({ReceiveBufferSize})");
                if (_isMethodHead)
                    len = 0;
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
                if (_isMethodHead)
                    len = 0;
                if (len == 0) return Task.Delay(0);
                WrapInChunk(_buffer, ref startOffset, ref len);
            }
            else
            {
                if (_isMethodHead)
                    return Task.Delay(0);
            }
            if (!_isMethodHead && _responseContentLength != ulong.MaxValue && _reqRespStream.ResponseLength > _responseContentLength)
            {
                CloseConnection();
                throw new ArgumentOutOfRangeException(nameof(len), "Cannot send more bytes than specified in Content-Length header");
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

        void StartNextReceive()
        {
            if (_clientClosedConnection) return;
            bool shouldStartRecv;
            lock (_receiveProcessingLock)
            {
                shouldStartRecv = ProcessReceive();
            }
            if (shouldStartRecv)
            {
                RealStartNextReceive();
            } 
        }

        public void Dispose()
        {
        }

        public ITransportLayerCallback Callback { set; private get; }

        public void PrepareAccept()
        {
            var t = _next.WaitForFinishingLastRequest();
            if (t == null || t.IsCompleted)
            {
                RealPrepareAccept();
            }
            else
            {
                t.ContinueWith((_, o) => ((Transport2HttpHandler)o).RealPrepareAccept(), this);
            }
        }

        void RealPrepareAccept()
        {
            lock (_receiveProcessingLock)
            {
                if (_receiving)
                {
                    _afterReceiveContinueWithAccept = true;
                    return;
                }
                _acceptCounter++;
                _disconnecting = 0;
                _clientClosedConnection = false;
            }
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
            _receiving = true;
            FinishReceive(buffer, offset, length);
        }

        public void SetRemoteCertificate(X509Certificate remoteCertificate)
        {
            _clientCertificate = remoteCertificate;
        }

        public void FinishReceive(byte[] buffer, int offset, int length)
        {
            TraceSources.CoreDebug.TraceInformation("======= Offset {0}, Length {1}", offset - StartBufferOffset, length);
            if (length == -1)
            {
                lock (_receiveProcessingLock)
                {
                    _receiving = false;
                }
                if (_afterReceiveContinueWithAccept)
                {
                    _afterReceiveContinueWithAccept = false;
                    RealPrepareAccept();
                    return;
                }
                _clientClosedConnection = true;
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
                    if (_startedReceiveRequestData)
                    {
                        _startedReceiveRequestData = false;
                        _reqRespStream.ConnectionClosed();
                    }
                }
                return;
            }
            Debug.Assert(StartBufferOffset + ReceiveBufferPos == offset || _waitingForRequest);
            Debug.Assert(_receiveBufferFullness == offset);
            TraceSources.CoreDebug.TraceInformation(Encoding.UTF8.GetString(buffer, offset, length));
            var startNextRecv = false;
            lock (_receiveProcessingLock)
            {
                _receiving = false;
                if (!_afterReceiveContinueWithAccept)
                {
                    _receiveBufferFullness = offset + length;
                    startNextRecv = ProcessReceive();
                }
            }
            if (_afterReceiveContinueWithAccept)
            {
                _afterReceiveContinueWithAccept = false;
                RealPrepareAccept();
                return;
            }
            if (startNextRecv)
            {
                RealStartNextReceive();
            }
        }

        void RealStartNextReceive()
        {
            var count = StartBufferOffset + ReceiveBufferSize - _receiveBufferFullness;
            Debug.Assert(count > 0);
            Callback.StartReceive(_buffer, _receiveBufferFullness, count);
        }

        /// <returns>true if new read request should be started</returns>
        bool ProcessReceive()
        {
            while (ReceiveDataLength > 0)
            {
                if (_waitingForRequest)
                {
                    var posOfReqEnd = FindRequestEnd(_buffer, StartBufferOffset + ReceiveBufferPos, _receiveBufferFullness);
                    if (posOfReqEnd < 0)
                    {
                        NormalizeReceiveBuffer();
                        var count = StartBufferOffset + ReceiveBufferSize - _receiveBufferFullness;
                        if (count == 0)
                        {
                            SendInternalServerError("400 Bad Request (Request Header too long)");
                            return false;
                        }
                        break;
                    }
                    else
                    {
                        _waitingForRequest = false;
                        var reenter = false;
                        var currentAcceptCounter = _acceptCounter;
                        try
                        {
                            var peqStartBufferOffset = StartBufferOffset + ReceiveBufferPos;
                            ReceiveBufferPos = posOfReqEnd - StartBufferOffset;
                            ParseRequest(_buffer, peqStartBufferOffset, posOfReqEnd);
                            var startRealReceive = false;
                            if (ReceiveDataLength == 0 && !_receiving)
                            {
                                _receiving = true;
                                ReceiveBufferPos = 0;
                                _receiveBufferFullness = StartBufferOffset;
                                startRealReceive = true;
                            }
                            Monitor.Exit(_receiveProcessingLock);
                            reenter = true;
                            if (startRealReceive)
                            {
                                RealStartNextReceive();
                            }
                            _next.HandleRequest();
                            reenter = false;
                            Monitor.Enter(_receiveProcessingLock); 
                            if (currentAcceptCounter != _acceptCounter)
                            {
                                // Delayed thread different connection already running this one needs to stop ASAP
                                return false;
                            } 
                        }
                        catch (Exception)
                        {
                            if (reenter)
                                Monitor.Enter(_receiveProcessingLock);
                            if (currentAcceptCounter != _acceptCounter)
                            {
                                // Delayed thread different connection already running this one needs to stop ASAP
                                return false;
                            }
                            ResponseStatusCode = 5000; // Means hardcoded 500 Internal Server Error
                            ResponseReasonPhase = null;
                            ResponseFinished();
                            return false;
                        }
                    }
                }
                else
                {
                    if (_startedReceiveData)
                    {
                        _startedReceiveData = false;
                        _next.FinishReceiveData(true);
                    }
                    else if (_startedReceiveRequestData)
                    {
                        _startedReceiveRequestData = false;
                        if (_reqRespStream.ProcessDataAndShouldReadMore())
                        {
                            _startedReceiveRequestData = true;
                        }
                    }
                    else
                    {
                        break;
                    }
                }
            }
            if (StartBufferOffset + ReceiveBufferPos == _receiveBufferFullness)
            {
                ReceiveBufferPos = 0;
                _receiveBufferFullness = StartBufferOffset;
            }
            if (ReceiveDataLength == 0 || _waitingForRequest)
            {
                if (!_receiving && !_clientClosedConnection)
                {
                    _receiving = true;
                    return true;
                }
            }
            return false;
        }

        public void FinishSend(Exception exception)
        {
            if (exception == null && !_clientClosedConnection)
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
                    if (exception == null) exception = new EndOfStreamException("Client closed connection");
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
                if (_isKeepAlive && !_clientClosedConnection)
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

        public CancellationToken CallCancelled => _cancellation.Token;

        public Stream ReqRespBody => _reqRespStream;

        public string RequestPath => _requestPath;

        public string RequestQueryString => _requestQueryString;

        public string RequestMethod => _requestMethod;

        public string RequestScheme => _requestScheme;

        public string RequestProtocol => _requestProtocol;

        public string RemoteIpAddress => _remoteIpAddress ?? (_remoteIpAddress = _remoteEndPoint.Address.ToString());

        public X509Certificate ClientCertificate => _clientCertificate;

        public string RemotePort => _remotePort ?? (_remotePort = _remoteEndPoint.Port.ToString(CultureInfo.InvariantCulture));

        public string LocalIpAddress => _localIpAddress ?? (_localIpAddress = _localEndPoint.Address.ToString());

        public string LocalPort => _localPort ?? (_localPort = _localEndPoint.Port.ToString(CultureInfo.InvariantCulture));

        public bool IsLocal
        {
            get
            {
                if (!_knownIsLocal) _isLocal = _ipIsLocalChecker.IsLocal(_remoteEndPoint.Address);
                return _isLocal;
            }
        }

        public bool IsWebSocketReq => _webSocketReqCondition == WebSocketReqConditions.AllSatisfied;

        public int ResponseStatusCode
        {
            get { return _statusCode; }
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
                var str = header.Value as string;
                if (str != null)
                {
                    HeaderAppend(header.Key);
                    HeaderAppend(": ");
                    HeaderAppend(str);
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
            if (_statusCode == 599)
            {
                _cancellation.Cancel();
                _isKeepAlive = false;
                CloseConnection();
                return;
            }
            if (_statusCode == 5000 /* uncatched exception */ || _cancellation.IsCancellationRequested)
            {
                _cancellation.Cancel();
                if (!_responseHeadersSend)
                {
                    SendInternalServerError();
                }
                else
                {
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

        public bool HeadersSend => _responseHeadersSend;

        public byte[] Buffer => _buffer;

        public int ReceiveDataOffset => StartBufferOffset + ReceiveBufferPos;

        public int ReceiveDataLength => _receiveBufferFullness - StartBufferOffset - ReceiveBufferPos;

        public void ConsumeReceiveData(int count)
        {
            ReceiveBufferPos += count;
        }

        public void StartReceiveData()
        {
            _startedReceiveData = true;
            StartNextReceive();
        }

        public int SendDataOffset => StartBufferOffset + ReceiveBufferSize;

        public int SendDataLength => ReceiveBufferSize * 2 + Transport2HttpFactory.AdditionalSpace;

        public int ReceiveBufferPos;
        X509Certificate _clientCertificate;

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
