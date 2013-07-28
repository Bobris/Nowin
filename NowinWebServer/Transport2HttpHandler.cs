using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace NowinWebServer
{
    class Transport2HttpHandler : ITransportLayerHandler, IHttpLayerCallback
    {
        readonly IHttpLayerHandler _next;
        public readonly int StartBufferOffset;
        public readonly int ReceiveBufferSize;
        public readonly int ResponseBodyBufferOffset;
        readonly int _constantsOffset;
        internal readonly byte[] Buffer;
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
        readonly IIpIsLocalChecker _ipIsLocalChecker;
        readonly ResponseStream _responseStream;
        readonly RequestStream _requestStream;
        TaskCompletionSource<bool> _tcsSend;
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

        public Transport2HttpHandler(IHttpLayerHandler next, bool isSsl, IIpIsLocalChecker ipIsLocalChecker, byte[] buffer, int startBufferOffset, int receiveBufferSize, int constantsOffset)
        {
            _next = next;
            StartBufferOffset = startBufferOffset;
            ReceiveBufferSize = receiveBufferSize;
            ResponseBodyBufferOffset = StartBufferOffset + ReceiveBufferSize * 2 + 8;
            _constantsOffset = constantsOffset;
            Buffer = buffer;
            _isSsl = isSsl;
            _ipIsLocalChecker = ipIsLocalChecker;
            _responseStream = new ResponseStream(this);
            _requestStream = new RequestStream(this);
            _next.Callback = this;
        }

        public int ReceiveBufferDataLength
        {
            get { return _receiveBufferFullness - StartBufferOffset - ReceiveBufferPos; }
        }

        void ParseRequest(byte[] buffer, int startBufferOffset, int posOfReqEnd)
        {
            _next.PrepareForRequest();
            posOfReqEnd -= 2;
            _responseHeaders.Clear();
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

        static void ParseHttpPath(byte[] buffer, ref int pos, out string reqPath, out string reqQueryString, ref string reqScheme, out string reqHost)
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

        static string ParsePath(byte[] buffer, int start, int end)
        {
            var chs = new char[end - start];
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

        static string ParseHttpMethod(byte[] buffer, ref int pos)
        {
            var p = pos;
            var start = p;
            switch (buffer[p])
            {
                case (byte)'G':
                    if (buffer[p + 1] == 'E' && buffer[p + 2] == 'T' && buffer[p + 3] == ' ')
                    {
                        pos = p + 4;
                        return "GET";
                    }
                    break;
                case (byte)'P':
                    if (buffer[p + 1] == 'O' && buffer[p + 2] == 'S' && buffer[p + 3] == 'T' && buffer[p + 4] == ' ')
                    {
                        pos = p + 5;
                        return "POST";
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
            }
            return StringFromLatin1(buffer, start, p);
        }

        static string StringFromLatin1(byte[] buffer, int start, int end)
        {
            var chs = new char[end - start];
            for (var i = 0; i < chs.Length; i++)
            {
                chs[i] = (char)buffer[start + i];
            }
            return new string(chs);
        }

        void FillResponse(bool finished)
        {
            _next.PrepareResponseHeaders();
            var status = _statusCode;
            if (status < 200)
            {
                status = 500;
            }
            if (finished)
            {
                if (_responseContentLength != ulong.MaxValue && _responseContentLength != (ulong)_responseStream.Length)
                {
                    status = 500;
                }
                _responseContentLength = (ulong)_responseStream.Length;
            }
            _responseHeaderPos = 0;
            HeaderAppend("HTTP/1.1 ");
            HeaderAppend(status.ToString(CultureInfo.InvariantCulture));
            if (_reasonPhase != null)
            {
                HeaderAppend(" ");
                HeaderAppend(_reasonPhase);
            }
            HeaderAppendCrLf();
            if (status == 500)
            {
                _responseContentLength = 0;
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

        void HeaderAppendCrLf()
        {
            if (_responseHeaderPos > ReceiveBufferSize - 2)
            {
                _responseHeaderPos += 2;
                return;
            }
            var i = StartBufferOffset + ReceiveBufferSize + _responseHeaderPos;
            Buffer[i] = 13;
            Buffer[i + 1] = 10;
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
                Buffer[j++] = (byte)ch;
            }
            _responseHeaderPos += text.Length;
        }

        void NormalizeReceiveBuffer()
        {
            if (ReceiveBufferPos == 0) return;
            Array.Copy(Buffer, StartBufferOffset + ReceiveBufferPos, Buffer, StartBufferOffset, _receiveBufferFullness - StartBufferOffset - ReceiveBufferPos);
            _receiveBufferFullness -= ReceiveBufferPos;
            ReceiveBufferPos = 0;
        }

        void SendHttpResponseAndPrepareForNext()
        {
            var offset = _responseStream.StartOffset;
            var len = _responseStream.LocalPos;
            if (!_responseHeadersSend)
            {
                FillResponse(true);
                if (_responseHeaderPos > ReceiveBufferSize)
                {
                    SendInternalServerError();
                    return;
                }
                OptimallyMergeTwoRegions(Buffer, StartBufferOffset + ReceiveBufferSize, _responseHeaderPos, ref offset, ref len);
                _responseHeadersSend = true;
            }
            else
            {
                if (_responseContentLength != ulong.MaxValue && (ulong)_responseStream.Position != _responseContentLength)
                {
                    Callback.StartDisconnect();
                    return;
                }
                if (_responseIsChunked)
                {
                    WrapInChunk(Buffer, ref offset, ref len);
                    AppendZeroChunk(Buffer, offset, ref len);
                }
            }
            _lastPacket = true;
            Callback.StartSend(Buffer, offset, len);
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
                var len = await _requestStream.ReadAsync(Buffer, StartBufferOffset + ReceiveBufferSize, ReceiveBufferSize);
                if (len < ReceiveBufferSize) return;
            }
        }

        void SendInternalServerError()
        {
            _isKeepAlive = false;
            _lastPacket = true;
            try
            {
                Callback.StartSend(Server.Status500InternalServerError, 0, Server.Status500InternalServerError.Length);
            }
            catch (Exception)
            {
                Callback.StartDisconnect();
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
            _requestStream.Reset();
            _responseStream.Reset();
        }

        public Task WriteAsync(byte[] buffer, int startOffset, int len)
        {
            if (!_responseHeadersSend)
            {
                if (Buffer != buffer) throw new InvalidOperationException();
                FillResponse(false);
                if (_responseHeaderPos > ReceiveBufferSize) throw new ArgumentException(string.Format("Response headers are longer({0}) than buffer({1})", _responseHeaderPos, ReceiveBufferSize));
                if (_responseIsChunked && len!=0)
                {
                    WrapInChunk(Buffer, ref startOffset, ref len);
                }
                OptimallyMergeTwoRegions(Buffer, StartBufferOffset + ReceiveBufferSize, _responseHeaderPos, ref startOffset, ref len);
                _responseHeadersSend = true;
            }
            else if (_responseIsChunked)
            {
                if (Buffer != buffer) throw new InvalidOperationException();
                if (len == 0) return Task.Delay(0);
                WrapInChunk(Buffer, ref startOffset, ref len);
            }
            if (_responseContentLength != ulong.MaxValue && (ulong)_responseStream.Position > _responseContentLength)
            {
                Callback.StartDisconnect();
                throw new ArgumentOutOfRangeException("len", "Cannot send more bytes than specified in Content-Length header");
            }
            var tcs = new TaskCompletionSource<bool>();
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
            Callback.StartSend(Buffer, _constantsOffset, Server.Status100Continue.Length);
        }

        public void StartNextReceive()
        {
            NormalizeReceiveBuffer();
            var count = StartBufferOffset + ReceiveBufferSize - _receiveBufferFullness;
            if (count > 0)
            {
                Callback.StartReceive(Buffer, _receiveBufferFullness, count);
            }
        }

        public void Dispose()
        {
        }

        public ITransportLayerCallback Callback { set; private get; }

        public void PrepareAccept()
        {
            Callback.StartAccept(Buffer, StartBufferOffset, ReceiveBufferSize);
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
                    Callback.StartDisconnect();
                }
                else
                {
                    _cancellation.Cancel();
                    _requestStream.ConnectionClosed();
                }
                return;
            }
            //Console.WriteLine("======= Offset {0}, Length {1}", offset - StartBufferOffset, length);
            //Console.WriteLine(Encoding.UTF8.GetString(buffer, offset, length));
            _receiveBufferFullness = offset + length;
            if (_waitingForRequest)
            {
                NormalizeReceiveBuffer();
                var posOfReqEnd = FindRequestEnd(Buffer, StartBufferOffset, _receiveBufferFullness);
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
                    ParseRequest(Buffer, StartBufferOffset, posOfReqEnd);
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
                if (_requestStream.ProcessDataAndShouldReadMore())
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
            }
            else
            {
                var tcs = _tcsSend;
                _tcsSend = null;
                if (tcs != null)
                {
                    tcs.SetException(exception);
                }
                _isKeepAlive = false;
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
                    Callback.StartDisconnect();
                }
            }
        }

        public CancellationToken CallCancelled
        {
            get { return _cancellation.Token; }
        }

        public bool ResponseWriteIsFlushAndFlushIsNoOp
        {
            set { _responseStream.SetResponseWriteIsFlushAndFlushIsNoOp(value); }
        }

        public Stream ResponseBody
        {
            get { return _responseStream; }
        }

        public Stream RequestBody
        {
            get { return _requestStream; }
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
            _responseHeaders.Add(new KeyValuePair<string, object>(name, value));
        }

        public void AddResponseHeader(string name, IEnumerable<string> values)
        {
            _responseHeaders.Add(new KeyValuePair<string, object>(name, values));
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
                    Callback.StartDisconnect();
                }
                return;
            }
            if (_requestStream.Position != _requestStream.Length)
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

        public bool CanUseDirectWrite()
        {
            return !_responseIsChunked && _responseHeadersSend;
        }
    }
}
