using System;
using System.Security.Cryptography.X509Certificates;
using NowinWebServer;

namespace NowinSample
{
    static class Program
    {
        static void Main(string[] args)
        {
            var builder = ServerBuilder.New().SetPort(8888).SetOwinApp(SampleOwinApp.Sample.App);
            builder.SetCertificate(new X509Certificate2("../../../sslcert/test.pfx", "nowin"));
            using (builder.Start())
            {
                Console.WriteLine("Listening on port 8888. Enter to exit.");
                Console.ReadLine();
            }
        }
    }

}
