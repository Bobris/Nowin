namespace NowinWebServer
{
    public interface ILayerFactory
    {
        int PerConnectionBufferSize { get; }
        int CommonBufferSize { get; }
        void InitCommonBuffer(byte[] buffer, int offset);
        ILayerHandler Create(Server server, byte[] buffer, int offset, int commonOffset);
    }
}