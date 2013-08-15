using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Nowin
{
    public class OwinHandlerFactory : ILayerFactory
    {
        readonly Func<IDictionary<string, object>, Task> _app;
        readonly IDictionary<string, object> _owinCapabilities;

        public OwinHandlerFactory(Func<IDictionary<string, object>, Task> app, IDictionary<string, object> owinCapabilities)
        {
            _app = app;
            _owinCapabilities = owinCapabilities;
        }

        public int PerConnectionBufferSize { get { return 0; } }
        public int CommonBufferSize { get { return 0; } }
        public void InitCommonBuffer(byte[] buffer, int offset)
        {
        }

        public ILayerHandler Create(byte[] buffer, int offset, int commonOffset, int handlerId)
        {
            return new OwinHandler(_app, _owinCapabilities, handlerId);
        }
    }
}