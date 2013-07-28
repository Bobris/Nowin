using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace NowinWebServer
{
    public class OwinHandlerFactory : ILayerFactory
    {
        readonly Func<IDictionary<string, object>, Task> _app;

        public OwinHandlerFactory(Func<IDictionary<string, object>, Task> app)
        {
            _app = app;
        }

        public int PerConnectionBufferSize { get { return 0; } }
        public int CommonBufferSize { get { return 0; } }
        public void InitCommonBuffer(byte[] buffer, int offset)
        {
        }

        public ILayerHandler Create(byte[] buffer, int offset, int commonOffset)
        {
            return new OwinHandler(_app);
        }
    }
}