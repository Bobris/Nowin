using System;
using System.Collections.Generic;

namespace HttpListener
{
    class Program
    {
        static void Main(string[] args)
        {
            var props = new Dictionary<string, object>
                {
                    {
                        "host.Addresses", new List<IDictionary<string, object>>
                            {
                                new Dictionary<string, object>
                                    {
                                        {"scheme","http"},
                                        {"host","+"},
                                        {"port","8888"},
                                        {"path",""}
                                    }
                            }
                    }
                };
            using (Microsoft.Owin.Host.HttpListener.OwinServerFactory.Create(SampleOwinApp.Sample.App, props))
            {
                Console.WriteLine("Listening on 8888. Enter to stop.");
                Console.ReadLine();
            }
        }
    }
}
