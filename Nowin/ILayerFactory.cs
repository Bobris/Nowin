namespace Nowin
{
    public interface ILayerFactory
    {
        int PerConnectionBufferSize { get; }
        int CommonBufferSize { get; }
        void InitCommonBuffer(byte[] buffer, int offset);
        ILayerHandler Create(byte[] buffer, int offset, int commonOffset, int handlerId);
    }
}