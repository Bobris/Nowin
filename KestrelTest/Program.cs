using System;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting.Internal;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.ObjectPool;
using Microsoft.Extensions.Options;

namespace KestrelTest
{
    class Program
    {
        static void Main(string[] args)
        {
            var options = new KestrelServerOptions();
            options.NoDelay = true;
            options.ThreadCount = 2;
            var applicationLifetime = new ApplicationLifetime();
            var server = new KestrelServer(new OptionsWrapper<KestrelServerOptions>(options), applicationLifetime,
                new LoggerFactory());
            server.Features.Get<IServerAddressesFeature>().Addresses.Add("http://localhost:8888");
            server.Start(new HttpApp());
            Console.WriteLine("Listening on 8888. Press Enter to stop.");
            Console.ReadLine();
            server.Dispose();
        }

        class HttpApp : IHttpApplication<HttpContext>
        {
            readonly HttpContextFactory _factory;
            readonly byte[] _text;

            public HttpApp()
            {
                _factory = new HttpContextFactory(new DefaultObjectPoolProvider(), new OptionsWrapper<FormOptions>(new FormOptions()));
                _text = Encoding.UTF8.GetBytes("Hello World");
            }

            public HttpContext CreateContext(IFeatureCollection contextFeatures)
            {
                return _factory.Create(contextFeatures);
            }

            public Task ProcessRequestAsync(HttpContext context)
            {
                context.Response.ContentLength = _text.Length;
                context.Response.Body.Write(_text, 0, _text.Length);
                return Task.Delay(0);
            }

            public void DisposeContext(HttpContext context, Exception exception)
            {
                _factory.Dispose(context);
            }
        }
    }
}
