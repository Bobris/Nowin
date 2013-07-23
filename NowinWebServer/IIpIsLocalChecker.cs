using System.Net;

namespace NowinWebServer
{
    public interface IIpIsLocalChecker
    {
        bool IsLocal(IPAddress address);
    }
}