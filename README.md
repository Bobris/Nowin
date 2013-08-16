Nowin
=====

Fast and scalable Owin Web Server in pure .Net 4.5 (it does not use HttpListener)

Current status is usable for testing, not for production, nobody did any security review, you have been warned. On Windows speed is better than NodeJs and in some cases even better than HttpListener.

Features it supports:
- Http 1.0 and 1.1 clients uses SocketAsyncEventArgs in most optimal way you can found on the Internets
- KeepAlive, untested pipelining, automatic chunked en/decoding of request and response
- Everything strictly asynchronous and parallel automaticaly using all available cores
- SSL using .Net SSL Stream so in theory it should be same secure
- WebSockets in platform independent way! It buffers data so SignalR is more optimal on wire than current HttpListener on Win8.
- Tracks currently connection counts and maximum allocated connections and allocates new as needed
- One connection needs less than 20kb RAM and most of it is reused but never deallocated.
- By default settings maximum size of request and response headers are 8KB.
- Published in Nuget for easy use. No dependencies.

Other Owin .Net server samples also included. Some parts of these samples source code are modified from Katana project.

Sample: (uses Microsoft.Owin.Hosting nuget from http://www.myget.org/f/aspnetwebstacknightly/)

    static class Program
    {
        static void Main(string[] args)
        {
            var options = new StartOptions
            {
                ServerFactory = "Nowin",
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
