using System;
using System.Collections.Generic;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;

namespace Nowin
{
    public class ServerBuilder : IServerParameters
    {
        IConnectionAllocationStrategy _connectionAllocationStrategy;
        IPEndPoint _endPoint;
        X509Certificate _certificate;
        int _bufferSize;
        Func<IDictionary<string, object>, Task> _app;
        IDictionary<string, object> _capabilities;
        string _serverHeader = "Nowin";
        private ExecutionContextFlow _contextFlow  = ExecutionContextFlow.SuppressAlways;
        private TimeSpan _retrySocketBindingTime;

        public static ServerBuilder New()
        {
            return new ServerBuilder();
        }

        public ServerBuilder SetConnectionAllocationStrategy(IConnectionAllocationStrategy strategy)
        {
            _connectionAllocationStrategy = strategy;
            return this;
        }

        public ServerBuilder SetRetrySocketBindingTime(TimeSpan value)
        {
            _retrySocketBindingTime = value;
            return this;
        }
        public ServerBuilder SetPort(int port)
        {
            if (port < IPEndPoint.MinPort || port > IPEndPoint.MaxPort) throw new ArgumentOutOfRangeException("port", port, "must be in range of <0,65535>");
            InitEndPointIfNullByDefault();
            _endPoint.Port = port;
            return this;
        }

        void InitEndPointIfNullByDefault()
        {
            if (_endPoint == null) _endPoint = new IPEndPoint(IPAddress.Any, _certificate != null ? 443 : 8080);
        }

        public ServerBuilder SetAddress(IPAddress address)
        {
            InitEndPointIfNullByDefault();
            _endPoint.Address = address;
            return this;
        }

        public ServerBuilder SetEndPoint(IPEndPoint endPoint)
        {
            _endPoint = endPoint;
            return this;
        }

        public ServerBuilder SetCertificate(X509Certificate certificate)
        {
            _certificate = certificate;
            return this;
        }

        public ServerBuilder SetBufferSize(int size)
        {
            if (size < 1024 || size > 65536) throw new ArgumentOutOfRangeException("size", size, "Must be in range <1024,65536>");
            _bufferSize = size;
            return this;
        }

        public ServerBuilder SetOwinApp(Func<IDictionary<string, object>, Task> app)
        {
            _app = app;
            return this;
        }

        public ServerBuilder SetOwinCapabilities(IDictionary<string, object> capabilities)
        {
            _capabilities = capabilities;
            return this;
        }

        public ServerBuilder SetExecutionContextFlow(ExecutionContextFlow flow)
        {
            _contextFlow = flow;
            return this;
        }

        public ServerBuilder SetServerHeader(string value)
        {
            _serverHeader = value;
            return this;
        }

        public INowinServer Build()
        {
            return new Server(this);
        }

        public IDisposable Start()
        {
            var s = new Server(this);
            s.Start();
            return s;
        }

        public ExecutionContextFlow ContextFlow
        {
            get { return _contextFlow; }
        }

        IConnectionAllocationStrategy IServerParameters.ConnectionAllocationStrategy
        {
            get
            {
                return _connectionAllocationStrategy ??
                       (_connectionAllocationStrategy = new ConnectionAllocationStrategy(256, 256, 1024 * 1024, 32));
            }
        }

        IPEndPoint IServerParameters.EndPoint
        {
            get
            {
                InitEndPointIfNullByDefault();
                return _endPoint;
            }
        }

        X509Certificate IServerParameters.Certificate
        {
            get { return _certificate; }
        }

        int IServerParameters.BufferSize
        {
            get
            {
                if (_bufferSize < 1024) _bufferSize = 8192;
                return _bufferSize;
            }
        }

        Func<IDictionary<string, object>, Task> IServerParameters.OwinApp
        {
            get { return _app; }
        }

        IDictionary<string, object> IServerParameters.OwinCapabilities
        {
            get { return _capabilities; }
        }

        public string ServerHeader { get { return _serverHeader; }}

        public TimeSpan RetrySocketBindingTime
        {
            get
            {
                return _retrySocketBindingTime;
                throw new NotImplementedException();
            }
        }
    }
}