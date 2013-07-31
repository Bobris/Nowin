using System;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Owin;

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
            return dictionary.TryGetValue(key, out value) ? (T) value : default(T);
        }
    }

    public class Sample
    {
        public static Task App(IDictionary<string, object> arg)
        {
            var req = new OwinRequest(arg);
            var resp = new OwinResponse(arg);
            if (req.Path == "/")
            {
                resp.StatusCode = 200;
                resp.ContentType = "text/plain";
                resp.Write("Hello World!");
                return Task.Delay(0);
            }
            if (req.Path == "/echo")
            {
                var accept = arg.Get<WebSocketAccept>("websocket.Accept");
                if (accept == null)
                {
                    resp.StatusCode = 500;
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

            }
            var p = Path.Combine(@"c:\Research\SampleWebPage", req.Path.Substring(1));
            if (File.Exists(p))
            {
                resp.StatusCode = 200;
                resp.ContentType = "text/html";
                return resp.WriteAsync(File.ReadAllBytes(p));
            }
            resp.StatusCode = 500;
            return Task.Delay(0);
        }
    }
}
