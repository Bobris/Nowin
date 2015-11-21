using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using Nowin;
using Xunit;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace NowinTests
{
    public class NowinTestsPlain : NowinTestsBase
    {
        int Port => 8082;

        protected override string HttpClientAddress => "http://localhost:8082/";

        protected override string ExpectedRequestScheme => "http";

        protected override IDisposable CreateServer(Func<IDictionary<string, object>, Task> app)
        {
            var server = ServerBuilder.New()
                .SetEndPoint(new IPEndPoint(IPAddress.Loopback, 8082))
                .SetOwinApp(app)
                .SetConnectionAllocationStrategy(new ConnectionAllocationStrategy(1, 0, 1, 0))
                .SetRetrySocketBindingTime(TimeSpan.FromSeconds(4))
                .Start();
            return server;
        }

        [Fact]
        public void ClosingClientConnectionDoesCancelAsyncServer()
        {
            var callCancelled = false;
            var finished = false;
            using (CreateServer(
                async env =>
                {
                    GetCallCancelled(env).Register(() => callCancelled = true);
                    try
                    {
                        await Task.Delay(2000, GetCallCancelled(env));
                    }
                    finally
                    {
                        finished = true;
                    }
                }))
            {
                using (var client = new TcpClient())
                {
                    client.Connect(new IPEndPoint(IPAddress.Parse("127.0.0.1"), Port));
                    using (var connStream = client.GetStream())
                    {
                        var request = Encoding.UTF8.GetBytes("GET / HTTP/1.1\r\n"
                                                             + "Host: localhost:8080\r\n"
                                                             + "\r\n");
                        connStream.Write(request, 0, request.Length);
                        connStream.Flush();
                    }
                }
                Thread.Sleep(100);
                Assert.True(callCancelled);
                Assert.True(finished);
            }
        }

        [Fact]
        public void ClosingClientConnectionDoesCancelSyncServer()
        {
            var callCancelled = false;
            using (CreateServer(
                async env =>
                {
                    GetCallCancelled(env).Register(() => callCancelled = true);
                    while (!callCancelled)
                    {
                        Thread.Yield();
                    }
                    await Task.Delay(0);
                }))
            {
                using (var client = new TcpClient())
                {
                    client.Connect(new IPEndPoint(IPAddress.Parse("127.0.0.1"), Port));
                    using (var connStream = client.GetStream())
                    {
                        var request = Encoding.UTF8.GetBytes("GET / HTTP/1.1\r\n"
                                                             + "Host: localhost:8080\r\n"
                                                             + "\r\n");
                        connStream.Write(request, 0, request.Length);
                        connStream.Flush();
                    }
                }
                Thread.Sleep(100);
                Assert.True(callCancelled);
            }
        }
    }
}