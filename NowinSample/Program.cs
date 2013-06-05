using System;
using System.Net;
using NowinWebServer;

namespace NowinSample
{
    static class Program
    {
        static void Main(string[] args)
        {
            var server = new Server(maxConnections: 1000);
            server.Start(new IPEndPoint(IPAddress.Any, 8888), SampleOwinApp.Sample.App);
            Console.WriteLine("Listening on port 8888. Enter to exit.");
            Console.ReadLine();
            server.Stop();
        }
    }

}
