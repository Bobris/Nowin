using System;
using System.Collections.Generic;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;

namespace Nowin
{
    internal interface IServerParameters
    {
        ExecutionContextFlow ContextFlow { get; }
        IConnectionAllocationStrategy ConnectionAllocationStrategy { get; }
        IPEndPoint EndPoint { get; }
        X509Certificate Certificate { get; }
        int BufferSize { get; }
        Func<IDictionary<string, object>, Task> OwinApp { get; }
        IDictionary<string, object> OwinCapabilities { get; }
        string ServerHeader { get; }
        TimeSpan RetrySocketBindingTime { get; }
    }
}