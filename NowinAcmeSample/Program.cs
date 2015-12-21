using System;
using System.IO;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;
using Nowin;
using NowinAcme;

namespace NowinAcmeSample
{
    static class Program
    {
        // If it does not exist it will be created, if it is older than 30 days it will be refreshed
        const string CertPathOnDisk = "cert.pfx";
        const string CertPassword = "pass";

        class AcmeCfg : IAcmeConfiguration
        {
            readonly IUpdateCertificate _updateCertificate;
            DateTime _lastUpdate;

            public AcmeCfg(IUpdateCertificate updateCertificate, DateTime lastModified)
            {
                _updateCertificate = updateCertificate;
                _lastUpdate = lastModified;
            }

            // Fill your real e-mail here
            public string Email => "email@email.com";
            // Fill your real domain here
            public string Domain => "example.com";

            public DateTime LastUpdate => _lastUpdate;
            public void UpdateCertificate(X509Certificate cert)
            {
                _updateCertificate.UpdateCertificate(cert);
                File.WriteAllBytes(CertPathOnDisk, cert.Export(X509ContentType.Pfx, CertPassword));
                _lastUpdate = DateTime.UtcNow;
                LogInfo("Certificate successfully updated");
            }

            public void LogVerbose(string message, params object[] args)
            {
                Console.WriteLine(message, args);
            }

            public void LogInfo(string message, params object[] args)
            {
                Console.WriteLine(message, args);
            }

            public void LogWarning(string message, params object[] args)
            {
                Console.WriteLine(message, args);
            }

            public void LogError(string message, params object[] args)
            {
                Console.WriteLine("Error: " + message, args);
            }
        }

        static void Main(string[] args)
        {
            var cert = new X509Certificate();
            var lastModified = DateTime.MinValue;
            try
            {
                cert = new X509Certificate2(CertPathOnDisk, CertPassword);
                lastModified = File.GetLastWriteTimeUtc(CertPathOnDisk);
            }
            catch (Exception)
            {
                // ignored new certificate needs to be created
            }
            var builder443 = ServerBuilder.New().SetAddress(IPAddress.Any)
                .SetPort(443)
                .SetCertificate(cert)
                .SetExecutionContextFlow(ExecutionContextFlow.SuppressAlways)
                .SetOwinApp(env =>
                {
                    // This should be your owin application instead of this sample
                    var respBody = (Stream)env["owin.ResponseBody"];
                    var resp = Encoding.UTF8.GetBytes("Secured content");
                    respBody.Write(resp, 0, resp.Length);
                    return Task.CompletedTask;
                });
            var acmeCfg = new AcmeCfg(builder443, lastModified);
            if (acmeCfg.Domain == "example.com")
            {
                Console.WriteLine("You have to provide some real domain for this sample");
                Console.ReadLine();
                return;
            }
            var builder80 = ServerBuilder.New().SetAddress(IPAddress.Any)
                .SetPort(80)
                .SetExecutionContextFlow(ExecutionContextFlow.SuppressAlways)
                .SetOwinApp(NowinAcme.NowinAcme.Use(NowinAcme.NowinAcme.RedirectToHttps, acmeCfg));

            using (var server443 = builder443.Build())
            using (var server80 = builder80.Build())
            {
                // Workaround for bug in Windows Server 2012 when ReadLine is called directly after AcceptAsync
                // By starting it in another thread and probably even later than calling readline it works
                Task.Run(() => server443.Start());
                Task.Run(() => server80.Start());
                Console.WriteLine("Listening on ports 80 and 443. Enter to exit.");
                Console.ReadLine();
            }
        }
    }
}
