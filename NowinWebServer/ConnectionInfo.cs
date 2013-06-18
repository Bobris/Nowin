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
        public readonly int ConstantsOffset;
        readonly byte[] _buffer;
        public int ReceiveBufferPos;
        public int ReceiveBufferFullness;
        public Socket Socket;
        public bool WaitingForRequest;
        public bool IsHttp10;
        public bool IsKeepAlive;
        public bool ShouldSend100Continue;
        public ulong RequestContentLength;
        public bool ResponseHeadersSend;
        public readonly IDictionary<string, object> Environment;
        readonly Dictionary<string, string[]> _reqHeaders;
        readonly Dictionary<string, string[]> _respHeaders;
        public readonly SocketAsyncEventArgs ReceiveSocketAsyncEventArgs;
        public readonly SocketAsyncEventArgs SendSocketAsyncEventArgs;
        readonly Socket _listenSocket;
        readonly Func<IDictionary<string, object>, Task> _app;
        public readonly ResponseStream ResponseStream;
        public readonly RequestStream RequestStream;
        TaskCompletionSource<bool> _tcsSend;
        CancellationTokenSource _cancellation;
        int _responseHeaderPos;
        public bool LastPacket;
        bool _sending100Continue;

        public ConnectionInfo(int startBufferOffset, int receiveBufferSize, int constantsOffset, SocketAsyncEventArgs receiveSocketAsyncEventArgs, SocketAsyncEventArgs sendSocketAsyncEventArgs, Socket listenSocket, Func<IDictionary<string, object>, Task> app)
        {
            StartBufferOffset = startBufferOffset;
            ReceiveBufferSize = receiveBufferSize;
            ConstantsOffset = constantsOffset;
            ReceiveSocketAsyncEventArgs = receiveSocketAsyncEventArgs;
            SendSocketAsyncEventArgs = sendSocketAsyncEventArgs;
            _buffer = receiveSocketAsyncEventArgs.Buffer;
            _listenSocket = listenSocket;
            _app = app;
            ResponseStream = new ResponseStream(this);
            RequestStream = new RequestStream(this);
            Environment = new Dictionary<string, object>();
            _reqHeaders = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            _respHeaders = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            _sending100Continue = false;
        }

        public int ReceiveBufferDataLength
        {
            get { return ReceiveBufferFullness - StartBufferOffset - ReceiveBufferPos; }
        }

        public void ParseRequest(byte[] buffer, int startBufferOffset, int posOfReqEnd)
        {
            posOfReqEnd -= 2;
            Environment.Clear();
            _reqHeaders.Clear();
            _respHeaders.Clear();
            _cancellation = new CancellationTokenSource();
            _sending100Continue = false;
            Environment.Add(OwinKeys.Version, "1.0");
            Environment.Add(OwinKeys.CallCancelled, _cancellation.Token);
            Environment.Add(OwinKeys.RequestBody, RequestStream);
            Environment.Add(OwinKeys.RequestHeaders, _reqHeaders);
            Environment.Add(OwinKeys.RequestPathBase, "");
            Environment.Add(OwinKeys.ResponseHeaders, _respHeaders);
            var pos = startBufferOffset;
            Environment.Add(OwinKeys.RequestMethod, ParseHttpMethod(buffer, ref pos));
            string reqPath, reqQueryString, reqScheme = "http", reqHost;
            ParseHttpPath(buffer, ref pos, out reqPath, out reqQueryString, ref reqScheme, out reqHost);
            Environment.Add(OwinKeys.RequestPath, reqPath);
            Environment.Add(OwinKeys.RequestScheme, reqScheme);
            Environment.Add(OwinKeys.RequestQueryString, reqQueryString);
            string reqProtocol;
            ParseHttpProtocol(buffer, ref pos, out reqProtocol);
            Environment.Add(OwinKeys.RequestProtocol, reqProtocol);
            if (!SkipCrLf(buffer, ref pos)) throw new Exception("Request line does not end with CRLF");
            if (!ParseHttpHeaders(buffer, pos, posOfReqEnd)) throw new Exception("Request headers cannot be parsed");
            Environment.Add(OwinKeys.ResponseBody, ResponseStream);
            if (IsHttp10)
            {
                IsKeepAlive = false;
                string[] connectionValues;
                if (_reqHeaders.TryGetValue("Connection", out connectionValues))
                {
                    if (connectionValues.Length == 1 && connectionValues[0].Equals("Keep-Alive", StringComparison.OrdinalIgnoreCase))
                    {
                        IsKeepAlive = true;
                    }
                }
            }
            else
            {
                IsKeepAlive = true;
                string[] connectionValues;
                if (_reqHeaders.TryGetValue("Connection", out connectionValues))
                {
                    if (connectionValues.Length == 1 && connectionValues[0].Equals("Close", StringComparison.OrdinalIgnoreCase))
                    {
                        IsKeepAlive = false;
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
                            IsHttp10 = true;
                            return;
                        }
                    case (byte)'1':
                        {
                            reqProtocol = "HTTP/1.1";
                            pos += 8;
                            IsHttp10 = false;
                            return;
                        }
                }
                pos += 8;
                throw new NotImplementedException();
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

        static int ParseHexChar(byte ch)
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
            _responseHeaderPos = 0;
            HeaderAppend(IsHttp10 ? "HTTP/1.0 " : "HTTP/1.1 ");
            HeaderAppend(status.ToString(CultureInfo.InvariantCulture));
            HeaderAppendCrLf();
            var headers = (IDictionary<string, string[]>)Environment[OwinKeys.ResponseHeaders];
            if (finished)
            {
                HeaderAppend("Content-Length: ");
                HeaderAppend(ResponseStream.Length.ToString(CultureInfo.InvariantCulture));
                HeaderAppendCrLf();
            }
            else
            {
                string[] contentLengthValues;
                if (headers.TryGetValue("Content-Length", out contentLengthValues) && contentLengthValues.Length == 1)
                {
                    HeaderAppend("Content-Length: ");
                    HeaderAppend(contentLengthValues[0]);
                    HeaderAppendCrLf();
                }
                else
                {
                    IsKeepAlive = false;
                    // TODO: chunked for Http1.1
                }
            }
            headers.Remove("Content-Length");
            if (IsHttp10 && IsKeepAlive)
            {
                HeaderAppend("Connection: Keep-Alive\n\r");
            }
            if (!IsHttp10 && !IsKeepAlive)
            {
                HeaderAppend("Connection: Close\n\r");
            }
            foreach (var header in headers)
            {
                switch (header.Value.Length)
                {
                    case 0:
                        continue;
                    case 1:
                        HeaderAppend(header.Key);
                        HeaderAppend(": ");
                        HeaderAppend(header.Value[0]);
                        HeaderAppendCrLf();
                        continue;
                }
                HeaderAppend(header.Key);
                HeaderAppend(": ");
                HeaderAppend(header.Value[0]);
                HeaderAppendCrLf();
                for (int i = 1; i < header.Value.Length; i++)
                {
                    HeaderAppend(" ");
                    HeaderAppend(header.Value[i]);
                    HeaderAppendCrLf();
                }
            }
            HeaderAppendCrLf();
        }

        int GetStatusFromEnvironment()
        {
            object value;
            if (!Environment.TryGetValue(OwinKeys.ResponseStatusCode, out value))
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
                ReceiveBufferFullness = ReceiveSocketAsyncEventArgs.Offset + ReceiveSocketAsyncEventArgs.BytesTransferred;
                if (WaitingForRequest)
                {
                    NormalizeReceiveBuffer();
                    var posOfReqEnd = FindRequestEnd(ReceiveSocketAsyncEventArgs.Buffer, StartBufferOffset, ReceiveBufferFullness);
                    if (posOfReqEnd < 0)
                    {
                        var count = StartBufferOffset + ReceiveBufferSize - ReceiveBufferFullness;
                        if (count > 0)
                        {
                            ReceiveSocketAsyncEventArgs.SetBuffer(ReceiveBufferFullness, count);
                            var willRaiseEvent = Socket.ReceiveAsync(ReceiveSocketAsyncEventArgs);
                            return !willRaiseEvent;
                        }
                    }
                    WaitingForRequest = false;
                    Task task;
                    try
                    {
                        ReceiveBufferPos = posOfReqEnd - StartBufferOffset;
                        ParseRequest(ReceiveSocketAsyncEventArgs.Buffer, StartBufferOffset, posOfReqEnd);
                        task = _app(Environment);
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
                    if (RequestStream.ProcessDataAndShouldReadMore())
                    {
                        NormalizeReceiveBuffer();
                        var count = StartBufferOffset + ReceiveBufferSize - ReceiveBufferFullness;
                        ReceiveSocketAsyncEventArgs.SetBuffer(ReceiveBufferFullness, count);
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
            Array.Copy(_buffer, StartBufferOffset + ReceiveBufferPos, _buffer, StartBufferOffset, ReceiveBufferFullness - StartBufferOffset - ReceiveBufferPos);
            ReceiveBufferFullness -= ReceiveBufferPos;
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
                if (!ResponseHeadersSend)
                    SendInternalServerError();
                else
                {
                    IsKeepAlive = false;
                    CloseClientSocket(SendSocketAsyncEventArgs);
                }
                return;
            }
            var offset = ResponseStream.StartOffset;
            var len = ResponseStream.LocalPos;
            if (!ResponseHeadersSend)
            {
                FillResponse(true);
                if (_responseHeaderPos > ReceiveBufferSize)
                {
                    SendInternalServerError();
                    return;
                }
                OptimallyMergeTwoRegions(_buffer, StartBufferOffset + ReceiveBufferSize, _responseHeaderPos, ref offset, ref len);
                ResponseHeadersSend = true;
            }
            SendSocketAsyncEventArgs.SetBuffer(offset, len);
            LastPacket = true;
            var willRaiseEvent = Socket.SendAsync(SendSocketAsyncEventArgs);
            if (!willRaiseEvent)
            {
                ProcessSend();
            }
        }

        void SendInternalServerError()
        {
            var status500InternalServerError = IsHttp10
                                                   ? Server.Status500InternalServerError10
                                                   : Server.Status500InternalServerError11;
            Array.Copy(status500InternalServerError, 0, ReceiveSocketAsyncEventArgs.Buffer,
                       StartBufferOffset + ReceiveBufferSize, status500InternalServerError.Length);
            ReceiveSocketAsyncEventArgs.SetBuffer(StartBufferOffset + ReceiveBufferSize, status500InternalServerError.Length);
            IsKeepAlive = false;
            LastPacket = true;
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
            _sending100Continue = false;
            if (SendSocketAsyncEventArgs.SocketError == SocketError.Success)
            {
                if (_tcsSend != null)
                {
                    _tcsSend.SetResult(true);
                    _tcsSend = null;
                }
            }
            else
            {
                if (_tcsSend != null)
                {
                    _tcsSend.SetException(new IOException());
                    _tcsSend = null;
                }
                IsKeepAlive = false;
            }
            if (LastPacket)
            {
                LastPacket = false;
                if (IsKeepAlive)
                {
                    ResetForNextRequest();
                    NormalizeReceiveBuffer();
                    ReceiveSocketAsyncEventArgs.SetBuffer(ReceiveBufferFullness, StartBufferOffset + ReceiveBufferSize - ReceiveBufferFullness);
                    var willRaiseEvent = Socket.ReceiveAsync(ReceiveSocketAsyncEventArgs);
                    if (!willRaiseEvent)
                    {
                        while (ProcessReceive()) ;
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
            WaitingForRequest = true;
            ResponseHeadersSend = false;
            LastPacket = false;
            ResponseStream.Reset();
        }

        void CloseClientSocket(SocketAsyncEventArgs e)
        {
            var token = (ConnectionInfo)e.UserToken;
            var willRaiseEvent = token.Socket.DisconnectAsync(e);
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
            LastPacket = false;
            while (ProcessReceive()) ;
        }

        public void StartAccept()
        {
            ReceiveSocketAsyncEventArgs.SetBuffer(StartBufferOffset, ReceiveBufferSize);
            var willRaiseEvent = _listenSocket.AcceptAsync(ReceiveSocketAsyncEventArgs);
            if (!willRaiseEvent)
            {
                ProcessAccept();
            }
        }

        public Task WriteAsync(int startOffset, int len)
        {
            if (!ResponseHeadersSend)
            {
                FillResponse(false);
                if (_responseHeaderPos > ReceiveBufferSize) throw new ArgumentException(string.Format("Response headers are longer({0}) than buffer({1})", _responseHeaderPos, ReceiveBufferSize));
                OptimallyMergeTwoRegions(_buffer, StartBufferOffset + ReceiveBufferSize, _responseHeaderPos, ref startOffset, ref len);
                ResponseHeadersSend = true;
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
            SendSocketAsyncEventArgs.SetBuffer(ConstantsOffset, Server.Status100Continue.Length);
            _sending100Continue = true;
            var willRaiseEvent = Socket.SendAsync(SendSocketAsyncEventArgs);
            if (!willRaiseEvent)
            {
                ProcessSend();
            }
        }

        public void StartNextReceive()
        {
            NormalizeReceiveBuffer();
            var count = StartBufferOffset + ReceiveBufferSize - ReceiveBufferFullness;
            if (count > 0)
            {
                ReceiveSocketAsyncEventArgs.SetBuffer(ReceiveBufferFullness, count);
                var willRaiseEvent = Socket.ReceiveAsync(ReceiveSocketAsyncEventArgs);
                if (!willRaiseEvent)
                {
                    while (ProcessReceive()) ;
                }
            }
        }
    }
}