using System.Security.Cryptography.X509Certificates;

namespace Nowin
{
    public interface IUpdateCertificate
    {
        void UpdateCertificate(X509Certificate certificate);
    }
}