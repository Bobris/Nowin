namespace Nowin
{
    public interface IHttpLayerHandler : ILayerHandler
    {
        IHttpLayerCallback Callback { set; }

        void PrepareForRequest();
        void AddRequestHeader(string name, string value);
        void HandleRequest();
        void PrepareResponseHeaders();
        void UpgradedToWebSocket(bool success);
        void FinishReceiveData(bool success);
    }
}