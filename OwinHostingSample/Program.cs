using System;
using System.Threading.Tasks;
using Microsoft.Owin.Hosting;
using Owin;

namespace OwinHostingSample
{
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
            app.UseHandlerAsync((req, res) =>
            {
                if (req.Path == "/")
                {
                    res.ContentType = "text/plain";
                    return res.WriteAsync("Hello World!");
                }
                res.StatusCode = 404;
                return Task.Delay(0);
            });
        }
    }
}
