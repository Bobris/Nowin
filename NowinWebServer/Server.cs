using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NowinWebServer
{
    public class Server : IDisposable
    {
        internal static readonly byte[] Status100Continue = Encoding.UTF8.GetBytes("HTTP/1.1 100 Continue\r\n\r\n");
        internal static readonly byte[] Status500InternalServerError = Encoding.UTF8.GetBytes("HTTP/1.1 500 Internal Server Error\r\nContent-Length: 0\r\n\r\n");

        IConnectionAllocationStrategy _connectionAllocationStrategy;

        readonly ConcurrentBag<ConnectionBlock> _blocks = new ConcurrentBag<ConnectionBlock>();
        internal Socket ListenSocket;
        internal Func<IDictionary<string, object>, Task> App;
        internal int AllocatedConnections;
        internal int ConnectedCount;
        readonly object _newConnectionLock = new object();
        ILayerFactory _layerFactory;

        public Server()
            : this(new ConnectionAllocationStrategy(64, 64, 1024 * 1024, 16))
        {
        }

        public Server(int maxConnections, int receiveBufferSize = 8192)
            : this(new ConnectionAllocationStrategy(maxConnections, 0, maxConnections, 0), receiveBufferSize)
        {
        }

        public Server(IConnectionAllocationStrategy connectionAllocationStrategy, int receiveBufferSize = 8192)
        {
            _connectionAllocationStrategy = connectionAllocationStrategy;
            _layerFactory = new Transport2Http2OwinFactory(receiveBufferSize);
        }

        public void Start(IPEndPoint localEndPoint, Func<IDictionary<string, object>, Task> app)
        {
            App = app;
            ListenSocket = new Socket(localEndPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            ListenSocket.Bind(localEndPoint);
            ListenSocket.Listen(100);
            var initialConnectionCount = _connectionAllocationStrategy.CalculateNewConnectionCount(0, 0);
            AllocatedConnections = initialConnectionCount;
            _blocks.Add(new ConnectionBlock(this, _layerFactory, initialConnectionCount));
        }

        internal void ReportNewConnectedClient()
        {
            var cc = Interlocked.Increment(ref ConnectedCount);
            var add = _connectionAllocationStrategy.CalculateNewConnectionCount(AllocatedConnections, cc);
            if (add <= 0) return;
            Task.Run(() =>
                {
                    lock (_newConnectionLock)
                    {
                        var delta = _connectionAllocationStrategy.CalculateNewConnectionCount(AllocatedConnections, ConnectedCount);
                        if (delta <= 0) return;
                        AllocatedConnections += delta;
                        _blocks.Add(new ConnectionBlock(this, _layerFactory, delta));
                    }
                });
        }

        internal void ReportDisconnectedClient()
        {
            Interlocked.Decrement(ref ConnectedCount);
        }

        public void Stop()
        {
            lock (_newConnectionLock)
            {
                _connectionAllocationStrategy = new FinishingAllocationStrategy();
            }
            ListenSocket.Close();
            foreach (var block in _blocks)
            {
                block.Stop();
            }
        }

        class FinishingAllocationStrategy : IConnectionAllocationStrategy
        {
            public int CalculateNewConnectionCount(int currentCount, int connectedCount)
            {
                return 0;
            }
        }

        public void Dispose()
        {
            Stop();
        }
    }
}