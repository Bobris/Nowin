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
        static readonly bool RuntimeCorrectlyImplementsDisconnectReuseSocket;

        static SaeaLayerCallback()
        {
            RuntimeCorrectlyImplementsDisconnectReuseSocket = Type.GetType("Mono.Runtime") == null;
        }

        [Flags]
        enum State
        {
            Receive = 1, // Accept or Receive in process
            Send = 2, // Send in process
            Disconnect = 4, // Disconnect in process
            Aborting = 8, // Disconnect or wait for next StartAccept => Fast Failing And Receive or Send
            DelayedAccept = 16 // Waiting Socket to stop receive after disconnect
        }

        readonly ITransportLayerHandler _handler;
        readonly Socket _listenSocket;
        readonly Server _server;
        readonly int _handlerId;
        SocketAsyncEventArgs _acceptEvent;
        SocketAsyncEventArgs _receiveEvent;
        SocketAsyncEventArgs _sendEvent;
        SocketAsyncEventArgs _disconnectEvent;
        Socket _socket;
#pragma warning disable 420
        volatile int _state;
        readonly Func<IDisposable> _contextSuppresser;

        public SaeaLayerCallback(ITransportLayerHandler handler, Socket listenSocket, Server server, int handlerId, ExecutionContextFlow contextFlow)
        {
            _handler = handler;
            _listenSocket = listenSocket;
            _server = server;
            _handlerId = handlerId;
            _contextSuppresser = ExecutionContextFlowSuppresser.CreateContextSuppresser(contextFlow);
            RecreateSaeas();
            handler.Callback = this;
        }

        void RecreateSaeas()
        {
            DisposeEventArgs();
            _acceptEvent = new SocketAsyncEventArgs();
            _receiveEvent = new SocketAsyncEventArgs();
            _sendEvent = new SocketAsyncEventArgs();
            _disconnectEvent = new SocketAsyncEventArgs();
            _acceptEvent.Completed += IoCompleted;
            _receiveEvent.Completed += IoCompleted;
            _sendEvent.Completed += IoCompleted;
            _disconnectEvent.Completed += IoCompleted;
            _acceptEvent.DisconnectReuseSocket = RuntimeCorrectlyImplementsDisconnectReuseSocket;
            _receiveEvent.DisconnectReuseSocket = RuntimeCorrectlyImplementsDisconnectReuseSocket;
            _sendEvent.DisconnectReuseSocket = RuntimeCorrectlyImplementsDisconnectReuseSocket;
            _disconnectEvent.DisconnectReuseSocket = RuntimeCorrectlyImplementsDisconnectReuseSocket;
            _acceptEvent.UserToken = this;
            _receiveEvent.UserToken = this;
            _sendEvent.UserToken = this;
            _disconnectEvent.UserToken = this;
        }

        void DisposeEventArgs()
        {
            _receiveEvent?.Dispose();
            _sendEvent?.Dispose();
            _disconnectEvent?.Dispose();
        }

        static void IoCompleted(object sender, SocketAsyncEventArgs e)
        {
            var self = (SaeaLayerCallback)e.UserToken;
            TraceSources.CoreDebug.TraceInformation("ID{0,-5} IoCompleted {1} {2} {3} {4}", self._handlerId, e.LastOperation, e.Offset, e.BytesTransferred, e.SocketError);
            switch (e.LastOperation)
            {
                case SocketAsyncOperation.Accept:
                    Debug.Assert(e == self._acceptEvent);
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
            var bytesTransfered = _acceptEvent.BytesTransferred;
            var socketError = _acceptEvent.SocketError;

            if (bytesTransfered >= 0 && socketError == SocketError.Success)
            {
                _socket = _acceptEvent.AcceptSocket;
                IPEndPoint remoteEndpoint = null;
                IPEndPoint localEndpoint = null;
                try
                {
                    remoteEndpoint = _socket.RemoteEndPoint as IPEndPoint;
                    localEndpoint = _socket.LocalEndPoint as IPEndPoint;
                }
                catch (SocketException) //"The socket is not connected" is intentionally ignored
                { }

                if (remoteEndpoint != null && localEndpoint != null)
                {
                    _server.ReportNewConnectedClient();
                    _handler.FinishAccept(_acceptEvent.Buffer, _acceptEvent.Offset, bytesTransfered,
                        remoteEndpoint, localEndpoint);
                    return;
                }
            }

            // Current socket could be corrupted Windows returns InvalidArguments nonsense.
            RecreateSaeas();
            _handler.PrepareAccept();
        }

        void ProcessReceive()
        {
            bool postponedAccept;
            var bytesTransferred = _receiveEvent.BytesTransferred;

            int oldState, newState;
            do
            {
                oldState = _state;
                postponedAccept = (oldState & (int)State.DelayedAccept) != 0;
                newState = oldState & ~(int)(State.Receive | State.DelayedAccept);
            } while (Interlocked.CompareExchange(ref _state, newState, oldState) != oldState);

            if (bytesTransferred > 0 && _receiveEvent.SocketError == SocketError.Success)
            {
                _handler.FinishReceive(_receiveEvent.Buffer, _receiveEvent.Offset, bytesTransferred);
            }
            else
            {
                _handler.FinishReceive(null, 0, -1);
            }
            if (postponedAccept)
                _handler.PrepareAccept();
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
            bool delayedAccept;
            int oldState, newState;
            do
            {
                oldState = _state;
                delayedAccept = (oldState & (int)State.Receive) != 0;
                newState = (oldState & ~(int)State.Disconnect) | (delayedAccept ? (int)State.DelayedAccept : 0);
            } while (Interlocked.CompareExchange(ref _state, newState, oldState) != oldState);
            if (!RuntimeCorrectlyImplementsDisconnectReuseSocket)
            {
                _acceptEvent.AcceptSocket = null;
                _socket.Close();
                _socket.Dispose();
            }
            _socket = null;
            _server.ReportDisconnectedClient();
            if (!delayedAccept)
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
                newState = (oldState | (int)State.Receive) & ~(int)State.Aborting;
            } while (Interlocked.CompareExchange(ref _state, newState, oldState) != oldState);
            bool willRaiseEvent;
            try
            {
                _acceptEvent.SetBuffer(buffer, offset, length);

                using (StopExecutionContextFlow())
                    willRaiseEvent = _listenSocket.AcceptAsync(_acceptEvent);
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            if (!willRaiseEvent)
            {
                var e = _acceptEvent;
                TraceSources.CoreDebug.TraceInformation("ID{0,-5} Sync Accept {1} {2} {3} {4}", _handlerId, e.LastOperation, e.Offset, e.BytesTransferred, e.SocketError);
                ProcessAccept();
            }
        }

        IDisposable StopExecutionContextFlow()
        {
            return _contextSuppresser();
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
                if ((oldState & (int)State.Aborting) != 0)
                {
                    _handler.FinishReceive(null, 0, -1);
                    return;
                }
                newState = oldState | (int)State.Receive;
            } while (Interlocked.CompareExchange(ref _state, newState, oldState) != oldState);
            bool willRaiseEvent;
            try
            {
                _receiveEvent.SetBuffer(buffer, offset, length);
                using (StopExecutionContextFlow())
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
                if ((oldState & (int)State.Aborting) != 0)
                {
                    _handler.FinishSend(new IOException());
                    return;
                }
                newState = oldState | (int)State.Send;
            } while (Interlocked.CompareExchange(ref _state, newState, oldState) != oldState);
            bool willRaiseEvent;
            try
            {
                _sendEvent.SetBuffer(buffer, offset, length);
                using (StopExecutionContextFlow())
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
                newState = oldState | (int)(State.Disconnect | State.Aborting);
            } while (Interlocked.CompareExchange(ref _state, newState, oldState) != oldState);
            bool willRaiseEvent;
            try
            {
                using (StopExecutionContextFlow())
                {
                    var s = _socket;
                    if (s == null)
                        return;
                    willRaiseEvent = s.DisconnectAsync(_disconnectEvent);
                }
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
            DisposeEventArgs();
            _socket?.Dispose();
        }
    }
}
