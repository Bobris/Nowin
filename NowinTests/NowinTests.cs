using System;
using System.Collections.Generic;
using System.Globalization;
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
            var response = SendGetRequest(listener, HttpClientAddress);
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
            var response = SendGetRequest(listener, HttpClientAddress);
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

            var response = SendGetRequest(listener, HttpClientAddress);
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
                Assert.Throws<AggregateException>(() => SendGetRequest(listener, HttpClientAddress));
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
                Assert.Throws<AggregateException>(() => SendGetRequest(listener, HttpClientAddress));
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
            var listener = CreateServerSync(env =>
            {
                Assert.AreEqual("", env["owin.RequestPathBase"]);
                Assert.AreEqual(expectedPath, env["owin.RequestPath"]);
                Assert.AreEqual(expectedQuery, env["owin.RequestQueryString"]);
            });
            using (listener)
            {
                var client = new HttpClient();
                var result = client.GetAsync(clientString).Result;
                Assert.AreEqual(HttpStatusCode.OK, result.StatusCode);
            }
        }

        [Test]
        public void CallParametersEmptyGetRequest()
        {
            var listener = CreateServerSync(
                env =>
                {
                    Assert.NotNull(env);
                    Assert.NotNull(env.Get<Stream>("owin.RequestBody"));
                    Assert.NotNull(env.Get<Stream>("owin.ResponseBody"));
                    Assert.NotNull(env.Get<IDictionary<string, string[]>>("owin.RequestHeaders"));
                    Assert.NotNull(env.Get<IDictionary<string, string[]>>("owin.ResponseHeaders"));
                });

            SendGetRequest(listener, HttpClientAddress);
        }

        [Test]
        public void EnvironmentEmptyGetRequest()
        {
            var listener = CreateServerSync(
                env =>
                {
                    object ignored;
                    Assert.True(env.TryGetValue("owin.RequestMethod", out ignored));
                    Assert.AreEqual("GET", env["owin.RequestMethod"]);

                    Assert.True(env.TryGetValue("owin.RequestPath", out ignored));
                    Assert.AreEqual("/SubPath", env["owin.RequestPath"]);

                    Assert.True(env.TryGetValue("owin.RequestPathBase", out ignored));
                    Assert.AreEqual("", env["owin.RequestPathBase"]);

                    Assert.True(env.TryGetValue("owin.RequestProtocol", out ignored));
                    Assert.AreEqual("HTTP/1.1", env["owin.RequestProtocol"]);

                    Assert.True(env.TryGetValue("owin.RequestQueryString", out ignored));
                    Assert.AreEqual("QueryString", env["owin.RequestQueryString"]);

                    Assert.True(env.TryGetValue("owin.RequestScheme", out ignored));
                    Assert.AreEqual("http", env["owin.RequestScheme"]);

                    Assert.True(env.TryGetValue("owin.Version", out ignored));
                    Assert.AreEqual("1.0", env["owin.Version"]);
                });

            SendGetRequest(listener, HttpClientAddress + "SubPath?QueryString");
        }

        [Test]
        public void EnvironmentPost10Request()
        {
            var listener = CreateServerSync(
                env =>
                {
                    object ignored;
                    Assert.True(env.TryGetValue("owin.RequestMethod", out ignored));
                    Assert.AreEqual("POST", env["owin.RequestMethod"]);

                    Assert.True(env.TryGetValue("owin.RequestPath", out ignored));
                    Assert.AreEqual("/SubPath", env["owin.RequestPath"]);

                    Assert.True(env.TryGetValue("owin.RequestPathBase", out ignored));
                    Assert.AreEqual("", env["owin.RequestPathBase"]);

                    Assert.True(env.TryGetValue("owin.RequestProtocol", out ignored));
                    Assert.AreEqual("HTTP/1.0", env["owin.RequestProtocol"]);

                    Assert.True(env.TryGetValue("owin.RequestQueryString", out ignored));
                    Assert.AreEqual("QueryString", env["owin.RequestQueryString"]);

                    Assert.True(env.TryGetValue("owin.RequestScheme", out ignored));
                    Assert.AreEqual("http", env["owin.RequestScheme"]);

                    Assert.True(env.TryGetValue("owin.Version", out ignored));
                    Assert.AreEqual("1.0", env["owin.Version"]);
                });

            var request = new HttpRequestMessage(HttpMethod.Post, HttpClientAddress + "SubPath?QueryString")
                {
                    Content = new StringContent("Hello World"),
                    Version = new Version(1, 0)
                };
            SendRequest(listener, request);
        }

        [Test]
        public void HeadersEmptyGetRequest()
        {
            var listener = CreateServerSync(
                env =>
                {
                    var requestHeaders = env.Get<IDictionary<string, string[]>>("owin.RequestHeaders");

                    string[] values;
                    Assert.True(requestHeaders.TryGetValue("host", out values));
                    Assert.AreEqual(1, values.Length);
                    Assert.AreEqual("localhost:8080", values[0]);
                });

            SendGetRequest(listener, HttpClientAddress);
        }

        [Test]
        public void HeadersPostContentLengthRequest()
        {
            const string requestBody = "Hello World";

            var listener = CreateServerSync(
                env =>
                {
                    var requestHeaders = env.Get<IDictionary<string, string[]>>("owin.RequestHeaders");

                    string[] values;

                    Assert.True(requestHeaders.TryGetValue("host", out values));
                    Assert.AreEqual(1, values.Length);
                    Assert.AreEqual("localhost:8080", values[0]);

                    Assert.True(requestHeaders.TryGetValue("Content-length", out values));
                    Assert.AreEqual(1, values.Length);
                    Assert.AreEqual(requestBody.Length.ToString(CultureInfo.InvariantCulture), values[0]);

                    Assert.True(requestHeaders.TryGetValue("exPect", out values));
                    Assert.AreEqual(1, values.Length);
                    Assert.AreEqual("100-continue", values[0]);

                    Assert.True(requestHeaders.TryGetValue("Content-Type", out values));
                    Assert.AreEqual(1, values.Length);
                    Assert.AreEqual("text/plain; charset=utf-8", values[0]);
                });

            var request = new HttpRequestMessage(HttpMethod.Post, HttpClientAddress + "SubPath?QueryString")
                {
                    Content = new StringContent(requestBody)
                };
            SendRequest(listener, request);
        }

        [Test]
        public void HeadersPostChunkedRequest()
        {
            const string requestBody = "Hello World";

            var listener = CreateServerSync(
                env =>
                {
                    var requestHeaders = env.Get<IDictionary<string, string[]>>("owin.RequestHeaders");

                    string[] values;

                    Assert.True(requestHeaders.TryGetValue("host", out values));
                    Assert.AreEqual(1, values.Length);
                    Assert.AreEqual("localhost:8080", values[0]);

                    Assert.True(requestHeaders.TryGetValue("Transfer-encoding", out values));
                    Assert.AreEqual(1, values.Length);
                    Assert.AreEqual("chunked", values[0]);

                    Assert.True(requestHeaders.TryGetValue("exPect", out values));
                    Assert.AreEqual(1, values.Length);
                    Assert.AreEqual("100-continue", values[0]);

                    Assert.True(requestHeaders.TryGetValue("Content-Type", out values));
                    Assert.AreEqual(1, values.Length);
                    Assert.AreEqual("text/plain; charset=utf-8", values[0]);
                });

            var request = new HttpRequestMessage(HttpMethod.Post, HttpClientAddress + "SubPath?QueryString");
            request.Headers.TransferEncodingChunked = true;
            request.Content = new StringContent(requestBody);
            SendRequest(listener, request);
        }

        [Test]
        public void BodyPostContentLengthZero()
        {
            var listener = CreateServerSync(
                env =>
                {
                    string[] values;
                    var requestHeaders = env.Get<IDictionary<string, string[]>>("owin.RequestHeaders");

                    Assert.True(requestHeaders.TryGetValue("Content-length", out values));
                    Assert.AreEqual(1, values.Length);
                    Assert.AreEqual("0", values[0]);

                    Assert.NotNull(env.Get<Stream>("owin.RequestBody"));
                });

            var request = new HttpRequestMessage(HttpMethod.Post, HttpClientAddress)
                {
                    Content = new StringContent("")
                };
            SendRequest(listener, request);
        }

        [Test]
        public void BodyPostContentLengthX()
        {
            var listener = CreateServerSync(
                env =>
                {
                    string[] values;
                    var requestHeaders = env.Get<IDictionary<string, string[]>>("owin.RequestHeaders");

                    Assert.True(requestHeaders.TryGetValue("Content-length", out values));
                    Assert.AreEqual(1, values.Length);
                    Assert.AreEqual("11", values[0]);

                    var requestBody = env.Get<Stream>("owin.RequestBody");
                    Assert.NotNull(requestBody);

                    var buffer = new MemoryStream();
                    requestBody.CopyTo(buffer);
                    Assert.AreEqual(11, buffer.Length);
                });

            var request = new HttpRequestMessage(HttpMethod.Post, HttpClientAddress)
                {
                    Content = new StringContent("Hello World")
                };
            SendRequest(listener, request);
        }

        [Test]
        public void BodyPostChunkedEmpty()
        {
            var listener = CreateServerSync(
                env =>
                {
                    string[] values;
                    var requestHeaders = env.Get<IDictionary<string, string[]>>("owin.RequestHeaders");

                    Assert.True(requestHeaders.TryGetValue("Transfer-Encoding", out values));
                    Assert.AreEqual(1, values.Length);
                    Assert.AreEqual("chunked", values[0]);

                    var requestBody = env.Get<Stream>("owin.RequestBody");
                    Assert.NotNull(requestBody);

                    var buffer = new MemoryStream();
                    requestBody.CopyTo(buffer);
                    Assert.AreEqual(0, buffer.Length);
                });

            var request = new HttpRequestMessage(HttpMethod.Post, HttpClientAddress);
            request.Headers.TransferEncodingChunked = true;
            request.Content = new StringContent("");
            SendRequest(listener, request);
        }

        [Test]
        public void BodyPostChunkedX()
        {
            var listener = CreateServerSync(
                env =>
                {
                    string[] values;
                    var requestHeaders = env.Get<IDictionary<string, string[]>>("owin.RequestHeaders");

                    Assert.True(requestHeaders.TryGetValue("Transfer-Encoding", out values));
                    Assert.AreEqual(1, values.Length);
                    Assert.AreEqual("chunked", values[0]);

                    var requestBody = env.Get<Stream>("owin.RequestBody");
                    Assert.NotNull(requestBody);

                    var buffer = new MemoryStream();
                    requestBody.CopyTo(buffer);
                    Assert.AreEqual(11, buffer.Length);
                });

            var request = new HttpRequestMessage(HttpMethod.Post, HttpClientAddress);
            request.Headers.TransferEncodingChunked = true;
            request.Content = new StringContent("Hello World");
            SendRequest(listener, request);
        }

        void SendRequest(IDisposable listener, HttpRequestMessage request)
        {
            using (listener)
            {
                var client = new HttpClient();
                var result = client.SendAsync(request).Result;
                result.EnsureSuccessStatusCode();
            }
        }

        static CancellationToken GetCallCancelled(IDictionary<string, object> env)
        {
            return env.Get<CancellationToken>("owin.CallCancelled");
        }

        static HttpResponseMessage SendGetRequest(IDisposable listener, string address)
        {
            using (listener)
            {
                var handler = new WebRequestHandler
                    {
                        ServerCertificateValidationCallback = (a, b, c, d) => true,
                        ClientCertificateOptions = ClientCertificateOption.Automatic
                    };
                var client = new HttpClient(handler);
                return client.GetAsync(address).Result;
            }
        }

        static Server CreateServer(AppFunc app)
        {
            var server = new Server(10);
            server.Start(new IPEndPoint(IPAddress.Loopback, 8080), app);
            return server;
        }

        static Server CreateServerSync(Action<IDictionary<string, object>> appSync)
        {
            return CreateServer(env =>
                {
                    appSync(env);
                    return Task.Delay(0);
                });
        }
    }
}
