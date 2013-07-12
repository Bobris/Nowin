using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace NowinWebServer
{
    internal class RequestStream : Stream
    {
        readonly Transport2Http2OwinHandler _transport2Http2OwinHandler;
        readonly byte[] _buf;
        TaskCompletionSource<int> _tcs;
        byte[] _asyncBuffer;
        int _asyncOffset;
        int _asyncCount;
        int _asyncResult;
        long _position;
        ChunkedDecoder _chunkedDecoder = new ChunkedDecoder();

        public RequestStream(Transport2Http2OwinHandler transport2Http2OwinHandler)
        {
            _transport2Http2OwinHandler = transport2Http2OwinHandler;
            _buf = transport2Http2OwinHandler.Buffer;
        }

        public void Reset()
        {
            _position = 0;
            _chunkedDecoder.Reset();
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
            ReadSyncPart(buffer, offset, count);
            if (_asyncCount == 0) return _asyncResult;
            return ReadOverflowAsync().Result;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            ReadSyncPart(buffer, offset, count);
            if (_asyncCount == 0) return Task.FromResult(_asyncResult);
            return ReadOverflowAsync();
        }

        void ReadSyncPart(byte[] buffer, int offset, int count)
        {
            if (Position == 0 && _transport2Http2OwinHandler.ShouldSend100Continue)
            {
                _transport2Http2OwinHandler.Send100Continue();
            }
            if ((uint)count > _transport2Http2OwinHandler.RequestContentLength - (ulong)Position)
            {
                count = (int)(_transport2Http2OwinHandler.RequestContentLength - (ulong)Position);
            }
            _asyncBuffer = buffer;
            _asyncOffset = offset;
            _asyncCount = count;
            _asyncResult = 0;
            if (count == 0) return;
            ProcessDataInternal();
        }

        Task<int> ReadOverflowAsync()
        {
            _tcs = new TaskCompletionSource<int>();
            _transport2Http2OwinHandler.StartNextReceive();
            return _tcs.Task;
        }

        void ProcessDataInternal()
        {
            if (_transport2Http2OwinHandler.RequestIsChunked)
            {
                ProcessChunkedDataInternal();
                return;
            }
            var len = Math.Min(_asyncCount, _transport2Http2OwinHandler.ReceiveBufferDataLength);
            if (len > 0)
            {
                Array.Copy(_buf, _transport2Http2OwinHandler.StartBufferOffset + _transport2Http2OwinHandler.ReceiveBufferPos, _asyncBuffer, _asyncOffset, len);
                _position += len;
                _transport2Http2OwinHandler.ReceiveBufferPos += len;
                _asyncOffset += len;
                _asyncCount -= len;
                _asyncResult += len;
            }
        }

        void ProcessChunkedDataInternal()
        {
            var encodedDataAvail = _transport2Http2OwinHandler.ReceiveBufferDataLength;
            var encodedDataOfs = _transport2Http2OwinHandler.StartBufferOffset + _transport2Http2OwinHandler.ReceiveBufferPos;
            while (encodedDataAvail > 0)
            {
                var decodedDataAvail = _chunkedDecoder.DataAvailable;
                if (decodedDataAvail < 0)
                {
                    _transport2Http2OwinHandler.RequestContentLength = (ulong)_position;
                    _asyncCount = 0;
                    break;
                }
                if (decodedDataAvail == 0)
                {
                    if (_chunkedDecoder.ProcessByte(_buf[encodedDataOfs]))
                    {
                        _transport2Http2OwinHandler.RequestContentLength = (ulong)_position;
                        _asyncCount = 0;
                    }
                    encodedDataOfs++;
                    encodedDataAvail--;
                }
                else
                {
                    if (decodedDataAvail > encodedDataAvail)
                    {
                        decodedDataAvail = encodedDataAvail;
                    }
                    if (decodedDataAvail > _asyncCount)
                    {
                        decodedDataAvail = _asyncCount;
                    }
                    _chunkedDecoder.DataEatten(decodedDataAvail);
                    Array.Copy(_buf, encodedDataOfs, _asyncBuffer, _asyncOffset, decodedDataAvail);
                    _asyncOffset += decodedDataAvail;
                    _asyncCount -= decodedDataAvail;
                    _asyncResult += decodedDataAvail;
                    encodedDataAvail -= decodedDataAvail;
                    encodedDataOfs += decodedDataAvail;
                    _position += decodedDataAvail;
                }
            }
            _transport2Http2OwinHandler.ReceiveBufferPos = encodedDataOfs - _transport2Http2OwinHandler.StartBufferOffset;
        }

        public bool ProcessDataAndShouldReadMore()
        {
            ProcessDataInternal();
            if (_asyncCount == 0)
            {
                _tcs.SetResult(_asyncResult);
                return false;
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
            get { return (long)_transport2Http2OwinHandler.RequestContentLength; }
        }

        public override long Position
        {
            get { return _position; }
            set { if (_position != value) throw new ArgumentOutOfRangeException("value"); }
        }
    }
}