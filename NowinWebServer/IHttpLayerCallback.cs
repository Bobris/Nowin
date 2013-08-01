using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace NowinWebServer
{
    public interface IHttpLayerCallback : ILayerCallback
    {
        CancellationToken CallCancelled { get; }

        Stream ResponseBody { get; }
        Stream RequestBody { get; }

        string RequestPath { get; }
        string RequestQueryString { get; }
        string RequestMethod { get; }
        string RequestScheme { get; }
        string RequestProtocol { get; }
        string RemoteIpAddress { get; }
        string RemotePort { get; }
        string LocalIpAddress { get; }
        string LocalPort { get; }
        bool IsLocal { get; }
        bool IsWebSocketReq { get; }

        int ResponseStatusCode { set; }
        string ResponseReasonPhase { set; }
        ulong ResponseContentLength { set; }
        bool KeepAlive { set; }

        void AddResponseHeader(string name, string value);
        void AddResponseHeader(string name, IEnumerable<string> values);

        void UpgradeToWebSocket();
        void ResponseFinished();
    }
}