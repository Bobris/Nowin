using System;
using System.IO;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;

namespace NowinWebServer
{
    class SslTransportHandler : ITransportLayerHandler, ITransportLayerCallback
    {
        readonly ITransportLayerHandler _next;
        readonly X509Certificate _serverCertificate;
        readonly byte[] _buffer;
        readonly int _bufferSize;
        SslStream _ssl;
        readonly int EncryptedReceiveBufferOffset;
        readonly int EncryptedSendBufferOffset;
        Task _authenticateTask;
        int _recvOffset;
        int _recvLength;
        readonly InputStream _inputStream;

        public SslTransportHandler(ITransportLayerHandler next, X509Certificate serverCertificate, byte[] buffer, int startBufferOffset, int bufferSize)
        {
            _next = next;
            _serverCertificate = serverCertificate;
            _buffer = buffer;
            EncryptedReceiveBufferOffset = startBufferOffset;
            EncryptedSendBufferOffset = startBufferOffset + bufferSize;
            _bufferSize = bufferSize;
            _inputStream = new InputStream(this);
            _ssl = new SslStream(_inputStream, true);
        }

        class InputStream : Stream
        {
            readonly SslTransportHandler _owner;
            readonly byte[] _buf;
            TaskCompletionSource<int> _tcs;
            int _receivePos;
            int _receiveLen;
            byte[] _asyncBuffer;
            int _asyncOffset;
            int _asyncCount;

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
                _tcs.TrySetResult(l);
            }

            public void FinishReceiveWithAbort()
            {
                _tcs.TrySetCanceled();
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
                return ReadOverflowAsync(buffer, offset, count).Result;
            }

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, System.Threading.CancellationToken cancellationToken)
            {
                if (_receiveLen > 0)
                {
                    var l = Math.Min(count, _receiveLen);
                    Array.Copy(_buf, _receivePos, buffer, offset, l);
                    _receivePos += l;
                    _receiveLen -= l;
                    return Task.FromResult(l);
                }
                return ReadOverflowAsync(buffer, offset, count);
            }

            Task<int> ReadOverflowAsync(byte[] buffer, int offset, int count)
            {
                _asyncBuffer = buffer;
                _asyncOffset = offset;
                _asyncCount = count;
                _tcs = new TaskCompletionSource<int>();
                _owner.Callback.StartReceive(_owner.EncryptedReceiveBufferOffset, _owner._bufferSize);
                return _tcs.Task;
            }

            public void FinishSend(Exception exception)
            {
                throw new NotImplementedException();
            }

            public override void Flush()
            {
                throw new NotImplementedException();
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                throw new NotImplementedException();
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
            _next.PrepareAccept();
        }

        public void FinishAccept(int offset, int length)
        {
            _inputStream.FinishAccept(offset, length);
            try
            {
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
            Callback.StartAccept(EncryptedReceiveBufferOffset, _bufferSize);
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
                t.ContinueWith((t2, callback) => ((ITransportLayerCallback) callback).StartDisconnect(), Callback);
            }
            else
            {
                Callback.StartDisconnect();
            }
        }
    }
}