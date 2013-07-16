using System.Security.Cryptography.X509Certificates;

namespace NowinWebServer
{
    public class SslTransportFactory : ILayerFactory
    {
        readonly int _bufferSize;
        readonly ILayerFactory _next;

        public SslTransportFactory(int bufferSize, ILayerFactory next)
        {
            _bufferSize = bufferSize;
            _next = next;
        }

        public int PerConnectionBufferSize
        {
            get { return _next.PerConnectionBufferSize + _bufferSize * 2; }
        }

        public int CommonBufferSize
        {
            get { return _next.CommonBufferSize; }
        }

        public void InitCommonBuffer(byte[] buffer, int offset)
        {
            _next.InitCommonBuffer(buffer, offset);
        }

        public ILayerHandler Create(Server server, byte[] buffer, int offset, int commonOffset)
        {
            var nextHandler = (ITransportLayerHandler) _next.Create(server, buffer, offset, commonOffset);
            var handler = new SslTransportHandler(nextHandler, new X509Certificate(), buffer,
                                                  offset + _next.PerConnectionBufferSize, _bufferSize);
            return handler;
        }
    }
}