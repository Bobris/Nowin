using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Owin.Host.HttpListener;
using Mono.Net;

namespace MonoHttpListener
{
    class Program
    {
        static void Main(string[] args)
        {
            using (var l = new OwinHttpListener())
            {
                l.Start(new HttpListener(), SampleOwinApp.Sample.App, new List<IDictionary<string, object>>
                            {
                                new Dictionary<string, object>
                                    {
                                        {"scheme","http"},
                                        {"host","localhost"},
                                        {"port","8888"},
                                        {"path",""}
                                    }
                            }, new Dictionary<string, object>());
                Console.ReadLine();
                l.Stop();
            }
        }
    }
}
