namespace Nowin
{
    class ConnectionBlock
    {
        readonly SaeaLayerCallback[] _connections;

        internal ConnectionBlock(Server server, ILayerFactory layerFactory, int connectionCount)
        {
            _connections = new SaeaLayerCallback[connectionCount];
            var perConnectionBufferSize = layerFactory.PerConnectionBufferSize;
            var reserveAtEnd = layerFactory.CommonBufferSize;
            var constantsOffset = checked(connectionCount * perConnectionBufferSize);
            var buffer = new byte[checked(constantsOffset + reserveAtEnd)];
            layerFactory.InitCommonBuffer(buffer, constantsOffset);
            for (var i = 0; i < connectionCount; i++)
            {
                var handler = (ITransportLayerHandler)layerFactory.Create(buffer, i * perConnectionBufferSize, constantsOffset, i);
                var callback = new SaeaLayerCallback(handler, server.ListenSocket, server, i, server.ContextFlow);
                _connections[i] = callback;
                handler.PrepareAccept();
            }
        }

        internal void Stop()
        {
            if (_connections == null) return;
            foreach (var connection in _connections)
            {
                connection?.Dispose();
            }
        }
    }
}