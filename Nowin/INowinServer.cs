using System;

namespace Nowin
{
    public interface INowinServer : IDisposable
    {
        void Start();
        int ConnectionCount { get; }
        int CurrentMaxConnectionCount { get; }
    }
}