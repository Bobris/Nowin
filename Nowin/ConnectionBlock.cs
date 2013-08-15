namespace Nowin
{
    internal class ConnectionBlock
    {
        readonly Server _server;
        readonly int _connectionCount;
        readonly SaeaLayerCallback[] _connections;

        internal ConnectionBlock(Server server, ILayerFactory layerFactory, int connectionCount)
        {
            _server = server;
            _connectionCount = connectionCount;
            _connections = new SaeaLayerCallback[_connectionCount];
            var perConnectionBufferSize = layerFactory.PerConnectionBufferSize;
            var reserveAtEnd = layerFactory.CommonBufferSize;
            var constantsOffset = checked(_connectionCount * perConnectionBufferSize);
            var buffer = new byte[checked(constantsOffset + reserveAtEnd)];
            layerFactory.InitCommonBuffer(buffer, constantsOffset);
            for (var i = 0; i < _connectionCount; i++)
            {
                var handler = (ITransportLayerHandler)layerFactory.Create(buffer, i * perConnectionBufferSize, constantsOffset, i);
                var callback = new SaeaLayerCallback(handler, _server.ListenSocket, _server, i);
                _connections[i] = callback;
                handler.PrepareAccept();
            }
        }

        internal void Stop()
        {
            if (_connections == null) return;
            foreach (var connection in _connections)
            {
                if (connection == null) continue;
                connection.Dispose();
            }
        }
    }
}