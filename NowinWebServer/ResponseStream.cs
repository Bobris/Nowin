using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace NowinWebServer
{
    class ResponseStream : Stream
    {
        readonly Transport2HttpHandler _transport2HttpHandler;
        readonly byte[] _buf;
        internal readonly int StartOffset;
        internal int LocalPos;
        readonly int _maxLen;
        long _pos;

        public ResponseStream(Transport2HttpHandler transport2HttpHandler)
        {
            _transport2HttpHandler = transport2HttpHandler;
            _buf = transport2HttpHandler.Buffer;
            _maxLen = transport2HttpHandler.ReceiveBufferSize;
            StartOffset = transport2HttpHandler.ResponseBodyBufferOffset;
        }

        public override void Flush()
        {
            FlushAsync(CancellationToken.None).Wait();
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            return FlushAsyncCore();
        }

        Task FlushAsyncCore()
        {
            var len = LocalPos;
            LocalPos = 0;
            return _transport2HttpHandler.WriteAsync(_buf, StartOffset, len);
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
                {
                    await FlushAsyncCore();
                    if ((count >= _maxLen) && _transport2HttpHandler.CanUseDirectWrite())
                    {
                        _pos += count;
                        await _transport2HttpHandler.WriteAsync(buffer, offset, count);
                        return;
                    }
                }
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