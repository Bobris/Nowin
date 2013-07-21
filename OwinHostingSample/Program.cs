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
            app.Use(context =>
            {
                if (context.Request.Path == "/")
                {
                    context.Response.ContentType = "text/plain";
                    return context.Response.WriteAsync("Hello World!");
                }

                context.Response.StatusCode = 404;
                return Task.Delay(0);
            });
        }
    }
}
