namespace Flux
{
    using System;
    using System.IO;
    using System.Threading;

    internal sealed class BufferStream : Stream
    {
        private MemoryStream _memoryStream;

        internal Stream InternalStream
        {
            get { return _memoryStream
                ??
                Interlocked.CompareExchange(ref _memoryStream, new MemoryStream(), null)
                ??
                _memoryStream; }
        }

        public void Reset()
        {
            if (_memoryStream == null) return;
            var buffer = _memoryStream.GetBuffer();
            Array.Clear(buffer, 0, buffer.Length);
            _memoryStream.Position = 0;
            _memoryStream.SetLength(0);
        }

        public override void Flush()
        {
            InternalStream.Flush();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            InternalStream.Write(buffer, offset, count);
        }

        public override long Length
        {
            get
            {
                return _memoryStream == null ? 0 : _memoryStream.Length;
            }
        }

        public override long Position
        {
            get
            {
                return _memoryStream == null ? 0 : _memoryStream.Position;
            }
            set { InternalStream.Position = value; }
        }

        public override void SetLength(long value)
        {
            InternalStream.SetLength(value);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            return InternalStream.Read(buffer, offset, count);
        }

        public override bool CanRead
        {
            get { return InternalStream.CanRead; }
        }

        public override bool CanSeek
        {
            get { return InternalStream.CanSeek; }
        }

        public override bool CanWrite
        {
            get { return InternalStream.CanWrite; }
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            return InternalStream.Seek(offset, origin);
        }

        public override void Close()
        {
        }
        protected override void Dispose(bool disposing)
        {
        }
        public override IAsyncResult BeginRead(byte[] buffer, int offset, int count, AsyncCallback callback, object state)
        {
            return InternalStream.BeginRead(buffer, offset, count, callback, state);
        }

        public override IAsyncResult BeginWrite(byte[] buffer, int offset, int count, AsyncCallback callback, object state)
        {
            return InternalStream.BeginWrite(buffer, offset, count, callback, state);
        }

        public override int EndRead(IAsyncResult asyncResult)
        {
            return InternalStream.EndRead(asyncResult);
        }

        public override void EndWrite(IAsyncResult asyncResult)
        {
            InternalStream.EndWrite(asyncResult);
        }

        public override int ReadByte()
        {
            return InternalStream.ReadByte();
        }

        public override int ReadTimeout
        {
            get
            {
                return InternalStream.ReadTimeout;
            }
            set
            {
                InternalStream.ReadTimeout = value;
            }
        }

        internal void ForceDispose()
        {
            if (_memoryStream != null)
            {
                _memoryStream.Dispose();
            }
        }

        internal bool TryGetBuffer(out byte[] buffer)
        {
            if (_memoryStream != null)
            {
                buffer = _memoryStream.GetBuffer();
                return true;
            }
            buffer = null;
            return false;
        }
    }
}