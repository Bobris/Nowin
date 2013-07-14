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

        public SslTransportHandler(ITransportLayerHandler next, X509Certificate serverCertificate, byte[] buffer, int startBufferOffset, int bufferSize)
        {
            _next = next;
            _serverCertificate = serverCertificate;
            _buffer = buffer;
            EncryptedReceiveBufferOffset = startBufferOffset;
            EncryptedSendBufferOffset = startBufferOffset + bufferSize;
            _bufferSize = bufferSize;
            _ssl = new SslStream(new InputStream(this), true);
        }

        class InputStream : Stream
        {
            readonly SslTransportHandler _owner;

            public InputStream(SslTransportHandler owner)
            {
                _owner = owner;
            }

            public override void Flush()
            {
                throw new NotImplementedException();
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                throw new InvalidOperationException();
            }

            public override void SetLength(long value)
            {
                throw new InvalidOperationException();
            }

            public override int Read(byte[] buffer, int offset, int count)
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

        public void FinishReceive(int offset, int length)
        {
            throw new NotImplementedException();
        }

        public void FinishReceiveWithAbort()
        {
            throw new NotImplementedException();
        }

        public void FinishSend(Exception exception)
        {
            throw new NotImplementedException();
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
            Callback.StartDisconnect();
        }
    }
}