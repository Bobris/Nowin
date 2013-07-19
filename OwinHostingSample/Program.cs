using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Owin.Hosting;
using Owin;

namespace OwinHostingSample
{
    using App = Func<IDictionary<string, object>, Task>;

    static class Program
    {
        static void Main(string[] args)
        {
            var options = new StartOptions
            {
                ServerFactory = "NowinWebServer"
            };

            using (WebApp.Start<Startup>(options))
            {
                Console.WriteLine("Running a http server");
                Console.ReadKey();
            }
        }
    }

    public class Startup
    {
        public void Configuration(IAppBuilder app)
        {
            app.Run((App)SampleOwinApp.Sample.App);
        }
    }
}
