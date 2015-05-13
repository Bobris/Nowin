using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace Nowin
{
    public class SslTransportFactory : ILayerFactory
    {
        readonly X509Certificate _certificate;
        readonly ILayerFactory _next;
        readonly SslProtocols _protocols;

        public SslTransportFactory(X509Certificate certificate, SslProtocols protocols, ILayerFactory next)
        {
            _certificate = certificate;
            _protocols = protocols;
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
            var handler = new SslTransportHandler(nextHandler, _certificate, _protocols);
            return handler;
        }
    }
}