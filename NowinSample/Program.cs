using System;
using System.Collections.Generic;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using Microsoft.Owin.Builder;
using Nowin;

namespace NowinSample
{
    static class Program
    {
        static void Main(string[] args)
        {
            var owinbuilder = new AppBuilder();
            OwinServerFactory.Initialize(owinbuilder.Properties);
            new SampleOwinApp.Startup().Configuration(owinbuilder);
            var builder = ServerBuilder.New().SetPort(8888).SetOwinApp(owinbuilder.Build()).SetOwinCapabilities((IDictionary<string, object>)owinbuilder.Properties[OwinKeys.ServerCapabilitiesKey])
                .SetExecutionContextFlow(ExecutionContextFlow.SuppressAlways);
            //builder.SetCertificate(new X509Certificate2("../../../sslcert/test.pfx", "nowin"));
            var builder2 = ServerBuilder.New().SetPort(9999).SetOwinApp(owinbuilder.Build()).SetOwinCapabilities((IDictionary<string, object>)owinbuilder.Properties[OwinKeys.ServerCapabilitiesKey]);
            using (var server = builder.Build())
            using (var server2 = builder2.Build())
            {
                // Workaround for bug in Windows Server 2012 when ReadLine is called directly after AcceptAsync
                // By starting it in another thread and probably even later than calling readline it works
                Task.Run(() => server.Start());
                Task.Run(() => server2.Start());
                //using (new Timer(o =>
                //    {
                //        var s = (INowinServer)o;
                //        Console.WriteLine("Connections {0}/{1}", s.ConnectionCount, s.CurrentMaxConnectionCount);
                //    }, server, 2000, 2000))
                {
                    Console.WriteLine("Listening on ports 8888 and 9999. Enter to exit.");
                    Console.ReadLine();
                }
            }
        }
    }

}
