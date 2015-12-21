using System;
using System.Security.Cryptography.X509Certificates;

namespace NowinAcme
{
    public interface IAcmeConfiguration
    {
        string Email { get; }
        string Domain { get; }
        DateTime LastUpdate { get; }
        void UpdateCertificate(X509Certificate cert);
        void LogVerbose(string message, params object[] args);
        void LogInfo(string message, params object[] args);
        void LogWarning(string message, params object[] args);
        void LogError(string message, params object[] args);
    }
}