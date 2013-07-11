using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace SampleOwinApp
{
    public class Sample
    {
        public static Task App(IDictionary<string, object> arg)
        {
            var req = new Owin.Types.OwinRequest(arg);
            var resp = new Owin.Types.OwinResponse(req);
            if (req.Path == "/")
            {
                resp.StatusCode = 200;
                resp.AddHeader("Content-Type", "text/plain");
                resp.Write("Hello World!");
                return Task.Delay(0);
            }
            var p = Path.Combine(@"c:\Research\SampleWebPage", req.Path.Substring(1));
            if (File.Exists(p))
            {
                resp.StatusCode = 200;
                resp.AddHeader("Content-Type", "text/html");
                return resp.WriteAsync(File.ReadAllBytes(p));
            }
            resp.StatusCode = 500;
            return Task.Delay(0);
        }
    }
}
