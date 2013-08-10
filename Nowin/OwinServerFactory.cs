using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;

namespace Nowin
{
    public static class OwinServerFactory
    {
        public static void Initialize(IDictionary<string, object> properties)
        {
            if (properties == null)
            {
                throw new ArgumentNullException("properties");
            }

            properties[OwinKeys.Version] = "1.0";

            var capabilities = properties.Get<IDictionary<string, object>>(OwinKeys.ServerCapabilitiesKey)
                               ?? new Dictionary<string, object>();
            properties[OwinKeys.ServerCapabilitiesKey] = capabilities;

            capabilities[OwinKeys.ServerNameKey] = "Nowin";
            capabilities[OwinKeys.WebSocketVersionKey] = OwinKeys.WebSocketVersion;
        }

        public static IDisposable Create(Func<IDictionary<string, object>, Task> app, IDictionary<string, object> properties)
        {
            if (app == null)
            {
                throw new ArgumentNullException("app");
            }

            if (properties == null)
            {
                throw new ArgumentNullException("properties");
            }

            var capabilities = properties.Get<IDictionary<string, object>>(OwinKeys.ServerCapabilitiesKey)
                               ?? new Dictionary<string, object>();

            var addresses = properties.Get<IList<IDictionary<string, object>>>("host.Addresses")
                            ?? new List<IDictionary<string, object>>();

            var servers = new List<INowinServer>();
            var endpoints = new List<IPEndPoint>();
            foreach (var address in addresses)
            {
                var builder = ServerBuilder.New().SetOwinApp(app);
                int port;
                if (!int.TryParse(address.Get<string>("port"), out port)) throw new ArgumentException("port must be number from 0 to 65535");
                builder.SetPort(port);
                builder.SetOwinCapabilities(capabilities);
                var certificate = address.Get<X509Certificate>("certificate");
                if (certificate != null)
                    builder.SetCertificate(certificate);
                servers.Add(builder.Build());
                endpoints.Add(new IPEndPoint(IPAddress.Loopback,port));
            }

            var disposer = new Disposer(servers.Cast<IDisposable>().ToArray());
            try
            {
                foreach (var nowinServer in servers)
                {
                    nowinServer.Start();
                }
                // This is workaround to Windows Server 2012 issue by calling ReadLine after AcceptAsync, by making one bogus connection this problem goes away
                foreach (var ipEndPoint in endpoints)
                {
                    var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.IP);
                    socket.Connect(ipEndPoint);
                    socket.Close();                    
                }
            }
            catch (Exception)
            {
                disposer.Dispose();
                throw;
            }
            return disposer;
        }

        class Disposer : IDisposable
        {
            readonly IDisposable[] _disposables;

            public Disposer(IDisposable[] disposables)
            {
                _disposables = disposables;
            }

            public void Dispose()
            {
                foreach (var disposable in _disposables)
                {
                    disposable.Dispose();
                }
            }
        }
    }
}