using System;

namespace NowinWebServer
{
    public class Transport2Http2OwinFactory : ILayerFactory
    {
        readonly int _receiveBufferSize;

        public Transport2Http2OwinFactory(int receiveBufferSize)
        {
            _receiveBufferSize = receiveBufferSize;
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

        public ILayerHandler Create(Server server, byte[] buffer, int offset, int commonOffset)
        {
            return new Transport2Http2OwinHandler(server, buffer, offset, _receiveBufferSize, commonOffset);
        }
    }
}