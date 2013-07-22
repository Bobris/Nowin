using System;

namespace NowinWebServer
{
    public interface INowinServer : IDisposable
    {
        void Start();
        int ConnectionCount { get; }
        int CurrentMaxConnectionCount { get; }
    }
}