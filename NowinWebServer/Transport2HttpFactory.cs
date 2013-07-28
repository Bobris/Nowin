using System;

namespace NowinWebServer
{
    public class Transport2HttpFactory : ILayerFactory
    {
        readonly int _receiveBufferSize;
        readonly bool _isSsl;
        readonly IIpIsLocalChecker _ipIsLocalChecker;
        readonly ILayerFactory _next;

        public Transport2HttpFactory(int receiveBufferSize, bool isSsl, IIpIsLocalChecker ipIsLocalChecker, ILayerFactory next)
        {
            _receiveBufferSize = receiveBufferSize;
            _isSsl = isSsl;
            _ipIsLocalChecker = ipIsLocalChecker;
            _next = next;
            PerConnectionBufferSize = MyPerConnectionBufferSize() + _next.PerConnectionBufferSize;
        }

        int MyPerConnectionBufferSize()
        {
            return _receiveBufferSize * 3 + 16;
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

        public ILayerHandler Create(byte[] buffer, int offset, int commonOffset)
        {
            var nextHandler = (IHttpLayerHandler)_next.Create(buffer, offset + MyPerConnectionBufferSize(), commonOffset + MyCommonBufferSize());
            return new Transport2HttpHandler(nextHandler, _isSsl, _ipIsLocalChecker, buffer, offset, _receiveBufferSize, commonOffset);
        }
    }
}