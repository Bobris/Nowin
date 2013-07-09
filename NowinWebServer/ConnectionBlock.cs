using System;
using System.Net.Sockets;

namespace NowinWebServer
{
    internal class ConnectionBlock
    {
        readonly Server _server;
        readonly int _connectionCount;
        readonly ConnectionInfo[] _connections;

        internal ConnectionBlock(Server server, int connectionCount)
        {
            _server = server;
            _connectionCount = connectionCount;
            _connections = new ConnectionInfo[_connectionCount];
            var perConnectionBufferSize = _server.PerConnectionBufferSize;
            var reserveAtEnd = Server.Status100Continue.Length;
            var constantsOffset = checked(_connectionCount * perConnectionBufferSize);
            var buffer = new byte[checked(constantsOffset + reserveAtEnd)];
            Array.Copy(Server.Status100Continue, 0, buffer, constantsOffset, Server.Status100Continue.Length);
            for (var i = 0; i < _connectionCount; i++)
            {
                var receiveEvent = new SocketAsyncEventArgs();
                var sendEvent = new SocketAsyncEventArgs();
                receiveEvent.Completed += Server.IoCompleted;
                sendEvent.Completed += Server.IoCompleted;
                receiveEvent.SetBuffer(buffer, 0, 0);
                sendEvent.SetBuffer(buffer, 0, 0);
                receiveEvent.DisconnectReuseSocket = true;
                sendEvent.DisconnectReuseSocket = true;
                var token = new ConnectionInfo(_server, i * perConnectionBufferSize, constantsOffset, receiveEvent, sendEvent);
                _connections[i] = token;
                receiveEvent.UserToken = token;
                sendEvent.UserToken = token;
                token.StartAccept();
            }
        }

        internal void Stop()
        {
            if (_connections == null) return;
            foreach (var connection in _connections)
            {
                if (connection == null) continue;
                var s = connection.Socket;
                if (s != null) s.Dispose();
            }
        }
    }
}