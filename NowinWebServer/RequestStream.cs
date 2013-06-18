using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace NowinWebServer
{
    internal class RequestStream : Stream
    {
        readonly ConnectionInfo _connectionInfo;
        readonly byte[] _buf;
        TaskCompletionSource<int> _tcs;
        byte[] _asyncBuffer;
        int _asyncOffset;
        int _asyncCount;
        int _asyncResult;
        long _position;

        public RequestStream(ConnectionInfo connectionInfo)
        {
            _connectionInfo = connectionInfo;
            _buf = connectionInfo.ReceiveSocketAsyncEventArgs.Buffer;
        }

        public override void Flush()
        {
            throw new InvalidOperationException();
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            throw new InvalidOperationException();
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            switch (origin)
            {
                case SeekOrigin.Begin:
                    Position = offset;
                    break;
                case SeekOrigin.Current:
                    Position = Position + offset;
                    break;
                case SeekOrigin.End:
                    Position = Length + offset;
                    break;
                default:
                    throw new ArgumentOutOfRangeException("origin");
            }
            return Position;
        }

        public override void SetLength(long value)
        {
            throw new InvalidOperationException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var len = ReadSyncPart(buffer, ref offset, ref count);
            if (count == 0) return len;
            return ReadOverflowAsync(buffer, offset, count, len).Result;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            var len = ReadSyncPart(buffer, ref offset, ref count);
            if (count == 0) return Task.FromResult(len);
            return ReadOverflowAsync(buffer, offset, count, len);
        }

        int ReadSyncPart(byte[] buffer, ref int offset, ref int count)
        {
            if (Position == 0 && _connectionInfo.ShouldSend100Continue)
            {
                _connectionInfo.Send100Continue();
            }
            if ((uint)count > _connectionInfo.RequestContentLength - (ulong)Position)
            {
                count = (int)(_connectionInfo.RequestContentLength - (ulong)Position);
            }
            if (count == 0) return 0;
            var len = Math.Min(count, _connectionInfo.ReceiveBufferDataLength);
            if (len > 0)
            {
                Array.Copy(_buf, _connectionInfo.StartBufferOffset + _connectionInfo.ReceiveBufferPos, buffer, offset, len);
                _position += len;
                _connectionInfo.ReceiveBufferPos += len;
                offset += len;
                count -= len;
                if (count == 0) return len;
            }
            return len;
        }

        Task<int> ReadOverflowAsync(byte[] buffer, int offset, int count, int len)
        {
            _tcs = new TaskCompletionSource<int>();
            _asyncBuffer = buffer;
            _asyncOffset = offset;
            _asyncCount = count;
            _asyncResult = len;
            _connectionInfo.StartNextReceive();
            return _tcs.Task;
        }

        public bool ProcessDataAndShouldReadMore()
        {
            var len = Math.Min(_asyncCount, _connectionInfo.ReceiveBufferDataLength);
            if (len > 0)
            {
                Array.Copy(_buf, _connectionInfo.StartBufferOffset + _connectionInfo.ReceiveBufferPos, _asyncBuffer, _asyncOffset, len);
                _position += len;
                _connectionInfo.ReceiveBufferPos += len;
                _asyncOffset += len;
                _asyncCount -= len;
                _asyncResult += len;
                if (_asyncCount == 0)
                {
                    _tcs.SetResult(_asyncResult);
                    return false;
                }
            }
            return true;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new InvalidOperationException();
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
            get { return false; }
        }

        public override long Length
        {
            get { return (long)_connectionInfo.RequestContentLength; }
        }

        public override long Position
        {
            get { return _position; }
            set { if (_position != value) throw new ArgumentOutOfRangeException("value"); }
        }
    }
}