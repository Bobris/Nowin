using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
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
        const string HostValue = "localhost:8080";
        const string SampleContent = "Hello World";

        readonly AppFunc _appThrow = env => { throw new InvalidOperationException(); };

        [Test]
        public void NowinTrivial()
        {
            using (CreateServer(_appThrow))
            {
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
                Assert.AreEqual("", result);
                result = client.GetStringAsync(HttpClientAddress).Result;
                Assert.AreEqual("", result);
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
                const string dataString = SampleContent;
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
        [TestCase("", "/", "")]
        [TestCase("path?query", "/path", "query")]
        [TestCase("pathBase/path?query", "/pathBase/path", "query")]
        public void PathAndQueryParsing(string clientString, string expectedPath, string expectedQuery)
        {
            clientString = HttpClientAddress + clientString;
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
                    Content = new StringContent(SampleContent),
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
                    Assert.AreEqual(HostValue, values[0]);
                });

            SendGetRequest(listener, HttpClientAddress);
        }

        [Test]
        public void HeadersPostContentLengthRequest()
        {
            const string requestBody = SampleContent;

            var listener = CreateServerSync(
                env =>
                {
                    var requestHeaders = env.Get<IDictionary<string, string[]>>("owin.RequestHeaders");

                    string[] values;

                    Assert.True(requestHeaders.TryGetValue("host", out values));
                    Assert.AreEqual(1, values.Length);
                    Assert.AreEqual(HostValue, values[0]);

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
            const string requestBody = SampleContent;

            var listener = CreateServerSync(
                env =>
                {
                    var requestHeaders = env.Get<IDictionary<string, string[]>>("owin.RequestHeaders");

                    string[] values;

                    Assert.True(requestHeaders.TryGetValue("host", out values));
                    Assert.AreEqual(1, values.Length);
                    Assert.AreEqual(HostValue, values[0]);

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
                    Assert.AreEqual(SampleContent.Length.ToString(CultureInfo.InvariantCulture), values[0]);

                    var requestBody = env.Get<Stream>("owin.RequestBody");
                    Assert.NotNull(requestBody);

                    var buffer = new MemoryStream();
                    requestBody.CopyTo(buffer);
                    Assert.AreEqual(SampleContent.Length, buffer.Length);
                });

            var request = new HttpRequestMessage(HttpMethod.Post, HttpClientAddress)
                {
                    Content = new StringContent(SampleContent)
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
                    Assert.AreEqual(SampleContent.Length, buffer.Length);
                });

            var request = new HttpRequestMessage(HttpMethod.Post, HttpClientAddress);
            request.Headers.TransferEncodingChunked = true;
            request.Content = new StringContent(SampleContent);
            SendRequest(listener, request);
        }

        [Test]
        public void BodyPostChunkedXClientCloseConnection()
        {
            var listener = CreateServerSync(
                env =>
                {
                    var requestBody = env.Get<Stream>("owin.RequestBody");
                    var buffer = new MemoryStream();
                    Assert.Throws<AggregateException>(() => requestBody.CopyTo(buffer));
                    Assert.True(GetCallCancelled(env).IsCancellationRequested);
                });

            var request = new HttpRequestMessage(HttpMethod.Post, HttpClientAddress);
            request.Headers.TransferEncodingChunked = true;
            request.Content = new StreamContent(new ReadZerosAndThrowAfter(100000));
            using (listener)
            {
                using (var client = new HttpClient())
                {
                    Assert.Throws<AggregateException>(() => client.SendAsync(request, HttpCompletionOption.ResponseContentRead).Wait());
                }
            }
        }

        class ReadZerosAndThrowAfter : Stream
        {
            readonly int _length;
            int _pos;

            public ReadZerosAndThrowAfter(int length)
            {
                _length = length;
            }

            public override void Flush()
            {
                throw new InvalidOperationException();
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                throw new InvalidOperationException();
            }

            public override void SetLength(long value)
            {
                throw new InvalidOperationException();
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (_pos + count >= _length) throw new EndOfStreamException();
                Array.Clear(buffer, offset, count);
                _pos += count;
                return count;
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                throw new InvalidOperationException();
            }

            public override bool CanRead
            {
                get { return true; }
            }

            public override bool CanSeek
            {
                get { return false; }
            }

            public override bool CanWrite
            {
                get { return false; }
            }

            public override long Length
            {
                get { return _length * 2; }
            }

            public override long Position
            {
                get { return _pos; }
                set { throw new InvalidOperationException(); }
            }
        }

        [Test]
        public void DefaultEmptyResponse()
        {
            var listener = CreateServer(call => Task.Delay(0));

            using (listener)
            {
                var client = new HttpClient();
                var response = client.GetAsync(HttpClientAddress).Result;
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                Assert.AreEqual("OK", response.ReasonPhrase);
                Assert.AreEqual(0, response.Headers.Count());
                Assert.False(response.Headers.TransferEncodingChunked.HasValue);
                Assert.False(response.Headers.Date.HasValue);
                Assert.AreEqual(0, response.Headers.Server.Count);
                Assert.AreEqual("", response.Content.ReadAsStringAsync().Result);
            }
        }

        [Test]
        public void SurviveNullResponseHeaders()
        {
            var listener = CreateServer(
                env =>
                {
                    env["owin.ResponseHeaders"] = null;
                    return Task.Delay(0);
                });

            using (listener)
            {
                var client = new HttpClient();
                var response = client.GetAsync(HttpClientAddress).Result;
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            }
        }

        [Test]
        public void CustomHeadersArePassedThrough()
        {
            var listener = CreateServer(
                env =>
                {
                    var responseHeaders = env.Get<IDictionary<string, string[]>>("owin.ResponseHeaders");
                    responseHeaders.Add("Custom1", new[] { "value1a", "value1b" });
                    responseHeaders.Add("Custom2", new[] { "value2a, value2b" });
                    responseHeaders.Add("Custom3", new[] { "value3a, value3b", "value3c" });
                    return Task.Delay(0);
                });

            using (listener)
            {
                var client = new HttpClient();
                var response = client.GetAsync(HttpClientAddress).Result;
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                Assert.AreEqual(3, response.Headers.Count());

                Assert.AreEqual(2, response.Headers.GetValues("Custom1").Count());
                Assert.AreEqual("value1a", response.Headers.GetValues("Custom1").First());
                Assert.AreEqual("value1b", response.Headers.GetValues("Custom1").Skip(1).First());
                Assert.AreEqual(1, response.Headers.GetValues("Custom2").Count());
                Assert.AreEqual("value2a, value2b", response.Headers.GetValues("Custom2").First());
                Assert.AreEqual(2, response.Headers.GetValues("Custom3").Count());
                Assert.AreEqual("value3a, value3b", response.Headers.GetValues("Custom3").First());
                Assert.AreEqual("value3c", response.Headers.GetValues("Custom3").Skip(1).First());
            }
        }

        [Test]
        public void ReservedHeadersArePassedThrough()
        {
            var listener = CreateServer(
                env =>
                {
                    var responseHeaders = env.Get<IDictionary<string, string[]>>("owin.ResponseHeaders");
                    env.Add("owin.ResponseProtocol", "HTTP/1.0");
                    responseHeaders.Add("KEEP-alive", new[] { "TRUE" });
                    responseHeaders.Add("content-length", new[] { "0" });
                    responseHeaders.Add("www-Authenticate", new[] { "Basic", "NTLM" });
                    return Task.Delay(0);
                });

            using (listener)
            {
                var client = new HttpClient();
                var response = client.GetAsync(HttpClientAddress).Result;
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                Assert.AreEqual(2, response.Headers.Count());
                Assert.AreEqual(0, response.Content.Headers.ContentLength);
                Assert.AreEqual(2, response.Headers.WwwAuthenticate.Count());

                // The client does not expose KeepAlive
            }
        }

        [Test]
        public void ConnectionHeaderIsHonoredAndTransferEncodingIngnored()
        {
            var listener = CreateServer(
                env =>
                {
                    var responseHeaders = env.Get<IDictionary<string, string[]>>("owin.ResponseHeaders");
                    responseHeaders.Add("Transfer-Encoding", new[] { "ChUnKed" });
                    responseHeaders.Add("CONNECTION", new[] { "ClOsE" });
                    return Task.Delay(0);
                });

            using (listener)
            {
                var client = new HttpClient();
                var response = client.GetAsync(HttpClientAddress).Result;
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                Assert.AreEqual(1, response.Headers.Count());
                Assert.AreEqual("", response.Headers.TransferEncoding.ToString());
                Assert.False(response.Headers.TransferEncodingChunked.HasValue);
                Assert.AreEqual("close", response.Headers.Connection.First()); // Normalized by server
                Assert.NotNull(response.Headers.ConnectionClose);
                Assert.True(response.Headers.ConnectionClose.Value);
            }
        }

        [Test]
        public void BadContentLengthIs500()
        {
            var listener = CreateServer(
                env =>
                {
                    var responseHeaders = env.Get<IDictionary<string, string[]>>("owin.ResponseHeaders");
                    responseHeaders.Add("content-length", new[] { "-10" });
                    return Task.Delay(0);
                });

            using (listener)
            {
                var client = new HttpClient();
                var response = client.GetAsync(HttpClientAddress).Result;
                Assert.AreEqual(HttpStatusCode.InternalServerError, response.StatusCode);
                Assert.NotNull(response.Content.Headers.ContentLength);
                Assert.AreEqual(0, response.Content.Headers.ContentLength.Value);
            }
        }

        [Test]
        public void CustomReasonPhraseSupported()
        {
            var listener = CreateServer(
                env =>
                {
                    env.Add("owin.ResponseReasonPhrase", SampleContent);
                    return Task.Delay(0);
                });

            using (listener)
            {
                var client = new HttpClient();
                var response = client.GetAsync(HttpClientAddress).Result;
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                Assert.AreEqual(SampleContent, response.ReasonPhrase);
            }
        }

        [Test]
        public void BadReasonPhraseIs500()
        {
            var listener = CreateServer(
                env =>
                {
                    env.Add("owin.ResponseReasonPhrase", int.MaxValue);
                    return Task.Delay(0);
                });

            using (listener)
            {
                var client = new HttpClient();
                var response = client.GetAsync(HttpClientAddress).Result;
                Assert.AreEqual(HttpStatusCode.InternalServerError, response.StatusCode);
            }
        }

        [Test]
        public void ResponseProtocolIsIgnored()
        {
            var listener = CreateServer(
                env =>
                {
                    env.Add("owin.ResponseProtocol", "garbage");
                    return Task.Delay(0);
                });

            using (listener)
            {
                var client = new HttpClient();
                var response = client.GetAsync(HttpClientAddress).Result;
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                Assert.AreEqual(new Version(1, 1), response.Version);
            }
        }

        [Test]
        public void SmallResponseBodyWorks()
        {
            var listener = CreateServer(
                env =>
                {
                    var responseStream = env.Get<Stream>("owin.ResponseBody");
                    responseStream.Write(new byte[10], 0, 10);
                    return Task.Delay(0);
                });

            using (listener)
            {
                var client = new HttpClient();
                var response = client.GetAsync(HttpClientAddress).Result;
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                Assert.AreEqual(10, response.Content.ReadAsByteArrayAsync().Result.Length);
            }
        }

        [Test]
        public void LargeResponseBodyWorks()
        {
            var listener = CreateServer(
                async env =>
                {
                    var responseStream = env.Get<Stream>("owin.ResponseBody");
                    for (var i = 0; i < 100; i++)
                    {
                        await responseStream.WriteAsync(new byte[1000], 0, 1000);
                    }
                });

            using (listener)
            {
                var client = new HttpClient();
                var response = client.GetAsync(HttpClientAddress).Result;
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                Assert.AreEqual(100 * 1000, response.Content.ReadAsByteArrayAsync().Result.Length);
            }
        }

        [Test]
        public void BodySmallerThanContentLengthClosesConnection()
        {
            var listener = CreateServer(
                env =>
                {
                    var responseHeaders = env.Get<IDictionary<string, string[]>>("owin.ResponseHeaders");
                    responseHeaders.Add("Content-Length", new[] { "10000" });
                    var responseStream = env.Get<Stream>("owin.ResponseBody");
                    responseStream.Write(new byte[9500], 0, 9500);
                    return Task.Delay(0);
                });

            using (listener)
            {
                var client = new HttpClient();
                Assert.Throws<AggregateException>(() => client.GetAsync(HttpClientAddress).Wait());
            }
        }

        [Test]
        public void BodyLargerThanContentLengthClosesConnection()
        {
            var listener = CreateServer(
                env =>
                {
                    var responseHeaders = env.Get<IDictionary<string, string[]>>("owin.ResponseHeaders");
                    responseHeaders.Add("Content-Length", new[] { "10000" });
                    var responseStream = env.Get<Stream>("owin.ResponseBody");
                    responseStream.Write(new byte[10500], 0, 10500);
                    return Task.Delay(0);
                });

            using (listener)
            {
                var client = new HttpClient();
                Assert.Throws<AggregateException>(() => client.GetAsync(HttpClientAddress).Wait());
            }
        }

        [Test]
        public void StatusesLessThan200AreInvalid()
        {
            var listener = CreateServer(
                env =>
                {
                    env["owin.ResponseStatusCode"] = 100;
                    return Task.Delay(0);
                });

            using (listener)
            {
                var client = new HttpClient();
                var response = client.PostAsync(HttpClientAddress, new StringContent(SampleContent)).Result;
                Assert.AreEqual(HttpStatusCode.InternalServerError, response.StatusCode);
            }
        }

        static void SendRequest(IDisposable listener, HttpRequestMessage request)
        {
            using (listener)
            {
                using (var client = new HttpClient())
                {
                    var result = client.SendAsync(request, HttpCompletionOption.ResponseContentRead).Result;
                    result.EnsureSuccessStatusCode();
                }
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
                using (var client = new HttpClient(handler))
                {
                    return client.GetAsync(address, HttpCompletionOption.ResponseContentRead).Result;
                }
            }
        }

        static IDisposable CreateServer(AppFunc app)
        {
            var server = ServerBuilder.New()
                .SetEndPoint(new IPEndPoint(IPAddress.Loopback, 8080))
                .SetOwinApp(app)
                .SetConnectionAllocationStrategy(new ConnectionAllocationStrategy(1, 0, 1, 0))
                .Start();
            return server;
        }

        static IDisposable CreateServerSync(Action<IDictionary<string, object>> appSync)
        {
            return CreateServer(env =>
                {
                    appSync(env);
                    return Task.Delay(0);
                });
        }
    }
}
