using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Nowin
{
    internal class ReqRespStream : Stream
    {
        readonly Transport2HttpHandler _transport2HttpHandler;
        readonly byte[] _buf;
        internal ulong RequestPosition;
        TaskCompletionSource<int> _tcs;
        byte[] _asyncBuffer;
        int _asyncOffset;
        int _asyncCount;
        int _asyncResult;
        ChunkedDecoder _chunkedDecoder = new ChunkedDecoder();

        internal readonly int ResponseStartOffset;
        internal int ResponseLocalPos;
        readonly int _responseMaxLen;
        ulong _responsePosition;

        public ReqRespStream(Transport2HttpHandler transport2HttpHandler)
        {
            _transport2HttpHandler = transport2HttpHandler;
            _buf = transport2HttpHandler.Buffer;
            _responseMaxLen = transport2HttpHandler.ReceiveBufferSize;
            ResponseStartOffset = transport2HttpHandler.ResponseBodyBufferOffset;
        }

        public void Reset()
        {
            _chunkedDecoder.Reset();
            RequestPosition = 0;
            ResponseLocalPos = 0;
            _responsePosition = 0;
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
            if (RequestPosition == 0 && _transport2HttpHandler.ShouldSend100Continue)
            {
                _transport2HttpHandler.Send100Continue();
            }
            if ((uint)count > _transport2HttpHandler.RequestContentLength - RequestPosition)
            {
                count = (int)(_transport2HttpHandler.RequestContentLength - RequestPosition);
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
            _transport2HttpHandler.StartNextReceive();
            return _tcs.Task;
        }

        void ProcessDataInternal()
        {
            if (_transport2HttpHandler.RequestIsChunked)
            {
                ProcessChunkedDataInternal();
                return;
            }
            var len = Math.Min(_asyncCount, _transport2HttpHandler.ReceiveBufferDataLength);
            if (len > 0)
            {
                Array.Copy(_buf, _transport2HttpHandler.StartBufferOffset + _transport2HttpHandler.ReceiveBufferPos, _asyncBuffer, _asyncOffset, len);
                RequestPosition += (ulong)len;
                _transport2HttpHandler.ReceiveBufferPos += len;
                _asyncOffset += len;
                _asyncCount -= len;
                _asyncResult += len;
            }
        }

        void ProcessChunkedDataInternal()
        {
            var encodedDataAvail = _transport2HttpHandler.ReceiveBufferDataLength;
            var encodedDataOfs = _transport2HttpHandler.StartBufferOffset + _transport2HttpHandler.ReceiveBufferPos;
            while (encodedDataAvail > 0)
            {
                var decodedDataAvail = _chunkedDecoder.DataAvailable;
                if (decodedDataAvail < 0)
                {
                    _transport2HttpHandler.RequestContentLength = RequestPosition;
                    _asyncCount = 0;
                    break;
                }
                if (decodedDataAvail == 0)
                {
                    if (_chunkedDecoder.ProcessByte(_buf[encodedDataOfs]))
                    {
                        _transport2HttpHandler.RequestContentLength = RequestPosition;
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
                    RequestPosition += (ulong)decodedDataAvail;
                }
            }
            _transport2HttpHandler.ReceiveBufferPos = encodedDataOfs - _transport2HttpHandler.StartBufferOffset;
        }

        public bool ProcessDataAndShouldReadMore()
        {
            ProcessDataInternal();
            if (_asyncCount == 0)
            {
                _tcs.TrySetResult(_asyncResult);
                return false;
            }
            return true;
        }

        public void ConnectionClosed()
        {
            var tcs = _tcs;
            if (tcs != null)
            {
                tcs.TrySetCanceled();
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
            get { return (long)_transport2HttpHandler.RequestContentLength; }
        }

        public override long Position
        {
            get { throw new InvalidOperationException(); }
            set { throw new InvalidOperationException(); }
        }

        public ulong ResponseLength
        {
            get { return _responsePosition; }
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
            var len = ResponseLocalPos;
            ResponseLocalPos = 0;
            return _transport2HttpHandler.WriteAsync(_buf, ResponseStartOffset, len);
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new InvalidOperationException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (count <= _responseMaxLen - ResponseLocalPos)
            {
                Array.Copy(buffer, offset, _buf, ResponseStartOffset + ResponseLocalPos, count);
                _responsePosition += (ulong)count;
                ResponseLocalPos += count;
                return;
            }
            WriteAsync(buffer, offset, count, CancellationToken.None).Wait();
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (count <= _responseMaxLen - ResponseLocalPos)
            {
                Array.Copy(buffer, offset, _buf, ResponseStartOffset + ResponseLocalPos, count);
                _responsePosition += (ulong)count;
                ResponseLocalPos += count;
                return Task.Delay(0);
            }
            return WriteOverflowAsync(buffer, offset, count);
        }

        async Task WriteOverflowAsync(byte[] buffer, int offset, int count)
        {
            do
            {
                if (ResponseLocalPos == _responseMaxLen)
                {
                    await FlushAsyncCore();
                    if ((count >= _responseMaxLen) && _transport2HttpHandler.CanUseDirectWrite())
                    {
                        _responsePosition += (ulong)count;
                        await _transport2HttpHandler.WriteAsync(buffer, offset, count);
                        return;
                    }
                }
                var tillEnd = _responseMaxLen - ResponseLocalPos;
                if (tillEnd > count) tillEnd = count;
                Array.Copy(buffer, offset, _buf, ResponseStartOffset + ResponseLocalPos, tillEnd);
                _responsePosition += (ulong)tillEnd;
                ResponseLocalPos += tillEnd;
                offset += tillEnd;
                count -= tillEnd;
            } while (count > 0);
        }
    }
}