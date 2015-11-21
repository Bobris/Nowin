using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Runtime.ExceptionServices;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

namespace Nowin
{
    class SslTransportHandler : ITransportLayerHandler, ITransportLayerCallback
    {
        readonly ITransportLayerHandler _next;
        readonly X509Certificate _serverCertificate;
        readonly SslProtocols _protocols;
        readonly bool _clientCertificateRequired;
        SslStream _ssl;
        Task _authenticateTask;
        byte[] _recvBuffer;
        int _recvOffset;
        int _recvLength;
        readonly InputStream _inputStream;

        public SslTransportHandler(ITransportLayerHandler next, X509Certificate serverCertificate, SslProtocols protocols, bool clientCertificateRequired)
        {
            _protocols = protocols;
            _clientCertificateRequired = clientCertificateRequired;
            _next = next;
            _serverCertificate = serverCertificate;
            _inputStream = new InputStream(this);
            next.Callback = this;
        }

        class InputStream : Stream
        {
            readonly SslTransportHandler _owner;
            TaskCompletionSource<int> _tcsReceive;
            AsyncCallback _callbackReceive;
            TaskCompletionSource<object> _tcsSend;
            AsyncCallback _callbackSend;

            public InputStream(SslTransportHandler owner)
            {
                _owner = owner;
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                throw new InvalidOperationException();
            }

            public override void SetLength(long value)
            {
                throw new InvalidOperationException();
            }

            public void FinishReceive(int length)
            {
                if (length == -1)
                    _tcsReceive.SetCanceled();
                else
                    _tcsReceive.SetResult(length);
                _callbackReceive?.Invoke(_tcsReceive.Task);
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                return ReadOverflowAsync(buffer, offset, count, null, null).Result;
            }

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                return ReadOverflowAsync(buffer, offset, count, null, null);
            }

            public override IAsyncResult BeginRead(byte[] buffer, int offset, int count, AsyncCallback callback, object state)
            {
                return ReadOverflowAsync(buffer, offset, count, callback, state);
            }

            Task<int> ReadOverflowAsync(byte[] buffer, int offset, int count, AsyncCallback callback, object state)
            {
                _tcsReceive = new TaskCompletionSource<int>(state);
                _callbackReceive = callback;
                _owner.Callback.StartReceive(buffer, offset, count);
                return _tcsReceive.Task;
            }

            public override int EndRead(IAsyncResult asyncResult)
            {
                if (((Task<int>)asyncResult).IsCanceled)
                {
                    return 0;
                }
                try
                {
                    return ((Task<int>)asyncResult).Result;
                }
                catch (AggregateException ex)
                {
                    ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                    throw;
                }
            }

            public void FinishSend(Exception exception)
            {
                if (exception == null)
                {
                    _tcsSend.SetResult(null);
                }
                else
                {
                    _tcsSend.SetException(exception);
                }
                _callbackSend?.Invoke(_tcsSend.Task);
            }

            public override void Flush()
            {
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                WriteAsync(buffer, offset, count, CancellationToken.None).Wait();
            }

            public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                _tcsSend = new TaskCompletionSource<object>();
                _owner.Callback.StartSend(buffer, offset, count);
                return _tcsSend.Task;
            }

            public override IAsyncResult BeginWrite(byte[] buffer, int offset, int count, AsyncCallback callback, object state)
            {
                _tcsSend = new TaskCompletionSource<object>(state);
                _callbackSend = callback;
                _owner.Callback.StartSend(buffer, offset, count);
                return _tcsSend.Task;
            }

            public override void EndWrite(IAsyncResult asyncResult)
            {
                try
                {
                    ((Task<object>)asyncResult).Wait();
                }
                catch (AggregateException ex)
                {
                    ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                    throw;
                }
            }

            public override bool CanRead => true;

            public override bool CanSeek => false;

            public override bool CanWrite => true;

            public override long Length => long.MaxValue;

