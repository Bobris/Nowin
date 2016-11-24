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
            try
            {
                var host = Dns.GetHostEntry(Dns.GetHostName());

                _dict = host.AddressList.Where(
                    a =>
                        a.AddressFamily == AddressFamily.InterNetwork || a.AddressFamily == AddressFamily.InterNetworkV6)
                    .Distinct()
                    .ToDictionary(p => p, p => true);
            }
            catch (SocketException)
            {
                _dict = new Dictionary<IPAddress, bool>();
            }

            _dict[IPAddress.Loopback] = true;
            _dict[IPAddress.IPv6Loopback] = true;
        }

        public bool IsLocal(IPAddress address)
        {
            if (_dict.ContainsKey(address))
                return true;

            if (IPAddress.IsLoopback(address))
                return true;

            if (address.IsIPv4MappedToIPv6)
            {
                var ip4 = address.MapToIPv4();
                if (_dict.ContainsKey(ip4))
                    return true;
            }

            return false;
        }
    }
}