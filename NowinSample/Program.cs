using System;
using NowinWebServer;

namespace NowinSample
{
    static class Program
    {
        static void Main(string[] args)
        {
            using (ServerBuilder.New().SetPort(8888).SetOwinApp(SampleOwinApp.Sample.App).Start())
            {
                Console.WriteLine("Listening on port 8888. Enter to exit.");
                Console.ReadLine();
            }
        }
    }

}
