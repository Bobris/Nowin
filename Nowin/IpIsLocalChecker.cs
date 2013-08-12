using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;

namespace Nowin
{
    public class IpIsLocalChecker : IIpIsLocalChecker
    {
        readonly Dictionary<IPAddress, bool> _dict;

        public IpIsLocalChecker()
        {
            var host = Dns.GetHostEntry(Dns.GetHostName());
            _dict = host.AddressList.Where(
                a => a.AddressFamily == AddressFamily.InterNetwork || a.AddressFamily == AddressFamily.InterNetworkV6).
                ToDictionary(p => p, p => true);
            _dict[IPAddress.Loopback] = true;
            _dict[IPAddress.IPv6Loopback] = true;
        }

        public bool IsLocal(IPAddress address)
        {
            return _dict.ContainsKey(address);
        }
    }
}