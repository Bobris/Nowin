Nowin
=====

Fast Owin Web Server in pure .Net 4.5 (it does not use HttpListener)

Current status is usable for testing, not for production, nobody did any security review. But in keep-alive case with Hello World localhost response is 2-3 times faster than NodeJs 0.10.7 or HttpListener. Code is limited by Kernel socket speed, than by its implementation.

SSL is also supported!

Plan for future is to implement WebSockets, and call it a day feature wise.

Other Owin .Net server samples also included. Some parts of these samples source code are modified from Katana project.

Sample: (uses Microsoft.Owin.Hosting nuget from http://www.myget.org/f/aspnetwebstacknightly/)

    static class Program
    {
        static void Main(string[] args)
        {
            var options = new StartOptions
            {
                ServerFactory = "NowinWebServer",
                Port = 8080
            };

            using (WebApp.Start<Startup>(options))
            {
                Console.WriteLine("Running a http server on port 8080");
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

Https sample using builder:

    var builder = ServerBuilder.New().SetPort(8888).SetOwinApp(SomeOwinApp);
    builder.SetCertificate(new X509Certificate2("certificate.pfx", "password"));
    using (builder.Start())
    {
        Console.WriteLine("Listening on port 8888. Enter to exit.");
        Console.ReadLine();
    }
