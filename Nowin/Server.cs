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
        internal static readonly byte[] Status500InternalServerError = Encoding.UTF8.GetBytes("HTTP/1.1 500 Internal Server Error\r\nContent-Length: 0\r\n\r\n");

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
                _layerFactory = new SslTransportFactory(_parameters.Certificate, _layerFactory);
            }
            ListenSocket = new Socket(_parameters.EndPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            ListenSocket.Bind(_parameters.EndPoint);
            ListenSocket.Listen(100);
            var initialConnectionCount = _connectionAllocationStrategy.CalculateNewConnectionCount(0, 0);
            AllocatedConnections = initialConnectionCount;
            _blocks.Add(new ConnectionBlock(this, _layerFactory, initialConnectionCount));
        }

        public int ConnectionCount
        {
            get { return ConnectedCount; }
        }

        public int CurrentMaxConnectionCount
        {
            get { return AllocatedConnections; }
        }

        public void Dispose()
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
    }
}