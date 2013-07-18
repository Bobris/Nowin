using System;
using System.IO;
using System.Net.Security;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

namespace NowinWebServer
{
    class SslTransportHandler : ITransportLayerHandler, ITransportLayerCallback
    {
        internal const int SendBufferExtendedBySslSize = 128; // 102 could be enough probably so to have some reserve making it 128

        readonly ITransportLayerHandler _next;
        readonly X509Certificate _serverCertificate;
        readonly byte[] _buffer;
        readonly int _bufferSize;
        readonly int _sendBufferSize;
        SslStream _ssl;
        readonly int _encryptedReceiveBufferOffset;
        readonly int _encryptedSendBufferOffset;
        Task _authenticateTask;
        int _recvOffset;
        int _recvLength;
        readonly InputStream _inputStream;

        public SslTransportHandler(ITransportLayerHandler next, X509Certificate serverCertificate, byte[] buffer, int startBufferOffset, int bufferSize)
        {
            _next = next;
            _serverCertificate = serverCertificate;
            _buffer = buffer;
            _encryptedReceiveBufferOffset = startBufferOffset;
            _encryptedSendBufferOffset = startBufferOffset + bufferSize;
            _bufferSize = bufferSize;
            _sendBufferSize = bufferSize + SendBufferExtendedBySslSize;
            _inputStream = new InputStream(this);
            next.Callback = this;
        }

        class InputStream : Stream
        {
            readonly SslTransportHandler _owner;
            readonly byte[] _buf;
            TaskCompletionSource<int> _tcsReceive;
            AsyncCallback _callbackReceive;
            int _receivePos;
            int _receiveLen;
            byte[] _asyncBuffer;
            int _asyncOffset;
            int _asyncCount;
            TaskCompletionSource<object> _tcsSend;
            AsyncCallback _callbackSend;

            public InputStream(SslTransportHandler owner)
            {
                _owner = owner;
                _buf = owner._buffer;
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                throw new InvalidOperationException();
            }

            public override void SetLength(long value)
            {
                throw new InvalidOperationException();
            }

            public void FinishAccept(int offset, int length)
            {
                _receivePos = offset;
                _receiveLen = length;
            }

            public void FinishReceive(int offset, int length)
            {
                _receivePos = offset;
                _receiveLen = length;
                var l = Math.Min(_asyncCount, _receiveLen);
                Array.Copy(_buf, _receivePos, _asyncBuffer, _asyncOffset, l);
                _receivePos += l;
                _receiveLen -= l;
                _tcsReceive.SetResult(l);
                if (_callbackReceive != null)
                {
                    _callbackReceive(_tcsReceive.Task);
                }
            }

            public void FinishReceiveWithAbort()
            {
                _tcsReceive.SetCanceled();
                if (_callbackReceive != null)
                {
                    _callbackReceive(_tcsReceive.Task);
                }
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (_receiveLen > 0)
                {
                    var l = Math.Min(count, _receiveLen);
                    Array.Copy(_buf, _receivePos, buffer, offset, l);
                    _receivePos += l;
                    _receiveLen -= l;
                    return l;
                }
                _callbackReceive = null;
                return ReadOverflowAsync(buffer, offset, count, null, null).Result;
            }

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                if (_receiveLen > 0)
                {
                    var l = Math.Min(count, _receiveLen);
                    Array.Copy(_buf, _receivePos, buffer, offset, l);
                    _receivePos += l;
                    _receiveLen -= l;
                    return Task.FromResult(l);
                }
                return ReadOverflowAsync(buffer, offset, count, null, null);
            }

            public override IAsyncResult BeginRead(byte[] buffer, int offset, int count, AsyncCallback callback, object state)
            {
                if (_receiveLen > 0)
                {
                    var l = Math.Min(count, _receiveLen);
                    Array.Copy(_buf, _receivePos, buffer, offset, l);
                    _receivePos += l;
                    _receiveLen -= l;
                    var res = new SyncAsyncResult(l, state);
                    if (callback != null)
                    {
                        callback(res);
                    }
                    return res;
                }
                return ReadOverflowAsync(buffer, offset, count, callback, state);
            }

            Task<int> ReadOverflowAsync(byte[] buffer, int offset, int count, AsyncCallback callback, object state)
            {
                _asyncBuffer = buffer;
                _asyncOffset = offset;
                _asyncCount = count;
                _tcsReceive = new TaskCompletionSource<int>(state);
                _callbackReceive = callback;
                _owner.Callback.StartReceive(_owner._encryptedReceiveBufferOffset, _owner._bufferSize);
                return _tcsReceive.Task;
            }

