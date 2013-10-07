using System;
using System.Threading;

namespace Nowin
{
    public class Transport2HttpFactory : ILayerFactory
    {
        internal const int AdditionalSpace = 16;
        readonly int _receiveBufferSize;
        readonly bool _isSsl;
        readonly string _serverName;
        readonly IIpIsLocalChecker _ipIsLocalChecker;
        readonly ILayerFactory _next;
        readonly ThreadLocal<char[]> _charBuffer;
        static readonly IDateHeaderValueProvider DateProvider = new DateHeaderValueProvider();

        public Transport2HttpFactory(int receiveBufferSize, bool isSsl, string serverName, IIpIsLocalChecker ipIsLocalChecker, ILayerFactory next)
        {
            _receiveBufferSize = receiveBufferSize;
            _isSsl = isSsl;
            _serverName = serverName;
            _ipIsLocalChecker = ipIsLocalChecker;
            _next = next;
            _charBuffer = new ThreadLocal<char[]>(()=>new char[receiveBufferSize]);
            PerConnectionBufferSize = MyPerConnectionBufferSize() + _next.PerConnectionBufferSize;
        }

        int MyPerConnectionBufferSize()
        {
            return _receiveBufferSize * 3 + AdditionalSpace;
        }

        public int PerConnectionBufferSize { get; private set; }

        public int CommonBufferSize
        {
            get { return MyCommonBufferSize() + _next.CommonBufferSize; }
        }

        static int MyCommonBufferSize()
        {
            return Server.Status100Continue.Length;
        }

        public void InitCommonBuffer(byte[] buffer, int offset)
        {
            Array.Copy(Server.Status100Continue, 0, buffer, offset, MyCommonBufferSize());
            _next.InitCommonBuffer(buffer, offset + MyCommonBufferSize());
        }

        public ILayerHandler Create(byte[] buffer, int offset, int commonOffset, int handlerId)
        {
            var nextHandler = (IHttpLayerHandler)_next.Create(buffer, offset + MyPerConnectionBufferSize(), commonOffset + MyCommonBufferSize(), handlerId);
            return new Transport2HttpHandler(nextHandler, _isSsl, _serverName, DateProvider, _ipIsLocalChecker, buffer, offset, _receiveBufferSize, commonOffset, _charBuffer, handlerId);
        }
    }
}