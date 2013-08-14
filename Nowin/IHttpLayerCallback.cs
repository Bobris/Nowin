using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Nowin
{
    public interface IHttpLayerCallback : ILayerCallback
    {
        CancellationToken CallCancelled { get; }

        Stream ReqRespBody { get; }

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
        void CloseConnection();

        bool HeadersSend { get; }
        byte[] Buffer { get; }
        int ReceiveDataOffset { get; }
        int ReceiveDataLength { get; }
        void ConsumeReceiveData(int count);
        void StartReceiveData();
        int SendDataOffset { get; }
        int SendDataLength { get; }
        Task SendData(byte[] buffer, int offset, int length);
    }
}