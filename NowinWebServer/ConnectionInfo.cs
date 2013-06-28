using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace NowinWebServer
{
    class ConnectionInfo
    {
        public readonly int StartBufferOffset;
        public readonly int ReceiveBufferSize;
        public readonly int ResponseBodyBufferOffset;
        readonly int _constantsOffset;
        readonly byte[] _buffer;
        public int ReceiveBufferPos;
        int _receiveBufferFullness;
        public Socket Socket;
        bool _waitingForRequest;
        bool _isHttp10;
        bool _isKeepAlive;
        public bool ShouldSend100Continue;
        public ulong RequestContentLength;
        public bool RequestIsChunked;
        bool _responseHeadersSend;
        readonly IDictionary<string, object> _environment;
        readonly Dictionary<string, string[]> _reqHeaders;
        readonly Dictionary<string, string[]> _respHeaders;
        public readonly SocketAsyncEventArgs ReceiveSocketAsyncEventArgs;
        public readonly SocketAsyncEventArgs SendSocketAsyncEventArgs;
        readonly Socket _listenSocket;
        readonly Func<IDictionary<string, object>, Task> _app;
        readonly ResponseStream _responseStream;
        readonly RequestStream _requestStream;
        TaskCompletionSource<bool> _tcsSend;
        CancellationTokenSource _cancellation;
        int _responseHeaderPos;
        bool _lastPacket;
        bool _responseIsChunked;
        ulong _responseContentLength;

        public ConnectionInfo(int startBufferOffset, int receiveBufferSize, int constantsOffset, SocketAsyncEventArgs receiveSocketAsyncEventArgs, SocketAsyncEventArgs sendSocketAsyncEventArgs, Socket listenSocket, Func<IDictionary<string, object>, Task> app)
        {
            StartBufferOffset = startBufferOffset;
            ReceiveBufferSize = receiveBufferSize;
            ResponseBodyBufferOffset = StartBufferOffset + receiveBufferSize * 2 + 8;
            _constantsOffset = constantsOffset;
            ReceiveSocketAsyncEventArgs = receiveSocketAsyncEventArgs;
            SendSocketAsyncEventArgs = sendSocketAsyncEventArgs;
            _buffer = receiveSocketAsyncEventArgs.Buffer;
            _listenSocket = listenSocket;
            _app = app;
            _responseStream = new ResponseStream(this);
            _requestStream = new RequestStream(this);
            _environment = new Dictionary<string, object>();
            _reqHeaders = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            _respHeaders = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        }

        public int ReceiveBufferDataLength
        {
            get { return _receiveBufferFullness - StartBufferOffset - ReceiveBufferPos; }
        }

        void ParseRequest(byte[] buffer, int startBufferOffset, int posOfReqEnd)
        {
            posOfReqEnd -= 2;
            _environment.Clear();
            _reqHeaders.Clear();
            _respHeaders.Clear();
            _cancellation = new CancellationTokenSource();
            _responseIsChunked = false;
            _responseContentLength = ulong.MaxValue;
            _environment.Add(OwinKeys.Version, "1.0");
            _environment.Add(OwinKeys.CallCancelled, _cancellation.Token);
            _environment.Add(OwinKeys.RequestBody, _requestStream);
            _environment.Add(OwinKeys.RequestHeaders, _reqHeaders);
            _environment.Add(OwinKeys.RequestPathBase, "");
            _environment.Add(OwinKeys.ResponseHeaders, _respHeaders);
            var pos = startBufferOffset;
            _environment.Add(OwinKeys.RequestMethod, ParseHttpMethod(buffer, ref pos));
            string reqPath, reqQueryString, reqScheme = "http", reqHost;
            ParseHttpPath(buffer, ref pos, out reqPath, out reqQueryString, ref reqScheme, out reqHost);
            _environment.Add(OwinKeys.RequestPath, reqPath);
            _environment.Add(OwinKeys.RequestScheme, reqScheme);
            _environment.Add(OwinKeys.RequestQueryString, reqQueryString);
            string reqProtocol;
            ParseHttpProtocol(buffer, ref pos, out reqProtocol);
            _environment.Add(OwinKeys.RequestProtocol, reqProtocol);
            if (!SkipCrLf(buffer, ref pos)) throw new Exception("Request line does not end with CRLF");
            if (!ParseHttpHeaders(buffer, pos, posOfReqEnd)) throw new Exception("Request headers cannot be parsed");
            _environment.Add(OwinKeys.ResponseBody, _responseStream);
            if (_isHttp10)
            {
                _isKeepAlive = false;
                string[] connectionValues;
                if (_reqHeaders.TryGetValue("Connection", out connectionValues))
                {
                    if (connectionValues.Length == 1 && connectionValues[0].Equals("Keep-Alive", StringComparison.OrdinalIgnoreCase))
                    {
                        _isKeepAlive = true;
                    }
                }
            }
            else
            {
                _isKeepAlive = true;
                string[] connectionValues;
                if (_reqHeaders.TryGetValue("Connection", out connectionValues))
                {
                    if (connectionValues.Length == 1 && connectionValues[0].Equals("Close", StringComparison.OrdinalIgnoreCase))
                    {
                        _isKeepAlive = false;
                    }
                }
            }
            ShouldSend100Continue = false;
            string[] expectValues;
            if (_reqHeaders.TryGetValue("Expect", out expectValues))
            {
                if (expectValues.Length == 1 && expectValues[0].Equals("100-Continue", StringComparison.OrdinalIgnoreCase))
                {
                    ShouldSend100Continue = true;
                }
            }
            RequestContentLength = 0;
            string[] contentLengthValues;
            if (_reqHeaders.TryGetValue("Content-Length", out contentLengthValues))
            {
                if (contentLengthValues.Length != 1 || !ulong.TryParse(contentLengthValues[0], out RequestContentLength))
                {
                    throw new InvalidDataException("Wrong request content length");
                }
            }
            RequestIsChunked = false;
            string[] transferEncodingValues;
            if (_reqHeaders.TryGetValue("Transfer-Encoding", out transferEncodingValues))
            {
                if (transferEncodingValues.Length == 1 && transferEncodingValues[0] == "chunked")
                {
                    RequestIsChunked = true;
                    RequestContentLength = ulong.MaxValue;
                }
            }
        }

        bool ParseHttpHeaders(byte[] buffer, int pos, int posOfReqEnd)
        {
            var name = "";
            while (pos < posOfReqEnd)
            {
                int start;
                if (!IsSpaceOrTab(buffer[pos]))
                {
                    start = pos;
                    SkipTokenChars(buffer, ref pos);
                    if (buffer[pos] != ':') return false;
                    name = StringFromLatin1(buffer, start, pos);
                }
                pos++;
                SkipSpacesOrTabs(buffer, ref pos);
                start = pos;
                SkipToCR(buffer, ref pos);
                var value = StringFromLatin1(buffer, start, pos);
                string[] values;
                if (_reqHeaders.TryGetValue(name, out values))
                {
                    Array.Resize(ref values, values.Length + 1);
                    values[values.Length - 1] = value;
                    _reqHeaders[name] = values;
                }
                else
                {
                    _reqHeaders[name] = new[] { value };
                }
                SkipCrLf(buffer, ref pos);
            }
            return true;
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
            throw new NotImplementedException();
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
            var status = GetStatusFromEnvironment();
            if (status < 200)
            {
                status = 500;
            }
            string[] connectionValues;
            var headers = (IDictionary<string, string[]>)_environment[OwinKeys.ResponseHeaders] ?? _respHeaders;
            object responsePhase;
            if (_environment.TryGetValue(OwinKeys.ResponseReasonPhrase, out responsePhase))
            {
                if (!(responsePhase is String))
                {
                    status = 500;
                    responsePhase = null;
                }
            }
            if (headers.TryGetValue("Connection", out connectionValues))
            {
                headers.Remove("Connection");
                if (connectionValues.Length != 1)
                {
                    status = 500;
                }
                else
                {
                    var v = connectionValues[0];
                    if (v.Equals("Close", StringComparison.InvariantCultureIgnoreCase))
                        _isKeepAlive = false;
                    else if (v.Equals("Keep-alive", StringComparison.InvariantCultureIgnoreCase))
                        _isKeepAlive = true;
                }
            }
            string[] contentLengthValues;
            string contentLength = null;
            if (headers.TryGetValue("Content-Length", out contentLengthValues))
            {
                headers.Remove("Content-Length");
                if (contentLengthValues.Length != 1)
                {
                    status = 500;
                }
                else
                {
                    ulong temp;
                    if (!ulong.TryParse(contentLengthValues[0], out temp))
                    {
                        status = 500;
                    }
                    else
                    {
                        contentLength = contentLengthValues[0];
                        _responseContentLength = temp;
                    }
                }
            }
            if (finished)
            {
                if (_responseContentLength != ulong.MaxValue && _responseContentLength != (ulong)_responseStream.Length)
                {
                    status = 500;
                }
                contentLength = _responseStream.Length.ToString(CultureInfo.InvariantCulture);
            }
            _responseHeaderPos = 0;
            HeaderAppend("HTTP/1.1 ");
            HeaderAppend(status.ToString(CultureInfo.InvariantCulture));
            if (responsePhase != null)
            {
                HeaderAppend(" ");
                HeaderAppend((string)responsePhase);
            }
            HeaderAppendCrLf();
            if (status == 500)
            {
                contentLength = "0";
                _isKeepAlive = false;
            }
            if (contentLength != null)
            {
                HeaderAppend("Content-Length: ");
                HeaderAppend(contentLength);
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
            headers.Remove("Transfer-Encoding");
            if (_isHttp10 && _isKeepAlive)
            {
                HeaderAppend("Connection: keep-alive\r\n");
            }
            if (!_isKeepAlive)
            {
                HeaderAppend("Connection: close\r\n");
            }
            foreach (var header in headers)
            {
                foreach (var value in header.Value)
                {
                    HeaderAppend(header.Key);
                    HeaderAppend(": ");
                    HeaderAppend(value);
                    HeaderAppendCrLf();
                }
            }
            HeaderAppendCrLf();
        }

        int GetStatusFromEnvironment()
        {
            object value;
            if (!_environment.TryGetValue(OwinKeys.ResponseStatusCode, out value))
                return 200;
            return (int)value;
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

        public bool ProcessReceive()
        {
            if (ReceiveSocketAsyncEventArgs.BytesTransferred > 0 && ReceiveSocketAsyncEventArgs.SocketError == SocketError.Success)
            {
                _receiveBufferFullness = ReceiveSocketAsyncEventArgs.Offset + ReceiveSocketAsyncEventArgs.BytesTransferred;
                if (_waitingForRequest)
                {
                    NormalizeReceiveBuffer();
                    var posOfReqEnd = FindRequestEnd(ReceiveSocketAsyncEventArgs.Buffer, StartBufferOffset, _receiveBufferFullness);
                    if (posOfReqEnd < 0)
                    {
                        var count = StartBufferOffset + ReceiveBufferSize - _receiveBufferFullness;
                        if (count > 0)
                        {
                            ReceiveSocketAsyncEventArgs.SetBuffer(_receiveBufferFullness, count);
                            var willRaiseEvent = Socket.ReceiveAsync(ReceiveSocketAsyncEventArgs);
                            return !willRaiseEvent;
                        }
                    }
                    _waitingForRequest = false;
                    Task task;
                    try
                    {
                        ReceiveBufferPos = posOfReqEnd - StartBufferOffset;
                        ParseRequest(ReceiveSocketAsyncEventArgs.Buffer, StartBufferOffset, posOfReqEnd);
                        task = _app(_environment);
                    }
                    catch (Exception ex)
                    {
                        var tcs = new TaskCompletionSource<bool>();
                        tcs.SetException(ex);
                        task = tcs.Task;
                    }
                    if (task.IsCompleted)
                    {
                        AppFinished(task);
                        return false;
                    }
                    task.ContinueWith(AppFinished, this, TaskContinuationOptions.ExecuteSynchronously);
                }
                else
                {
                    if (_requestStream.ProcessDataAndShouldReadMore())
                    {
                        NormalizeReceiveBuffer();
                        var count = StartBufferOffset + ReceiveBufferSize - _receiveBufferFullness;
                        ReceiveSocketAsyncEventArgs.SetBuffer(_receiveBufferFullness, count);
                        var willRaiseEvent = Socket.ReceiveAsync(ReceiveSocketAsyncEventArgs);
                        return !willRaiseEvent;
                    }
                }
            }
            else
            {
                CloseClientSocket(ReceiveSocketAsyncEventArgs);
            }
            return false;
        }

        void NormalizeReceiveBuffer()
        {
            if (ReceiveBufferPos == 0) return;
            Array.Copy(_buffer, StartBufferOffset + ReceiveBufferPos, _buffer, StartBufferOffset, _receiveBufferFullness - StartBufferOffset - ReceiveBufferPos);
            _receiveBufferFullness -= ReceiveBufferPos;
            ReceiveBufferPos = 0;
        }

        static void AppFinished(Task task, object state)
        {
            ((ConnectionInfo)state).AppFinished(task);
        }

        void AppFinished(Task task)
        {
            if (task.IsFaulted || task.IsCanceled)
            {
                _cancellation.Cancel();
                if (!_responseHeadersSend)
                    SendInternalServerError();
                else
                {
                    _isKeepAlive = false;
                    CloseClientSocket(SendSocketAsyncEventArgs);
                }
                return;
            }
            if (_requestStream.Position != _requestStream.Length)
            {
                DrainRequestStreamAsync().ContinueWith((t, o) =>
                    {
                        if (t.IsFaulted || t.IsCanceled)
                        {
                            ((ConnectionInfo)o).AppFinished(t);
                            return;
                        }
                        ((ConnectionInfo)o).AppFinishedSuccessfully();
                    }, this);
                return;
            }
            AppFinishedSuccessfully();
        }

        void AppFinishedSuccessfully()
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
                OptimallyMergeTwoRegions(_buffer, StartBufferOffset + ReceiveBufferSize, _responseHeaderPos, ref offset, ref len);
                _responseHeadersSend = true;
            }
            else
            {
                if (_responseContentLength != ulong.MaxValue && (ulong)_responseStream.Position != _responseContentLength)
                {
                    CloseClientSocket(SendSocketAsyncEventArgs);
                    return;
                }
                if (_responseIsChunked)
                {
                    WrapInChunk(_buffer, ref offset, ref len);
                    AppendZeroChunk(_buffer, offset, ref len);
                }
            }
            SendSocketAsyncEventArgs.SetBuffer(offset, len);
            _lastPacket = true;
            var willRaiseEvent = Socket.SendAsync(SendSocketAsyncEventArgs);
            if (!willRaiseEvent)
            {
                ProcessSend();
            }
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
                var len = await _requestStream.ReadAsync(_buffer, StartBufferOffset + ReceiveBufferSize, ReceiveBufferSize);
                if (len < ReceiveBufferSize) return;
            }
        }

        void SendInternalServerError()
        {
            var status500InternalServerError = Server.Status500InternalServerError;
            Array.Copy(status500InternalServerError, 0, ReceiveSocketAsyncEventArgs.Buffer,
                       StartBufferOffset + ReceiveBufferSize, status500InternalServerError.Length);
            ReceiveSocketAsyncEventArgs.SetBuffer(StartBufferOffset + ReceiveBufferSize, status500InternalServerError.Length);
            _isKeepAlive = false;
            _lastPacket = true;
            var willRaiseEvent = Socket.SendAsync(ReceiveSocketAsyncEventArgs);
            if (!willRaiseEvent)
            {
                ProcessSend();
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

        public void ProcessSend()
        {
            if (SendSocketAsyncEventArgs.SocketError == SocketError.Success)
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
                    tcs.SetException(new IOException());
                }
                _isKeepAlive = false;
            }
            if (_lastPacket)
            {
                _lastPacket = false;
                if (_isKeepAlive)
                {
                    ResetForNextRequest();
                    NormalizeReceiveBuffer();
                    ReceiveSocketAsyncEventArgs.SetBuffer(_receiveBufferFullness, StartBufferOffset + ReceiveBufferSize - _receiveBufferFullness);
                    var willRaiseEvent = Socket.ReceiveAsync(ReceiveSocketAsyncEventArgs);
                    if (!willRaiseEvent)
                    {
                        while (ProcessReceive())
                        {
                        }
                    }
                }
                else
                {
                    CloseClientSocket(SendSocketAsyncEventArgs);
                }
            }
        }

        void ResetForNextRequest()
        {
            _waitingForRequest = true;
            _responseHeadersSend = false;
            _lastPacket = false;
            _requestStream.Reset();
            _responseStream.Reset();
        }

        void CloseClientSocket(SocketAsyncEventArgs e)
        {
            var token = (ConnectionInfo)e.UserToken;
            bool willRaiseEvent;
            try
            {
                willRaiseEvent = token.Socket.DisconnectAsync(e);
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            if (!willRaiseEvent)
            {
                ProcessDisconnect(e);
            }
        }

        public void ProcessDisconnect(SocketAsyncEventArgs e)
        {
            StartAccept();
        }

        public void ProcessAccept()
        {
            Socket = ReceiveSocketAsyncEventArgs.AcceptSocket;
            ReceiveSocketAsyncEventArgs.AcceptSocket = null;
            ResetForNextRequest();
            ReceiveBufferPos = 0;
            while (ProcessReceive())
            {
            }
        }

        public void StartAccept()
        {
            ReceiveSocketAsyncEventArgs.SetBuffer(StartBufferOffset, ReceiveBufferSize);
            bool willRaiseEvent;
            try
            {
                willRaiseEvent = _listenSocket.AcceptAsync(ReceiveSocketAsyncEventArgs);
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            if (!willRaiseEvent)
            {
                ProcessAccept();
            }
        }

        public Task WriteAsync(int startOffset, int len)
        {
            if (!_responseHeadersSend)
            {
                FillResponse(false);
                if (_responseHeaderPos > ReceiveBufferSize) throw new ArgumentException(string.Format("Response headers are longer({0}) than buffer({1})", _responseHeaderPos, ReceiveBufferSize));
                if (_responseIsChunked)
                {
                    WrapInChunk(_buffer, ref startOffset, ref len);
                }
                OptimallyMergeTwoRegions(_buffer, StartBufferOffset + ReceiveBufferSize, _responseHeaderPos, ref startOffset, ref len);
                _responseHeadersSend = true;
            }
            else if (_responseIsChunked)
            {
                WrapInChunk(_buffer, ref startOffset, ref len);
            }
            if (_responseContentLength != ulong.MaxValue && (ulong)_responseStream.Position > _responseContentLength)
            {
                CloseClientSocket(SendSocketAsyncEventArgs);
                throw new ArgumentOutOfRangeException("len", "Cannot send more bytes than specified in Content-Length header");
            }
            SendSocketAsyncEventArgs.SetBuffer(startOffset, len);
            _tcsSend = new TaskCompletionSource<bool>();
            var willRaiseEvent = Socket.SendAsync(SendSocketAsyncEventArgs);
            if (!willRaiseEvent)
            {
                ProcessSend();
            }
            return _tcsSend.Task;
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
            SendSocketAsyncEventArgs.SetBuffer(_constantsOffset, Server.Status100Continue.Length);
            var willRaiseEvent = Socket.SendAsync(SendSocketAsyncEventArgs);
            if (!willRaiseEvent)
            {
                ProcessSend();
            }
        }

        public void StartNextReceive()
        {
            NormalizeReceiveBuffer();
            var count = StartBufferOffset + ReceiveBufferSize - _receiveBufferFullness;
            if (count > 0)
            {
                ReceiveSocketAsyncEventArgs.SetBuffer(_receiveBufferFullness, count);
                var willRaiseEvent = Socket.ReceiveAsync(ReceiveSocketAsyncEventArgs);
                if (!willRaiseEvent)
                {
                    while (ProcessReceive())
                    {
                    }
                }
            }
        }
    }
}