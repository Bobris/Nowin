using System;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;
using Oocx.ACME.Client;
using Oocx.ACME.Common;
using Oocx.ACME.Protocol;
using Oocx.ACME.Services;
using Oocx.Asn1PKCS.Asn1BaseTypes;
using Oocx.Asn1PKCS.PKCS10;

namespace NowinAcme
{
    class AcmeProcess
    {
        readonly string _email;
        readonly string _domain;
        readonly Action<X509Certificate> _certificateUpdater;
        readonly Action<string, byte[]> _challengeProof;
        readonly IAcmeClient _client;
        readonly ICertificateRequestAsn1DEREncoder _certificateRequestEncoder;
        string _termsOfServiceUri = "https://letsencrypt.org/documents/LE-SA-v1.0.1-July-27-2015.pdf";

        internal AcmeProcess(string email, string domain, Action<X509Certificate> certificateUpdater, Action<string, byte[]> challengeProof)
        {
            _email = email;
            _domain = domain;
            _certificateUpdater = certificateUpdater;
            _challengeProof = challengeProof;
            _client = new AcmeClient("https://acme-v01.api.letsencrypt.org", "keyName", new FileKeyStore(Environment.CurrentDirectory));
            _certificateRequestEncoder = new CertificateRequestAsn1DEREncoder(new Asn1Serializer());
        }

        internal async Task StartAsync()
        {
            await RegisterWithServer();

            bool isAuthorized = await AuthorizeForDomain(_domain);
            if (!isAuthorized)
            {
                Log.Error($"authorization for domain {_domain} failed");
                return;
            }

            var keyPair = GetNewKeyPair();

            var certificateResponse = await RequestCertificateForDomain(_domain, keyPair);

            var csp = new CspParameters { KeyContainerName = "oocx-acme-temp" };
            var rsa2 = new RSACryptoServiceProvider(csp);
            rsa2.ImportParameters(keyPair);

            var certificate = new X509Certificate2(certificateResponse.Certificate, "", X509KeyStorageFlags.Exportable) { PrivateKey = rsa2 };

            _certificateUpdater(certificate);
        }

        async Task<CertificateResponse> RequestCertificateForDomain(string domain, RSAParameters key)
        {
            var csr = CreateCertificateRequest(domain, key);
            return await _client.NewCertificateRequestAsync(csr);
        }

        static RSAParameters GetNewKeyPair()
        {
            var rsa = new RSACryptoServiceProvider(2048);
            var key = rsa.ExportParameters(true);
            return key;
        }

        byte[] CreateCertificateRequest(string domain, RSAParameters key)
        {
            var data = new CertificateRequestData(domain, key);
            var csr = _certificateRequestEncoder.EncodeAsDER(data);
            return csr;
        }

        async Task<bool> AuthorizeForDomain(string domain)
        {
            var authorization = await _client.NewDnsAuthorizationAsync(domain);

            var challenge = authorization?.Challenges.FirstOrDefault(c => c.Type == "http-01");
            if (challenge == null)
            {
                Log.Error("the server does not accept challenge type http-01");
                return false;
            }

            Log.Info($"accepting challenge {challenge.Type}");

            var keyAuthorization = _client.GetKeyAuthorization(challenge.Token);
            _challengeProof($"/.well-known/acme-challenge/{challenge.Token}", Encoding.ASCII.GetBytes(keyAuthorization));
            var challengeResult = await _client.CompleteChallengeAsync(challenge);
            _challengeProof(null, null);
            return "valid".Equals(challengeResult?.Status, StringComparison.OrdinalIgnoreCase);
        }

        async Task RegisterWithServer()
        {
            var registration = await _client.RegisterAsync(_termsOfServiceUri, new[] { "mailto:" + _email });
            Log.Info($"Terms of service: {registration.Agreement}");
            Log.Verbose($"Created at: {registration.CreatedAt}");
            Log.Verbose($"Id: {registration.Id}");
            Log.Verbose($"Contact: {string.Join(", ", registration.Contact)}");
            Log.Verbose($"Initial Ip: {registration.InitialIp}");

            if (!string.IsNullOrWhiteSpace(registration.Location))
            {
                Log.Info("accepting terms of service");
                if (!string.Equals(registration.Agreement, _termsOfServiceUri))
                {
                    Log.Error($"Cannot accept terms of service. The terms of service uri is '{registration.Agreement}', expected it to be '{_termsOfServiceUri}'.");
                    return;
                }
                await _client.UpdateRegistrationAsync(registration.Location, registration.Agreement, new[] { "mailto:" + _email });
            }
        }
    }
}