            class SyncAsyncResult : IAsyncResult
            {
                internal readonly int Result;
                readonly object _state;
                ManualResetEvent _waitHandle;

                public SyncAsyncResult(int result, object state)
                {
                    Result = result;
                    _state = state;
                }

                public bool IsCompleted { get { return true; } }
                public WaitHandle AsyncWaitHandle
                {
                    get { return LazyInitializer.EnsureInitialized(ref _waitHandle, () => new ManualResetEvent(true)); }
                }
                public object AsyncState { get { return _state; } }
                public bool CompletedSynchronously { get { return true; } }
            }

            public override int EndRead(IAsyncResult asyncResult)
            {
                var syncAsyncResult = asyncResult as SyncAsyncResult;
                if (syncAsyncResult != null)
                {
                    return syncAsyncResult.Result;
                }
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
                if (_callbackSend != null)
                {
                    _callbackSend(_tcsSend.Task);
                }
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
                if (count > _owner._sendBufferSize) throw new ArgumentOutOfRangeException("count", "Buffer size overflow");
                Array.Copy(buffer, offset, _buf, _owner._encryptedSendBufferOffset, count);
                _tcsSend = new TaskCompletionSource<object>();
                _owner.Callback.StartSend(_owner._encryptedSendBufferOffset, count);
                return _tcsSend.Task;
            }

            public override IAsyncResult BeginWrite(byte[] buffer, int offset, int count, AsyncCallback callback, object state)
            {
                if (count > _owner._sendBufferSize) throw new ArgumentOutOfRangeException("count", "Buffer size overflow");
                Array.Copy(buffer, offset, _buf, _owner._encryptedSendBufferOffset, count);
                _tcsSend = new TaskCompletionSource<object>(state);
                _callbackSend = callback;
                _owner.Callback.StartSend(_owner._encryptedSendBufferOffset, count);
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

            public override bool CanRead
            {
                get { return true; }
            }

            public override bool CanSeek
            {
                get { return false; }
            }

            public override bool CanWrite
            {
                get { return true; }
            }

            public override long Length
            {
                get { return long.MaxValue; }
            }

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
            _next.PrepareAccept();
        }

        public void FinishAccept(int offset, int length)
        {
            _inputStream.FinishAccept(offset, length);
            try
            {
                _ssl = new SslStream(_inputStream, true);
                _authenticateTask = _ssl.AuthenticateAsServerAsync(_serverCertificate).ContinueWith((t, selfObject) =>
                {
                    var self = (SslTransportHandler)selfObject;
                    if (t.IsFaulted || t.IsCanceled)
                        self._next.FinishAccept(self._recvOffset, 0);
                    else
                        self._ssl.ReadAsync(self._buffer, self._recvOffset, self._recvLength).ContinueWith((t2, selfObject2) =>
                        {
                            var self2 = (SslTransportHandler)selfObject2;
                            if (t2.IsFaulted || t2.IsCanceled)
                                self2._next.FinishAccept(self2._recvOffset, 0);
                            else
                                self2._next.FinishAccept(self2._recvOffset, t2.Result);
                        }, self);
                }, this);
            }
            catch (Exception)
            {
                Callback.StartDisconnect();
            }
        }

        public void FinishReceive(int offset, int length)
        {
            _inputStream.FinishReceive(offset, length);
        }

        public void FinishReceiveWithAbort()
        {
            _inputStream.FinishReceiveWithAbort();
        }

        public void FinishSend(Exception exception)
        {
            _inputStream.FinishSend(exception);
        }

        public void StartAccept(int offset, int length)
        {
            _recvOffset = offset;
            _recvLength = length;
            Callback.StartAccept(_encryptedReceiveBufferOffset, _bufferSize);
        }

        public void StartReceive(int offset, int length)
        {
            _recvOffset = offset;
            _recvLength = length;
            _ssl.ReadAsync(_buffer, offset, length).ContinueWith((t, selfObject) =>
            {
                var self = (SslTransportHandler)selfObject;
                if (t.IsFaulted || t.IsCanceled)
                    self._next.FinishReceiveWithAbort();
                else
                    self._next.FinishReceive(self._recvOffset, t.Result);
            }, this);
        }

        public void StartSend(int offset, int length)
        {
            _ssl.WriteAsync(_buffer, offset, length).ContinueWith((t, selfObject) =>
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

        public void StartDisconnect()
        {
            var t = _authenticateTask;
            _authenticateTask = null;
            if (t != null)
            {
                t.ContinueWith((t2, callback) => ((ITransportLayerCallback)callback).StartDisconnect(), Callback);
            }
            else
            {
                Callback.StartDisconnect();
            }
        }
    }
}