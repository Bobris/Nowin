using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace NowinWebServer
{
    public class Transport2Http2OwinFactory : ILayerFactory
    {
        readonly int _receiveBufferSize;
        readonly Func<IDictionary<string, object>, Task> _app;

        public Transport2Http2OwinFactory(int receiveBufferSize, Func<IDictionary<string, object>, Task> app)
        {
            _receiveBufferSize = receiveBufferSize;
            _app = app;
            PerConnectionBufferSize = receiveBufferSize * 3 + 16;
        }

        public int PerConnectionBufferSize { get; private set; }

        public int CommonBufferSize
        {
            get { return Server.Status100Continue.Length; }
        }

        public void InitCommonBuffer(byte[] buffer, int offset)
        {
            Array.Copy(Server.Status100Continue, 0, buffer, offset, Server.Status100Continue.Length);
        }

        public ILayerHandler Create(byte[] buffer, int offset, int commonOffset)
        {
            return new Transport2Http2OwinHandler(_app, buffer, offset, _receiveBufferSize, commonOffset);
        }
    }
}