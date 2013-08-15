using System.Security.Cryptography.X509Certificates;

namespace Nowin
{
    public class SslTransportFactory : ILayerFactory
    {
        readonly X509Certificate _certificate;
        readonly ILayerFactory _next;

        public SslTransportFactory(X509Certificate certificate, ILayerFactory next)
        {
            _certificate = certificate;
            _next = next;
        }

        public int PerConnectionBufferSize
        {
            get { return _next.PerConnectionBufferSize; }
        }

        public int CommonBufferSize
        {
            get { return _next.CommonBufferSize; }
        }

        public void InitCommonBuffer(byte[] buffer, int offset)
        {
            _next.InitCommonBuffer(buffer, offset);
        }

        public ILayerHandler Create(byte[] buffer, int offset, int commonOffset, int handlerId)
        {
            var nextHandler = (ITransportLayerHandler)_next.Create(buffer, offset, commonOffset, handlerId);
            var handler = new SslTransportHandler(nextHandler, _certificate);
            return handler;
        }
    }
}