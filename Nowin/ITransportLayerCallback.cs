namespace Nowin
{
    public interface ITransportLayerCallback : ILayerCallback
    {
        void StartAccept(byte[] buffer, int offset, int length);
        void StartReceive(byte[] buffer, int offset, int length);
        void StartSend(byte[] buffer, int offset, int length);
        void StartDisconnect();
    }
}