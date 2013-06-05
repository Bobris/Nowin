using System;

namespace FluxServer
{
    class Program
    {
        static void Main(string[] args)
        {
            using (var server = new Flux.Server(8888))
            {
                server.Start(SampleOwinApp.Sample.App);
                Console.WriteLine("Listening on 8888. Enter to stop.");
                Console.ReadLine();
                server.Stop();
            }
        }
    }
}
