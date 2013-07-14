namespace NowinWebServer
{
    public interface ITransportLayerCallback : ILayerCallback
    {
        void StartAccept(int offset, int length);
        void StartReceive(int offset, int length);
        void StartSend(int offset, int length);
        void StartDisconnect();
    }
}