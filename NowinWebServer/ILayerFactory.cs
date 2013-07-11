namespace NowinWebServer
{
    public interface ILayerFactory
    {
        ILayerHandler Create(ILayerCallback callback,byte[] buffer,int offset,int commonOffset);
    }
}