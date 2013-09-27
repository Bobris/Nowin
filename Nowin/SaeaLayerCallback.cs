using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace Nowin
{
    public class SaeaLayerCallback : ITransportLayerCallback, IDisposable
    {
        [Flags]
        enum State
        {
            Receive = 1,
            Send = 2,
            Disconnect = 4,
            Aborting = 8
        }
        readonly ITransportLayerHandler _handler;
        readonly Socket _listenSocket;
        readonly Server _server;
        readonly int _handlerId;
        readonly SocketAsyncEventArgs _receiveEvent = new SocketAsyncEventArgs();
        readonly SocketAsyncEventArgs _sendEvent = new SocketAsyncEventArgs();
        readonly SocketAsyncEventArgs _disconnectEvent = new SocketAsyncEventArgs();
        Socket _socket;
#pragma warning disable 420
        volatile int _state;

        public SaeaLayerCallback(ITransportLayerHandler handler, Socket listenSocket, Server server, int handlerId)
        {
            _handler = handler;
            _listenSocket = listenSocket;
            _server = server;
            _handlerId = handlerId;
            _receiveEvent.Completed += IoCompleted;
            _sendEvent.Completed += IoCompleted;
            _disconnectEvent.Completed += IoCompleted;
            _receiveEvent.DisconnectReuseSocket = true;
            _sendEvent.DisconnectReuseSocket = true;
            _disconnectEvent.DisconnectReuseSocket = true;
            _receiveEvent.UserToken = this;
            _sendEvent.UserToken = this;
            _disconnectEvent.UserToken = this;
            handler.Callback = this;
        }

        static void IoCompleted(object sender, SocketAsyncEventArgs e)
        {
            var self = (SaeaLayerCallback)e.UserToken;
            TraceSources.CoreDebug.TraceInformation("ID{0,-5} IoCompleted {1} {2} {3} {4}", self._handlerId, e.LastOperation, e.Offset, e.BytesTransferred, e.SocketError);
            switch (e.LastOperation)
            {
                case SocketAsyncOperation.Accept:
                    Debug.Assert(e == self._receiveEvent);
                    if (e.SocketError != SocketError.Success)
                    {
                        return;
                    }
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
                    Debug.Assert(e == self._disconnectEvent);
                    self.ProcessDisconnect();
                    break;
                default:
                    throw new ArgumentException("The last operation completed on the socket was not expected");
            }
        }

        void ProcessAccept()
        {
            int oldState, newState;
            do
            {
                oldState = _state;
                newState = oldState & ~(int)State.Receive;
            } while (Interlocked.CompareExchange(ref _state, newState, oldState) != oldState);
            _server.ReportNewConnectedClient();
            _socket = _receiveEvent.AcceptSocket;
            if (_receiveEvent.BytesTransferred >= 0 && _receiveEvent.SocketError == SocketError.Success)
            {
                _handler.FinishAccept(_receiveEvent.Buffer, _receiveEvent.Offset, _receiveEvent.BytesTransferred, _socket.RemoteEndPoint as IPEndPoint, _socket.LocalEndPoint as IPEndPoint);
            }
        }

        void ProcessReceive()
        {
            var bytesTransferred = _receiveEvent.BytesTransferred;
            if (bytesTransferred > 0 && _receiveEvent.SocketError == SocketError.Success)
            {
                int oldState, newState;
                do
                {
                    oldState = _state;
                    newState = oldState & ~(int)State.Receive;
                } while (Interlocked.CompareExchange(ref _state, newState, oldState) != oldState);
                _handler.FinishReceive(_receiveEvent.Buffer, _receiveEvent.Offset, bytesTransferred);
            }
            else
            {
                int oldState, newState;
                do
                {
                    oldState = _state;
                    newState = (oldState & ~(int)State.Receive) | (int)State.Aborting;
                } while (Interlocked.CompareExchange(ref _state, newState, oldState) != oldState);
                _handler.FinishReceive(null, 0, -1);
            }
        }

        void ProcessSend()
        {
            int oldState, newState;
            do
            {
                oldState = _state;
                newState = oldState & ~(int)State.Send;
            } while (Interlocked.CompareExchange(ref _state, newState, oldState) != oldState);
            Exception ex = null;
            if (_sendEvent.SocketError != SocketError.Success)
            {
                ex = new IOException();
            }
            _handler.FinishSend(ex);
        }

        void ProcessDisconnect()
        {
            int oldState, newState;
            do
            {
                oldState = _state;
                newState = (oldState & ~(int)(State.Disconnect | State.Aborting));
            } while (Interlocked.CompareExchange(ref _state, newState, oldState) != oldState);
            _socket = null;
            _server.ReportDisconnectedClient();
            _handler.PrepareAccept();
        }

        public void StartAccept(byte[] buffer, int offset, int length)
        {
            TraceSources.CoreDebug.TraceInformation("ID{0,-5} start accept {1} {2}", _handlerId, offset, length);
            int oldState, newState;
            do
            {
                oldState = _state;
                if ((oldState & (int)State.Receive) != 0)
                    throw new InvalidOperationException("Already receiving or accepting");
                newState = oldState | (int)State.Receive;
            } while (Interlocked.CompareExchange(ref _state, newState, oldState) != oldState);
            _receiveEvent.SetBuffer(buffer, offset, length);
            bool willRaiseEvent;
            try
            {
                StopExecutionContextFlow();
                willRaiseEvent = _listenSocket.AcceptAsync(_receiveEvent);
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            if (!willRaiseEvent)
            {
                var e = _receiveEvent;
                TraceSources.CoreDebug.TraceInformation("ID{0,-5} Sync Accept {1} {2} {3} {4}", _handlerId, e.LastOperation, e.Offset, e.BytesTransferred, e.SocketError);
                ProcessAccept();
            }
        }

        void StopExecutionContextFlow()
        {
            if (!ExecutionContext.IsFlowSuppressed())
                ExecutionContext.SuppressFlow();
        }

        public void StartReceive(byte[] buffer, int offset, int length)
        {
            TraceSources.CoreDebug.TraceInformation("ID{0,-5} start receive {1} {2}", _handlerId, offset, length);
            int oldState, newState;
            do
            {
                oldState = _state;
                if ((oldState & (int)State.Receive) != 0)
                    throw new InvalidOperationException("Already receiving or accepting");
                newState = oldState | (int)State.Receive;
            } while (Interlocked.CompareExchange(ref _state, newState, oldState) != oldState);
            _receiveEvent.SetBuffer(buffer, offset, length);
            bool willRaiseEvent;
            try
            {
                StopExecutionContextFlow();
                willRaiseEvent = _socket.ReceiveAsync(_receiveEvent);
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            if (!willRaiseEvent)
            {
                var e = _receiveEvent;
                TraceSources.CoreDebug.TraceInformation("ID{0,-5} Sync Receive {1} {2} {3} {4}", _handlerId, e.LastOperation, e.Offset, e.BytesTransferred, e.SocketError);
                ProcessReceive();
            }
        }

        public void StartSend(byte[] buffer, int offset, int length)
        {
            TraceSources.CoreDebug.TraceInformation("ID{0,-5} start send {1} {2}", _handlerId, offset, length);
            int oldState, newState;
            do
            {
                oldState = _state;
                if ((oldState & (int)State.Send) != 0)
                    throw new InvalidOperationException("Already sending");
                newState = oldState | (int)State.Send;
            } while (Interlocked.CompareExchange(ref _state, newState, oldState) != oldState);
            _sendEvent.SetBuffer(buffer, offset, length);
            bool willRaiseEvent;
            try
            {
                StopExecutionContextFlow();
                willRaiseEvent = _socket.SendAsync(_sendEvent);
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            if (!willRaiseEvent)
            {
                var e = _sendEvent;
                TraceSources.CoreDebug.TraceInformation("ID{0,-5} Sync Send {1} {2} {3} {4}", _handlerId, e.LastOperation, e.Offset, e.BytesTransferred, e.SocketError);
                ProcessSend();
            }
        }

        public void StartDisconnect()
        {
            TraceSources.CoreDebug.TraceInformation("ID{0,-5} start disconnect", _handlerId);
            int oldState, newState;
            do
            {
                oldState = _state;
                if ((oldState & (int)State.Disconnect) != 0)
                    throw new InvalidOperationException("Already disconnecting");
                newState = oldState | (int)State.Disconnect;
            } while (Interlocked.CompareExchange(ref _state, newState, oldState) != oldState);
            bool willRaiseEvent;
            try
            {
                StopExecutionContextFlow();
                willRaiseEvent = _socket.DisconnectAsync(_disconnectEvent);
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            if (!willRaiseEvent)
            {
                var e = _disconnectEvent;
                TraceSources.CoreDebug.TraceInformation("ID{0,-5} Sync Disconnect {1} {2} {3} {4}", _handlerId, e.LastOperation, e.Offset, e.BytesTransferred, e.SocketError);
                ProcessDisconnect();
            }
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