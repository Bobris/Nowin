using System;

namespace NowinWebServer
{
    public interface ITransportLayerHandler : ILayerHandler
    {
        ITransportLayerCallback Callback { set; }
        void PrepareAccept();
        void FinishAccept(byte[] buffer, int offset, int length);

        // length==-1 for connection closed
        void FinishReceive(byte[] buffer, int offset, int length);
        void FinishSend(Exception exception);
    }
}