using System;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using NowinWebServer;

namespace NowinSample
{
    static class Program
    {
        static void Main(string[] args)
        {
            var builder = ServerBuilder.New().SetPort(8888).SetOwinApp(SampleOwinApp.Sample.App);
            //builder.SetCertificate(new X509Certificate2("../../../sslcert/test.pfx", "nowin"));
            using (var server = builder.Build())
            {
                // Workaround for bug in Windows Server 2012 when ReadLine is called directly after AcceptAsync
                // By starting it in another thread and probably even later than calling readline it works
                Task.Run(() => server.Start());
                //using (new Timer(o =>
                //    {
                //        var s = (INowinServer)o;
                //        Console.WriteLine("Connections {0}/{1}", s.ConnectionCount, s.CurrentMaxConnectionCount);
                //    }, server, 2000, 2000))
                {
                    Console.WriteLine("Listening on port 8888. Enter to exit.");
                    Console.ReadLine();
                }
            }
        }
    }

}
