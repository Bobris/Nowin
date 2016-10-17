using System;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Nowin
{
    public class Server : INowinServer
    {
        internal static readonly byte[] Status100Continue = Encoding.UTF8.GetBytes("HTTP/1.1 100 Continue\r\n\r\n");

        readonly IServerParameters _parameters;

        readonly ConcurrentBag<ConnectionBlock> _blocks = new ConcurrentBag<ConnectionBlock>();
        internal Socket ListenSocket;
        internal int AllocatedConnections;
        internal int ConnectedCount;
        readonly object _newConnectionLock = new object();
        ILayerFactory _layerFactory;
        IConnectionAllocationStrategy _connectionAllocationStrategy;
        IIpIsLocalChecker _ipIsLocalChecker;

        internal Server(IServerParameters parameters)
        {
            _parameters = parameters;
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

        public void Start()
        {
            _layerFactory = new OwinHandlerFactory(_parameters.OwinApp, _parameters.OwinCapabilities);
            _ipIsLocalChecker = new IpIsLocalChecker();
            _connectionAllocationStrategy = _parameters.ConnectionAllocationStrategy;
            var isSsl = _parameters.Certificate != null;
            _layerFactory = new Transport2HttpFactory(_parameters.BufferSize, isSsl, _parameters.ServerHeader, _ipIsLocalChecker, _layerFactory);
            if (isSsl)
            {
                _layerFactory = new SslTransportFactory(_parameters, _layerFactory);
            }

            ListenSocket = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp);
            ListenSocket.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.IPv6Only, false);

            var start = DateTime.UtcNow;
            while (true)
            {
                try
                {
                    ListenSocket.Bind(_parameters.EndPoint);
                    break;
                }
                catch when(start + _parameters.RetrySocketBindingTime > DateTime.UtcNow)
                {
                }
                Thread.Sleep(50);
            }
            ListenSocket.Listen(100);
            var initialConnectionCount = _connectionAllocationStrategy.CalculateNewConnectionCount(0, 0);
            AllocatedConnections = initialConnectionCount;
            _blocks.Add(new ConnectionBlock(this, _layerFactory, initialConnectionCount));
        }

        public int ConnectionCount => ConnectedCount;

        public int CurrentMaxConnectionCount => AllocatedConnections;

        public ExecutionContextFlow ContextFlow => _parameters.ContextFlow;

        public void Dispose()
        {
            lock (_newConnectionLock)
            {
                _connectionAllocationStrategy = new FinishingAllocationStrategy();
            }

            ListenSocket?.Dispose();

            ConnectionBlock block;
            while (_blocks.TryTake(out block))
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
    }
}