            public override long Position { get; set; }
        }

        public void Dispose()
        {
            _next.Dispose();
        }

        public ITransportLayerCallback Callback { set; private get; }

        public void PrepareAccept()
        {
            _ssl = null;
            var t = _authenticateTask;
            _authenticateTask = null;
            if (t != null && !t.IsCompleted)
            {
                t.ContinueWith((t2, next) =>
                {
                    ((ITransportLayerHandler)next).PrepareAccept();
                }, _next);
                return;
            }
            _next.PrepareAccept();
        }

        public void FinishAccept(byte[] buffer, int offset, int length, IPEndPoint remoteEndPoint, IPEndPoint localEndPoint)
        {
            Debug.Assert(length == 0);
            try
            {
                _ssl = new SslStream(_inputStream, true);
                _authenticateTask = _ssl.AuthenticateAsServerAsync(_serverCertificate, _clientCertificateRequired, _protocols, false).ContinueWith((t, selfObject) =>
                {
                    var self = (SslTransportHandler)selfObject;
                    if (t.IsFaulted || t.IsCanceled)
                        self.Callback.StartDisconnect();
                    else
                        _next.SetRemoteCertificate(_ssl.RemoteCertificate);
                }, this);
                _next.FinishAccept(_recvBuffer, _recvOffset, 0, remoteEndPoint, localEndPoint);
            }
            catch (Exception)
            {
                Callback.StartDisconnect();
            }
        }

        public void SetRemoteCertificate(X509Certificate remoteCertificate)
        {
            throw new InvalidOperationException();
        }

        public void FinishReceive(byte[] buffer, int offset, int length)
        {
            _inputStream.FinishReceive(length);
        }

        public void FinishSend(Exception exception)
        {
            _inputStream.FinishSend(exception);
        }

        public void StartAccept(byte[] buffer, int offset, int length)
        {
            _recvBuffer = buffer;
            _recvOffset = offset;
            _recvLength = length;
            Callback.StartAccept(null, 0, 0);
        }

        public void StartReceive(byte[] buffer, int offset, int length)
        {
            _recvBuffer = buffer;
            _recvOffset = offset;
            _recvLength = length;
            try
            {
                if (!_authenticateTask.IsCompleted)
                {
                    _authenticateTask.ContinueWith((t, selfObject) =>
                    {
                        var self = (SslTransportHandler)selfObject;
                        if (t.IsCanceled || t.IsFaulted)
                        {
                            self._next.FinishReceive(null, 0, -1);
                        }
                        else
                        {
                            _ssl.ReadAsync(self._recvBuffer, self._recvOffset, self._recvLength).ContinueWith((t2, selfObject2) =>
                            {
                                var self2 = (SslTransportHandler)selfObject2;
                                if (t2.IsFaulted || t2.IsCanceled || t2.Result == 0)
                                    self._next.FinishReceive(null, 0, -1);
                                else
                                    self._next.FinishReceive(self2._recvBuffer, self2._recvOffset, t2.Result);
                            }, self);
                        }
                    }, this);
                    return;
                }
                _ssl.ReadAsync(buffer, offset, length).ContinueWith((t, selfObject) =>
                {
                    var self = (SslTransportHandler)selfObject;
                    if (t.IsFaulted || t.IsCanceled || t.Result == 0)
                        self._next.FinishReceive(null, 0, -1);
                    else
                        self._next.FinishReceive(self._recvBuffer, self._recvOffset, t.Result);
                }, this);
            }
            catch (Exception)
            {
                _next.FinishReceive(null, 0, -1);
            }
        }

        public void StartSend(byte[] buffer, int offset, int length)
        {
            try
            {
                _ssl.WriteAsync(buffer, offset, length).ContinueWith((t, selfObject) =>
                {
                    var self = (SslTransportHandler)selfObject;
                    if (t.IsCanceled)
                    {
                        self._next.FinishSend(new OperationCanceledException());
                    }
                    else if (t.IsFaulted)
                    {
                        self._next.FinishSend(t.Exception);
                    }
                    else
                    {
                        self._next.FinishSend(null);
                    }
                }, this);
            }
            catch (Exception ex)
            {
                _next.FinishSend(ex);
            }
        }

        public void StartDisconnect()
        {
            Callback.StartDisconnect();
        }
    }
}