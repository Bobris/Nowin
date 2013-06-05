namespace Flux
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Globalization;
    using System.IO;
    using System.Net.Sockets;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Fix;
    using AppFunc = System.Func< // Call
        System.Collections.Generic.IDictionary<string, object>, // Environment
        System.Threading.Tasks.Task>; // Completion

    internal sealed class Instance : IDisposable
    {
        private static readonly byte[] Status100Continue = Encoding.UTF8.GetBytes("HTTP/1.1 100 Continue\r\n");
        private static readonly byte[] Status404NotFound = Encoding.UTF8.GetBytes("HTTP/1.1 404 Not found");
        private static readonly byte[] Status500InternalServerError = Encoding.UTF8.GetBytes("HTTP/1.1 500 Internal Server Error");
        private readonly Socket _socket;
        private readonly AppFunc _app;
        private readonly int _timeoutSeconds;
        private readonly NetworkStream _networkStream;
        private readonly TaskCompletionSource<int> _taskCompletionSource = new TaskCompletionSource<int>();
        private readonly Timer _timer;
        private int _tick = int.MinValue;
        private bool _disposed;
        private bool _isHttp10;
        private bool _keepAlive;

        private void TimeoutCallback(object state)
        {
            if (++_tick > _timeoutSeconds)
            {
                Dispose();
                _taskCompletionSource.SetResult(0);
            }
        }

        private BufferStream _bufferStream;

        public Instance(Socket socket, AppFunc app, int timeoutSeconds = 2)
        {
            _socket = socket;
            _networkStream = new NetworkStream(_socket, FileAccess.ReadWrite, false);
            _app = app;
            _timeoutSeconds = timeoutSeconds;
            _timer = new Timer(TimeoutCallback, null, 1000, 1000);
        }

        public Task Run()
        {
            try
            {
                var env = CreateEnvironmentDictionary();
                var headers = HeaderParser.Parse(_networkStream);
                CheckKeepAlive(headers);
                env[OwinKeys.RequestHeaders] = headers;
                env[OwinKeys.ResponseHeaders] = new Dictionary<string, string[]>();
                env[OwinKeys.ResponseBody] = Buffer;
                string[] expectContinue;
                if (headers.TryGetValue("Expect", out expectContinue))
                {
                    if (expectContinue.Length == 1 && expectContinue[0].Equals("100-Continue", StringComparison.OrdinalIgnoreCase))
                    {
                        _networkStream.WriteAsync(Status100Continue, 0, Status100Continue.Length)
                                      .ContinueWith(t =>
                                                        {
                                                            if (t.IsFaulted) return t;
                                                            Buffer.Reset();
                                                            return _app(env)
                                                                .ContinueWith(t2 => Result(t2, env));
                                                        });
                    }
                }
                Buffer.Reset();
                _app(env).ContinueWith(t2 => Result(t2, env));
            }
            catch (Exception ex)
            {
                _taskCompletionSource.SetException(ex);
            }
            return _taskCompletionSource.Task;
        }

        void CheckKeepAlive(IDictionary<string, string[]> headers)
        {
            if (!_isHttp10)
            {
                _keepAlive = true;
                return;
            }
            _keepAlive = false;
            string[] connectionValues;
            if (headers.TryGetValue("Connection", out connectionValues))
            {
                if (connectionValues.Length == 1 && connectionValues[0].Equals("Keep-Alive", StringComparison.OrdinalIgnoreCase))
                {
                    _keepAlive = true;
                }
            }
        }

        private Dictionary<string, object> CreateEnvironmentDictionary()
        {
            var env = new Dictionary<string, object>
                          {
                              {OwinKeys.Version, "0.8"}
                          };
            var requestLine = RequestLineParser.Parse(_networkStream);
            _isHttp10 = requestLine.HttpVersion.EndsWith("/1.0");
            env[OwinKeys.RequestMethod] = requestLine.Method;
            env[OwinKeys.RequestPathBase] = string.Empty;
            if (requestLine.Uri.StartsWith("http", StringComparison.InvariantCultureIgnoreCase))
            {
                Uri uri;
                if (Uri.TryCreate(requestLine.Uri, UriKind.Absolute, out uri))
                {
                    env[OwinKeys.RequestPath] = uri.AbsolutePath;
                    env[OwinKeys.RequestQueryString] = uri.Query;
                    env[OwinKeys.RequestScheme] = uri.Scheme;
                }
            }
            else
            {
                var splitUri = requestLine.Uri.Split('?');
                env[OwinKeys.RequestPath] = splitUri[0];
                env[OwinKeys.RequestQueryString] = splitUri.Length == 2 ? splitUri[1] : string.Empty;
                env[OwinKeys.RequestScheme] = "http";
            }

            env[OwinKeys.RequestBody] = _networkStream;
            env[OwinKeys.CallCancelled] = new CancellationToken();
            return env;
        }

        internal BufferStream Buffer
        {
            get { return _bufferStream ?? (_bufferStream = new BufferStream()); }
        }

        private void Result(Task task, IDictionary<string, object> env)
        {
            if (task.IsFaulted)
            {
                _networkStream.WriteAsync(Status500InternalServerError, 0, Status500InternalServerError.Length)
                    .ContinueWith(_ => Dispose());
            }

            int status = env.GetValueOrDefault(OwinKeys.ResponseStatusCode, 0);
            if (status == 0 || status == 404)
            {
                _networkStream.WriteAsync(Status404NotFound, 0, Status404NotFound.Length)
                    .ContinueWith(_ => Dispose());
            }

            WriteResult(status, env)
                .ContinueWith(ListenAgain);
        }

        private void ListenAgain(Task task)
        {
            if (task.IsFaulted)
            {
                _taskCompletionSource.SetException(task.Exception ?? new Exception("Unknown error."));
                return;
            }
            if (task.IsCanceled)
            {
                _taskCompletionSource.SetCanceled();
                return;
            }
            if (_disposed) return;
            if (!_keepAlive)
            {
                _timer.Dispose();
                Dispose();
                return;
            }
            var buffer = new byte[1];
            _tick = 0;
            try
            {
                _socket.BeginReceive(buffer, 0, 1, SocketFlags.Peek, ListenCallback, null);
            }
            catch (ObjectDisposedException)
            {
                _timer.Dispose();
                Dispose();
            }
        }

        private void ListenCallback(IAsyncResult ar)
        {
            _tick = int.MinValue;
            if (_disposed) return;
            int size = _socket.EndReceive(ar);
            if (size > 0)
            {
                Run();
            }
        }

        private Task WriteResult(int status, IDictionary<string, object> env)
        {
            var headerBuilder = new StringBuilder();
            headerBuilder.Append(_isHttp10 ? "HTTP/1.0 " : "HTTP/1.1 ").Append(status).Append("\r\n");

            var headers = (IDictionary<string, string[]>)env[OwinKeys.ResponseHeaders];
            if (!headers.ContainsKey("Content-Length"))
            {
                headers["Content-Length"] = new[] { Buffer.Length.ToString(CultureInfo.InvariantCulture) };
            }
            if (_isHttp10 && _keepAlive)
            {
                headers["Connection"] = new[] { "Keep-Alive" };
            }
            foreach (var header in headers)
            {
                switch (header.Value.Length)
                {
                    case 0:
                        continue;
                    case 1:
                        headerBuilder.AppendFormat("{0}: {1}\r\n", header.Key, header.Value[0]);
                        continue;
                }
                foreach (var value in header.Value)
                {
                    headerBuilder.AppendFormat("{0}: {1}\r\n", header.Key, value);
                }
            }

            return WriteResponse(headerBuilder);
        }

        private Task WriteResponse(StringBuilder headerBuilder)
        {
            headerBuilder.Append("\r\n");
            var bytes = Encoding.UTF8.GetBytes(headerBuilder.ToString());

            var task = _networkStream.WriteAsync(bytes, 0, bytes.Length);
            if (task.IsFaulted || task.IsCanceled) return task;
            if (Buffer.Length > 0)
            {
                task = task.ContinueWith(t => WriteBuffer()).Unwrap();
            }

            return task;
        }

        private Task WriteBuffer()
        {
            if (Buffer.Length <= int.MaxValue)
            {
                Buffer.Position = 0;
                byte[] buffer;
                if (Buffer.TryGetBuffer(out buffer))
                {
                    return _networkStream.WriteAsync(buffer, 0, (int)Buffer.Length);
                }
                Buffer.CopyTo(_networkStream);
            }
            return TaskHelper.Completed();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _timer.TryDispose();
            _networkStream.TryDispose();
            _socket.TryDispose();
            if (_bufferStream != null)
            {
                try
                {
                    _bufferStream.ForceDispose();
                }
                catch (Exception ex)
                {
                    Trace.TraceError(ex.Message);
                }
            }
        }
    }
}