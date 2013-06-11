using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using NowinWebServer;
using AppFunc = System.Func<System.Collections.Generic.IDictionary<string, object>, System.Threading.Tasks.Task>;

// Heavily inspired by Katana project OwinHttpListener tests

namespace NowinTests
{
    [TestFixture]
    public class NowinTests
    {
        const string HttpClientAddress = "http://localhost:8080/";
        readonly AppFunc _appThrow = env => { throw new InvalidOperationException(); };

        [Test]
        public void NowinTrivial()
        {
            var listener = CreateServer(_appThrow);
            using (listener)
            {
                listener.Stop();
            }
        }

        [Test]
        public void EmptyAppRespondOk()
        {
            var listener = CreateServer(env => Task.Delay(0));
            var response = SendGetRequest(listener, HttpClientAddress).Result;
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.NotNull(response.Content.Headers.ContentLength);
            Assert.AreEqual(0, response.Content.Headers.ContentLength.Value);
        }

        [Test]
        public void EmptyAppAnd2Requests()
        {
            var listener = CreateServer(env => Task.Delay(0));
            using (listener)
            {
                var client = new HttpClient();
                string result = client.GetStringAsync(HttpClientAddress).Result;
                Assert.AreEqual(string.Empty, result);
                result = client.GetStringAsync(HttpClientAddress).Result;
                Assert.AreEqual(string.Empty, result);
            }
        }

        [Test]
        public void ThrowAppRespond500()
        {
            var listener = CreateServer(_appThrow);
            var response = SendGetRequest(listener, HttpClientAddress).Result;
            Assert.AreEqual(HttpStatusCode.InternalServerError, response.StatusCode);
            Assert.NotNull(response.Content.Headers.ContentLength);
            Assert.AreEqual(0, response.Content.Headers.ContentLength.Value);
        }

        [Test]
        public void AsyncThrowAppRespond500()
        {
            var callCancelled = false;
            var listener = CreateServer(
                async env =>
                {
                    GetCallCancelled(env).Register(() => callCancelled = true);
                    await Task.Delay(1);
                    throw new InvalidOperationException();
                });

            var response = SendGetRequest(listener, HttpClientAddress).Result;
            Assert.AreEqual(HttpStatusCode.InternalServerError, response.StatusCode);
            Assert.NotNull(response.Content.Headers.ContentLength);
            Assert.AreEqual(0, response.Content.Headers.ContentLength.Value);
            Assert.True(callCancelled);
        }

        [Test]
        public void PostEchoAppWorks()
        {
            var callCancelled = false;
            var listener = CreateServer(
                async env =>
                {
                    GetCallCancelled(env).Register(() => callCancelled = true);
                    var requestHeaders = env.Get<IDictionary<string, string[]>>("owin.RequestHeaders");
                    var responseHeaders = env.Get<IDictionary<string, string[]>>("owin.ResponseHeaders");
                    responseHeaders.Add("Content-Length", requestHeaders["Content-Length"]);

                    var requestStream = env.Get<Stream>("owin.RequestBody");
                    var responseStream = env.Get<Stream>("owin.ResponseBody");

                    var buffer = new MemoryStream();
                    await requestStream.CopyToAsync(buffer, 1024);
                    buffer.Seek(0, SeekOrigin.Begin);
                    await buffer.CopyToAsync(responseStream, 1024);
                });

            using (listener)
            {
                var client = new HttpClient();
                const string dataString = "Hello World";
                var response = client.PostAsync(HttpClientAddress, new StringContent(dataString)).Result;
                response.EnsureSuccessStatusCode();
                Assert.NotNull(response.Content.Headers.ContentLength);
                Assert.AreEqual(dataString.Length, response.Content.Headers.ContentLength.Value);
                Assert.AreEqual(dataString, response.Content.ReadAsStringAsync().Result);
                Assert.False(callCancelled);
            }
        }

        [Test]
        public void ConnectionClosedAfterStartReturningResponseAndThrowing()
        {
            bool callCancelled = false;
            var listener = CreateServer(
                env =>
                {
                    GetCallCancelled(env).Register(() => callCancelled = true);
                    var responseHeaders = env.Get<IDictionary<string, string[]>>("owin.ResponseHeaders");
                    responseHeaders.Add("Content-Length", new[] { "10" });

                    var responseStream = env.Get<Stream>("owin.ResponseBody");
                    responseStream.WriteByte(0xFF);
                    responseStream.Flush();

                    throw new InvalidOperationException();
                });

            try
            {
                Assert.Throws<AggregateException>(() => SendGetRequest(listener, HttpClientAddress).Wait());
            }
            finally
            {
                Assert.True(callCancelled);
            }
        }

        [Test]
        public void ConnectionClosedAfterStartReturningResponseAndAsyncThrowing()
        {
            var callCancelled = false;

            var listener = CreateServer(
                env =>
                {
                    GetCallCancelled(env).Register(() => callCancelled = true);
                    var responseHeaders = env.Get<IDictionary<string, string[]>>("owin.ResponseHeaders");
                    responseHeaders.Add("Content-Length", new[] { "10" });

                    var responseStream = env.Get<Stream>("owin.ResponseBody");
                    responseStream.WriteByte(0xFF);
                    responseStream.Flush();

                    var tcs = new TaskCompletionSource<bool>();
                    tcs.SetException(new InvalidOperationException());
                    return tcs.Task;
                });

            try
            {
                Assert.Throws<AggregateException>(() => SendGetRequest(listener, HttpClientAddress).Wait());
            }
            finally
            {
                Assert.True(callCancelled);
            }
        }

        [Test]
        [TestCase("/", "/", "")]
        [TestCase("/path?query", "/path", "query")]
        [TestCase("/pathBase/path?query", "/pathBase/path", "query")]
        public void PathAndQueryParsing(string clientString, string expectedPath, string expectedQuery)
        {
            clientString = "http://localhost:8080" + clientString;
            var listener = CreateServer(env =>
            {
                Assert.AreEqual("", env["owin.RequestPathBase"]);
                Assert.AreEqual(expectedPath, env["owin.RequestPath"]);
                Assert.AreEqual(expectedQuery, env["owin.RequestQueryString"]);
                return Task.Delay(0);
            });
            using (listener)
            {
                var client = new HttpClient();
                var result = client.GetAsync(clientString).Result;
                Assert.AreEqual(HttpStatusCode.OK, result.StatusCode);
            }
        }

        static CancellationToken GetCallCancelled(IDictionary<string, object> env)
        {
            return env.Get<CancellationToken>("owin.CallCancelled");
        }

        static async Task<HttpResponseMessage> SendGetRequest(IDisposable listener, string address)
        {
            using (listener)
            {
                var handler = new WebRequestHandler
                    {
                        ServerCertificateValidationCallback = (a, b, c, d) => true,
                        ClientCertificateOptions = ClientCertificateOption.Automatic
                    };
                var client = new HttpClient(handler);
                return await client.GetAsync(address);
            }
        }

        static Server CreateServer(AppFunc app)
        {
            var server = new Server(10);
            server.Start(new IPEndPoint(IPAddress.Loopback, 8080), app);
            return server;
        }
    }
}
