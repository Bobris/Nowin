using System;
using System.Net;
using System.Security.Cryptography.X509Certificates;

namespace Nowin
{
    public interface ITransportLayerHandler : ILayerHandler
    {
        ITransportLayerCallback Callback { set; }
        void PrepareAccept();
        void FinishAccept(byte[] buffer, int offset, int length, IPEndPoint remoteEndPoint, IPEndPoint localEndPoint);
        void SetRemoteCertificate(X509Certificate remoteCertificate);

        // length==-1 for connection closed
        void FinishReceive(byte[] buffer, int offset, int length);
        void FinishSend(Exception exception);
    }
}