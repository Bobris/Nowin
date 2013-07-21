using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Owin;

namespace SampleOwinApp
{
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
