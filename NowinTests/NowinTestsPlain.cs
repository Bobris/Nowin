using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using NUnit.Framework;
using Nowin;

namespace NowinTests
{
    [TestFixture]
    public class NowinTestsPlain : NowinTestsBase
    {
        protected override string HttpClientAddress
        {
            get { return "http://localhost:8080/"; }
        }

        protected override string ExpectedRequestScheme
        {
            get { return "http"; }
        }

        protected override IDisposable CreateServer(Func<IDictionary<string, object>, Task> app)
        {
            var server = ServerBuilder.New()
                .SetEndPoint(new IPEndPoint(IPAddress.Loopback, 8080))
                .SetOwinApp(app)
                .SetConnectionAllocationStrategy(new ConnectionAllocationStrategy(1, 0, 1, 0))
                .Start();
            return server;
        }
    }
}