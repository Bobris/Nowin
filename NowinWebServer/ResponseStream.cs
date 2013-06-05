using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace NowinWebServer
{
    class ResponseStream : Stream
    {
        readonly ConnectionInfo _connectionInfo;
        readonly byte[] _buf;
        internal readonly int StartOffset;
        internal int LocalPos;
        readonly int _maxLen;
        long _pos;

        public ResponseStream(ConnectionInfo connectionInfo)
        {
            _connectionInfo = connectionInfo;
            _buf = connectionInfo.SendSocketAsyncEventArgs.Buffer;
            _maxLen = connectionInfo.ReceiveBufferSize - 2; // Space for CRLF after chunk
            StartOffset = connectionInfo.StartBufferOffset + connectionInfo.ReceiveBufferSize * 2; // Skip receive buffer and header buffer parts
        }

        public override void Flush()
        {
            FlushAsync(CancellationToken.None).Wait();
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            var len = LocalPos;
            LocalPos = 0;
            return _connectionInfo.WriteAsync(StartOffset, len);
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
            throw new InvalidOperationException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (count <= _maxLen - LocalPos)
            {
                Array.Copy(buffer, offset, _buf, StartOffset + LocalPos, count);
                LocalPos += count;
                _pos += count;
                return;
            }
            WriteAsync(buffer, offset, count, CancellationToken.None).Wait();
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (count <= _maxLen - LocalPos)
            {
                Array.Copy(buffer, offset, _buf, StartOffset + LocalPos, count);
                LocalPos += count;
                _pos += count;
                return Task.Delay(0);
            }
            return WriteOverflowAsync(buffer, offset, count);
        }

        async Task WriteOverflowAsync(byte[] buffer, int offset, int count)
        {
            do
            {
                if (LocalPos == _maxLen)
                    await FlushAsync(CancellationToken.None);
                var tillEnd = _maxLen - LocalPos;
                if (tillEnd > count) tillEnd = count;
                Array.Copy(buffer, offset, _buf, StartOffset + LocalPos, tillEnd);
                LocalPos += tillEnd;
                _pos += tillEnd;
                offset += tillEnd;
                count -= tillEnd;
            } while (count > 0);
        }

        public override bool CanRead
        {
            get { return false; }
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
            get
            {
                return _pos;
            }
        }

        public override long Position
        {
            get
            {
                return _pos;
            }
            set
            {
                if (_pos != value) throw new ArgumentOutOfRangeException("value");
            }
        }

        public void Reset()
        {
            LocalPos = 0;
            _pos = 0;
        }
    }
}