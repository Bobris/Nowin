using System.Security.Cryptography.X509Certificates;

namespace NowinWebServer
{
    public class SslTransportFactory : ILayerFactory
    {
        readonly int _bufferSize;
        readonly X509Certificate _certificate;
        readonly ILayerFactory _next;

        public SslTransportFactory(int bufferSize, X509Certificate certificate, ILayerFactory next)
        {
            _bufferSize = bufferSize;
            _certificate = certificate;
            _next = next;
        }

        public int PerConnectionBufferSize
        {
            get { return _next.PerConnectionBufferSize + _bufferSize * 2 + SslTransportHandler.SendBufferExtendedBySslSize; }
        }

        public int CommonBufferSize
        {
            get { return _next.CommonBufferSize; }
        }

        public void InitCommonBuffer(byte[] buffer, int offset)
        {
            _next.InitCommonBuffer(buffer, offset);
        }

        public ILayerHandler Create(byte[] buffer, int offset, int commonOffset)
        {
            var nextHandler = (ITransportLayerHandler)_next.Create(buffer, offset, commonOffset);
            var handler = new SslTransportHandler(nextHandler, _certificate, buffer,
                                                  offset + _next.PerConnectionBufferSize, _bufferSize);
            return handler;
        }
    }
}