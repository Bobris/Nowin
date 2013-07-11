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

        readonly IConnectionAllocationStrategy _connectionAllocationStrategy;
        internal readonly int ReceiveBufferSize;
        internal readonly int PerConnectionBufferSize;

        readonly ConcurrentBag<ConnectionBlock> _blocks = new ConcurrentBag<ConnectionBlock>();
        internal Socket ListenSocket;
        internal Func<IDictionary<string, object>, Task> App;
        internal int AllocatedConnections;
        internal int ConnectedCount;
        readonly object _newConnectionLock = new object();

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
            ReceiveBufferSize = receiveBufferSize;
            PerConnectionBufferSize = ReceiveBufferSize * 3 + 16;
        }

        public void Start(IPEndPoint localEndPoint, Func<IDictionary<string, object>, Task> app)
        {
            App = app;
            ListenSocket = new Socket(localEndPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            ListenSocket.Bind(localEndPoint);
            ListenSocket.Listen(100);
            var initialConnectionCount = _connectionAllocationStrategy.CalculateNewConnectionCount(0, 0);
            AllocatedConnections = initialConnectionCount;
            _blocks.Add(new ConnectionBlock(this, initialConnectionCount));
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
                        _blocks.Add(new ConnectionBlock(this, delta));
                    }
                });
        }

        internal void ReportDisconnectedClient()
        {
            Interlocked.Decrement(ref ConnectedCount);
        }

        public void Stop()
        {
            ListenSocket.Close();
            foreach (var block in _blocks)
            {
                block.Stop();
            }
        }

        internal static void IoCompleted(object sender, SocketAsyncEventArgs e)
        {
            switch (e.LastOperation)
            {
                case SocketAsyncOperation.Accept:
                    ProcessAccept(e);
                    break;
                case SocketAsyncOperation.Receive:
                    ProcessReceive(e);
                    break;
                case SocketAsyncOperation.Send:
                    ProcessSend(e);
                    break;
                case SocketAsyncOperation.Disconnect:
                    ProcessDisconnect(e);
                    break;
                default:
                    throw new ArgumentException("The last operation completed on the socket was not expected");
            }
        }

        static void ProcessAccept(SocketAsyncEventArgs e)
        {
            if (e.SocketError == SocketError.OperationAborted) return;
            var token = (ConnectionInfo)e.UserToken;
            token.ProcessAccept();
        }

        static void ProcessReceive(SocketAsyncEventArgs e)
        {
            var token = (ConnectionInfo)e.UserToken;
            while (token.ProcessReceive())
            {
            }
        }

        static void ProcessSend(SocketAsyncEventArgs e)
        {
            var token = (ConnectionInfo)e.UserToken;
            token.ProcessSend();
        }

        static void ProcessDisconnect(SocketAsyncEventArgs e)
        {
            var token = (ConnectionInfo)e.UserToken;
            token.ProcessDisconnect(e);
        }

        public void Dispose()
        {
            Stop();
        }
    }
}