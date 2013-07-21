using System;

namespace NowinWebServer
{
    public interface ITransportLayerHandler : ILayerHandler
    {
        ITransportLayerCallback Callback { set; }
        void PrepareAccept();
        void FinishAccept(byte[] buffer, int offset, int length);
        void FinishReceive(byte[] buffer, int offset, int length);
        void FinishReceiveWithAbort();
        void FinishSend(Exception exception);
    }
}