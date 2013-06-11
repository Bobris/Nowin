using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace NowinWebServer
{
    public class Server : IDisposable
    {
        static readonly byte[] Status100Continue = Encoding.UTF8.GetBytes("HTTP/1.1 100 Continue\r\n");
        internal static readonly byte[] Status500InternalServerError10 = Encoding.UTF8.GetBytes("HTTP/1.0 500 Internal Server Error\r\n\r\n");
        internal static readonly byte[] Status500InternalServerError11 = Encoding.UTF8.GetBytes("HTTP/1.1 500 Internal Server Error\r\n\r\n");

        readonly int _maxConnections;
        readonly int _receiveBufferSize;
        Socket _listenSocket;
        Func<IDictionary<string, object>, Task> _app;

        public Server(int maxConnections = 1024, int receiveBufferSize = 8192)
        {
            _maxConnections = maxConnections;
            _receiveBufferSize = receiveBufferSize;
        }

        public void Start(IPEndPoint localEndPoint, Func<IDictionary<string, object>, Task> app)
        {
            _app = app;
            _listenSocket = new Socket(localEndPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            _listenSocket.Bind(localEndPoint);
            _listenSocket.Listen(100);
            var reserveAtEnd = Status100Continue.Length;
            var constantsOffset = checked (_maxConnections*_receiveBufferSize*3);
            var buffer = new byte[checked (constantsOffset + reserveAtEnd)];

            for (var i = 0; i < _maxConnections; i++)
            {
                var receiveEvent = new SocketAsyncEventArgs();
                var sendEvent = new SocketAsyncEventArgs();
                receiveEvent.Completed += IoCompleted;
                sendEvent.Completed += IoCompleted;
                receiveEvent.SetBuffer(buffer, 0, 0);
                sendEvent.SetBuffer(buffer, 0, 0);
                receiveEvent.DisconnectReuseSocket = true;
                sendEvent.DisconnectReuseSocket = true;
                var token = new ConnectionInfo(i * _receiveBufferSize * 3, _receiveBufferSize, constantsOffset, receiveEvent, sendEvent, _listenSocket, _app);
                receiveEvent.UserToken = token;
                sendEvent.UserToken = token;
                token.StartAccept();
            }
        }

        public void Stop()
        {
            _listenSocket.Close();
        }

        static void IoCompleted(object sender, SocketAsyncEventArgs e)
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