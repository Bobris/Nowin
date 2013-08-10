using System.Net;

namespace Nowin
{
    public interface IIpIsLocalChecker
    {
        bool IsLocal(IPAddress address);
    }
}