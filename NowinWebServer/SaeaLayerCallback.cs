using System;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;

namespace NowinWebServer
{
    public class SaeaLayerCallback : ITransportLayerCallback, IDisposable
    {
        readonly ITransportLayerHandler _handler;
        readonly Socket _listenSocket;
        readonly Server _server;
        readonly SocketAsyncEventArgs _receiveEvent = new SocketAsyncEventArgs();
        readonly SocketAsyncEventArgs _sendEvent = new SocketAsyncEventArgs();
        Socket _socket;

        public SaeaLayerCallback(ITransportLayerHandler handler, byte[] buffer, Socket listenSocket, Server server)
        {
            _handler = handler;
            _listenSocket = listenSocket;
            _server = server;
            _receiveEvent.Completed += IoCompleted;
            _sendEvent.Completed += IoCompleted;
            _receiveEvent.SetBuffer(buffer, 0, 0);
            _sendEvent.SetBuffer(buffer, 0, 0);
            _receiveEvent.DisconnectReuseSocket = true;
            _sendEvent.DisconnectReuseSocket = true;
            _receiveEvent.UserToken = this;
            _sendEvent.UserToken = this;
            handler.Callback = this;
        }

        static void IoCompleted(object sender, SocketAsyncEventArgs e)
        {
            var self = (SaeaLayerCallback)e.UserToken;
            switch (e.LastOperation)
            {
                case SocketAsyncOperation.Accept:
                    Debug.Assert(e == self._receiveEvent);
                    if (e.SocketError == SocketError.OperationAborted) return;
                    self.ProcessAccept();
                    break;
                case SocketAsyncOperation.Receive:
                    Debug.Assert(e == self._receiveEvent);
                    self.ProcessReceive();
                    break;
                case SocketAsyncOperation.Send:
                    Debug.Assert(e == self._sendEvent);
                    self.ProcessSend();
                    break;
                case SocketAsyncOperation.Disconnect:
                    self.ProcessDisconnect();
                    break;
                default:
                    throw new ArgumentException("The last operation completed on the socket was not expected");
            }
        }

        void ProcessAccept()
        {
            _server.ReportNewConnectedClient();
            _socket = _receiveEvent.AcceptSocket;
            _receiveEvent.AcceptSocket = null;
            if (_receiveEvent.BytesTransferred > 0 && _receiveEvent.SocketError == SocketError.Success)
            {
                _handler.FinishAccept(_receiveEvent.Offset, _receiveEvent.BytesTransferred);
            }
        }

        void ProcessReceive()
        {
            if (_receiveEvent.BytesTransferred > 0 && _receiveEvent.SocketError == SocketError.Success)
            {
                _handler.FinishReceive(_receiveEvent.Offset, _receiveEvent.BytesTransferred);
            }
            else
            {
                _handler.StartAbort();
            }
        }

        void ProcessSend()
        {
            Exception ex = null;
            if (_sendEvent.SocketError != SocketError.Success)
            {
                ex = new IOException();
            }
            _handler.FinishSend(ex);
        }

        void ProcessDisconnect()
        {
            _socket = null;
            _server.ReportDisconnectedClient();
            _handler.PrepareAccept();
        }

        public void StartAccept(int offset, int length)
        {
            _receiveEvent.SetBuffer(offset, length);
            bool willRaiseEvent;
            try
            {
                willRaiseEvent = _listenSocket.AcceptAsync(_receiveEvent);
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            if (!willRaiseEvent)
            {
                ProcessAccept();
            }
        }

        public void StartReceive(int offset, int length)
        {
            _receiveEvent.SetBuffer(offset, length);
            bool willRaiseEvent;
            try
            {
                willRaiseEvent = _socket.ReceiveAsync(_receiveEvent);
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            if (!willRaiseEvent)
            {
                ProcessReceive();
            }
        }

        public void StartSend(int offset, int length)
        {
            _sendEvent.SetBuffer(offset, length);
            bool willRaiseEvent;
            try
            {
                willRaiseEvent = _socket.SendAsync(_sendEvent);
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            if (!willRaiseEvent)
            {
                ProcessSend();
            }
        }

        public void StartDisconnect()
        {
            bool willRaiseEvent;
            try
            {
                willRaiseEvent = _socket.DisconnectAsync(_sendEvent);
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            if (!willRaiseEvent)
            {
                ProcessDisconnect();
            }
        }

        public void FinishAbort()
        {
        }

        public void Dispose()
        {
            var s = _socket;
            if (s != null)
            {
                s.Dispose();
            }
        }
    }
}