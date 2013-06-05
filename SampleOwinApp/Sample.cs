using System.Collections.Generic;
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
            resp.StatusCode = 500;
            return Task.Delay(0);
        }
    }
}
