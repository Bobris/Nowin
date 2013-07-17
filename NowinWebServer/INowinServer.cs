using System;

namespace NowinWebServer
{
    public interface INowinServer : IDisposable
    {
        void Start();
    }
}