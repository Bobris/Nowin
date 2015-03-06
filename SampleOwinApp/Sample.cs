using System;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNet.SignalR;
using Microsoft.Owin.Cors;
using Owin;
using Microsoft.Owin.Builder;

namespace SampleOwinApp
{
    using WebSocketAccept = Action<IDictionary<string, object>, Func<IDictionary<string, object>, Task>>;
    using WebSocketCloseAsync =
        Func<int /* closeStatus */,
            string /* closeDescription */,
            CancellationToken /* cancel */,
            Task>;
    using WebSocketReceiveAsync =
        Func<ArraySegment<byte> /* data */,
            CancellationToken /* cancel */,
            Task<Tuple<int /* messageType */,
                bool /* endOfMessage */,
                int /* count */>>>;
    using WebSocketSendAsync =
        Func<ArraySegment<byte> /* data */,
            int /* messageType */,
            bool /* endOfMessage */,
            CancellationToken /* cancel */,
            Task>;

    internal static class DictionaryExtensions
    {
        internal static T Get<T>(this IDictionary<string, object> dictionary, string key)
        {
            object value;
            return dictionary.TryGetValue(key, out value) ? (T)value : default(T);
        }
    }

    public class MyHub : Hub
    {
        public void Send(string name, string message)
        {
            Clients.All.addMessage(name, message);
        }
    }

    public class Startup
    {
        public void Configuration(IAppBuilder app)
        {
            if (true)
            {
                app.Map("/signalr", map =>
                {
                    // Setup the CORS middleware to run before SignalR.
                    // By default this will allow all origins. You can 
                    // configure the set of origins and/or http verbs by
                    // providing a cors options with a different policy.
                    map.UseCors(CorsOptions.AllowAll);
                    var hubConfiguration = new HubConfiguration
                    {
                        EnableDetailedErrors = true
                        //EnableJSONP = true
                        // You can enable JSONP by uncommenting line below.
                        // JSONP requests are insecure but some older browsers (and some
                        // versions of IE) require JSONP to work cross domain
                        // EnableJSONP = true
                    };
                    // Run the SignalR pipeline. We're not using MapSignalR
                    // since this branch already runs under the "/signalr"
                    // path.
                    map.RunSignalR(hubConfiguration);
                });
            }
            else
            {
                //app.UseCors(CorsOptions.AllowAll);
                app.MapSignalR(); // replaced by the above
            }
            app.Map("/echo", a => a.Run(c =>
            {
                var accept = c.Get<WebSocketAccept>("websocket.Accept");
                if (accept == null)
                {
                    c.Response.StatusCode = 500;
                    return Task.Delay(0);
                }
                accept(
                    null,
                    async wsEnv =>
                    {
                        var sendAsync = wsEnv.Get<WebSocketSendAsync>("websocket.SendAsync");
                        var receiveAsync = wsEnv.Get<WebSocketReceiveAsync>("websocket.ReceiveAsync");
                        var closeAsync = wsEnv.Get<WebSocketCloseAsync>("websocket.CloseAsync");

                        var buffer = new ArraySegment<byte>(new byte[1000]);
                        var serverReceive = await receiveAsync(buffer, CancellationToken.None);
                        await sendAsync(new ArraySegment<byte>(buffer.Array, 0, serverReceive.Item3),
                            serverReceive.Item1, serverReceive.Item2, CancellationToken.None);
                        await closeAsync((int)WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
                    });

                return Task.Delay(0);
            }));
            app.Run(async c =>
                {
                    var path = c.Request.Path.Value;
                    if (path == "/")
                    {
                        c.Response.StatusCode = 200;
                        c.Response.ContentType = "text/plain";
                        c.Response.Write("Hello World!");
                        return;
                    }
                    if (path == "/sse")
                    {
                        c.Response.StatusCode = 200;
                        c.Response.ContentType = "text/event-stream";
                        c.Response.Headers.Add("Cache-Control", new[] { "no-cache" });
                        for (int i = 0; i < 10; i++)
                        {
                            await c.Response.WriteAsync("data: " + i.ToString() + "\n\n");
                            await c.Response.Body.FlushAsync();
                            await Task.Delay(500);
                        }
                        await c.Response.WriteAsync("data: Finish!\n\n");
                        return;
                    }
                    if (path.Contains(".."))
                    {
                        // hackers ..
                        c.Response.StatusCode = 500;
                        return;
                    }
                    var p = Path.Combine(@"..\..\..\SampleOwinApp\", path.Substring(1));
                    if (File.Exists(p))
                    {
                        c.Response.StatusCode = 200;
                        c.Response.ContentType = p.EndsWith(".js") ? "application/javascript" : "text/html";
                        await c.Response.WriteAsync(File.ReadAllBytes(p));
                        return;
                    }
                    c.Response.StatusCode = 404;
                    return;
                });
        }
    }

    public static class Sample
    {
        static readonly Func<IDictionary<string, object>, Task> OwinApp;

        static Sample()
        {
            var builder = new AppBuilder();
            new Startup().Configuration(builder);
            OwinApp = builder.Build();
        }

        public static Task App(IDictionary<string, object> arg)
        {
            return OwinApp(arg);
        }
    }
}
