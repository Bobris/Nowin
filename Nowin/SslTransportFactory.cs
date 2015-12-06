namespace Nowin
{
    public class SslTransportFactory : ILayerFactory
    {
        readonly IServerParameters _serverParameters;
        readonly ILayerFactory _next;

        internal SslTransportFactory(IServerParameters serverParameters, ILayerFactory next)
        {
            _serverParameters = serverParameters;
            _next = next;
        }

        public int PerConnectionBufferSize => _next.PerConnectionBufferSize;

        public int CommonBufferSize => _next.CommonBufferSize;

        public void InitCommonBuffer(byte[] buffer, int offset)
        {
            _next.InitCommonBuffer(buffer, offset);
        }

        public ILayerHandler Create(byte[] buffer, int offset, int commonOffset, int handlerId)
        {
            var nextHandler = (ITransportLayerHandler)_next.Create(buffer, offset, commonOffset, handlerId);
            var handler = new SslTransportHandler(nextHandler, _serverParameters);
            return handler;
        }
    }
}