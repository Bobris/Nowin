using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace NowinTests
{
    using OwinApp = Func<IDictionary<string, object>, Task>;
    public abstract class NowinTestsBase
    {
        const string HostValue = "localhost:8082";
        const string SampleContent = "Hello World";

        protected abstract string HttpClientAddress { get; }
        protected abstract string ExpectedRequestScheme { get; }

        readonly OwinApp _appThrow = env => { throw new InvalidOperationException(); };

        [Fact]
        public void NowinTrivial()
        {
            using (CreateServer(_appThrow))
            {
            }
        }

        [Fact]
        public void TooManyCookiesReturnsBadRequest()
        {
            var listener = CreateServer(env => Task.Delay(0));
            HttpResponseMessage response;
            using (listener)
            {
                var cookieContainer = new CookieContainer();

                var handler = new WebRequestHandler
                {
                    ServerCertificateValidationCallback = (a, b, c, d) => true,
                    ClientCertificateOptions = ClientCertificateOption.Automatic,
                    CookieContainer = cookieContainer
                };

                string signinMessageCookieValue =
                    "QAAANCMnd8BFdERjHoAwE_Cl-sBAAAAa4ZDCDenmUyKVkQePQm0tQAAAAACAAAAAAADZgAAwAAAABAAAAB1ylI7UbGpVqudfDeuj73fAAAAAASAAACgAAAAEAAAAEV5JAn2-ToGAfj_8kX8t5cIAwAAaH8KSaDGwal8NHo44jVF3czgOVvggha8D49gtkG5nuHG16Wg-i8STQ2oRMrKfKTy00a9rlHqemUb9Qugy87UqR6KCGueQf5IraNdpfyqBqqF4NGEIJOPdgcETC9GcuAcxXsVM5oNTu1WF55KBpDLbGkrfoI_5g5oA5DLYM3ichmVqz9WStSrwVgEKze_MNjxw3Ruq4qCNbevn8nZs-NC_7AYrv6LzYNOd9oLBoJhpkX6AuWombgggtDRsugcVnzDkC63pqsRtWnPL28VaGjYamQUSJjB7WM5go4pq_tM9OGOcX2wseGHsafNa3_LIr5TnJOifkufcWmQrEwJT_DpJAXUm7kBc-YYhD618AksRWFnjJ6b6zomxwTrNwLXzcFHB5YRTKM49NFlO0hBIcpD-JvYhK_1X58UtQiLQedwkv9xGdz8B0xnXDe_XuzlyYRWQg6TFwUEh9r0paGR-ttuoqB_uRh9gYHZhLjWumdDAkg5M4uYyM4mkX1yLxyUKDp46upLjvDjK_yGoTwJ6chO78PLkqg15fDfsp4sOzH4mWBNu9of2vEWnG2G9qB4ijUcD4iqRbtp7sspEelj2hCUWcUuwHxTWzPqiHdmyx-cRfRtS3d0gU03xwakz33y-4-jLo9_g0ND4ERwTaf7eitQmvaig1ILV1y7XyiZ89e-AzFm1q7eAzApFoU5E133FZTwlAIv-WraVCKmX06qOGfVue1Z2tYH5WT52fYKIOKjlS_zMQI1IgsjfxVU73UrzFV8pAoAw76jzEo5dico4Ehrwa6jdFZ8QAgYVfFJa44BiQs1Nk5EMjFt9tr91Fp0XLwBVGZuNMnlVDs-UTSA_xyVGq7MPTYX98aczQW86hzoAX3GmzlkXAJA5JwAMWpV4sC7zlfqaYN34LdfV7PdMka1dtIu5kfammRqWLRCU6Hq0WNgTezrjrITL8LT8x894nnYl75D3g_9xqE9mi81Dp5ziNdh32wJW2uA-RjN5pT4PfwFEdntp__D7HjQWuDl5bg7g82uvhrPuucUAAAAtnJ6YVQTrZeN_2lsyKPenDYZW14";

                var openIdConnectNonce = "QVFBQUFOQ01uZDhCRmRFUmpIb0F3RV9DbC1zQkFBQUFhNFpEQ0Rlbm1VeUtWa1FlUFFtMHRRQUFBQUFDQUFBQUFBQURaZ0FBd0FBQUFCQUFBQUNiZUNpSWFfWHdZbWd5ak1NNE5CMmZBQUFBQUFTQUFBQ2dBQUFBRUFBQUFPTl9qNkRaZjJmSGdEbVExTjlJZkxDQUFBQUF4Q3EwQXZiQVktd1lLX0pQYVprR2V2aFpOMnNhRWE1MTNtSndfekJnUkJtOXViZXRNZ1I1OU9yWjZPM0pIc3VhQ25CTl9hRVJBSklVUF9PX29sS0V3a240Q3l4eTlnbXJlNzRCVmM4TGZIUFVYUXhSTmpnLThFekgxZG1LMEc2d0w4R0d2TXlqS1BxRHJpMFgtUFFrRW84dXppdTVtNk1BRHZKalFtb1BMVFFVQUFBQWhHQ1MxYWFROVBQaS1aNmQyZjB4aHAwQnpPRQ%3D%3D";


                cookieContainer.Add(new Cookie("SignInMessage.91431d224c53cb8dbd4ff3c9817c63e1", signinMessageCookieValue, "", "localhost"));
                cookieContainer.Add(new Cookie("SignInMessage.91431d224c53cb8dbd4ff3c9817c63e2", signinMessageCookieValue, "", "localhost"));
                cookieContainer.Add(new Cookie("SignInMessage.91431d224c53cb8dbd4ff3c9817c63e3", signinMessageCookieValue, "", "localhost"));
                cookieContainer.Add(new Cookie("SignInMessage.91431d224c53cb8dbd4ff3c9817c63e4", signinMessageCookieValue, "", "localhost"));
                cookieContainer.Add(new Cookie("SignInMessage.91431d224c53cb8dbd4ff3c9817c63e5", signinMessageCookieValue, "", "localhost"));
                cookieContainer.Add(new Cookie("OpenIdConnect.nonce.b3Ccpijv%2F67GFS37gwl5rSPNSHVQ%2B8ZziQfKjG67eOo%31", openIdConnectNonce, "" ,"localhost"));
                cookieContainer.Add(new Cookie("OpenIdConnect.nonce.b3Ccpijv%2F67GFS37gwl5rSPNSHVQ%2B8ZziQfKjG67eOo%32", openIdConnectNonce, "", "localhost"));
                cookieContainer.Add(new Cookie("OpenIdConnect.nonce.b3Ccpijv%2F67GFS37gwl5rSPNSHVQ%2B8ZziQfKjG67eOo%33", openIdConnectNonce, "", "localhost"));
                cookieContainer.Add(new Cookie("OpenIdConnect.nonce.b3Ccpijv%2F67GFS37gwl5rSPNSHVQ%2B8ZziQfKjG67eOo%34", openIdConnectNonce, "", "localhost"));
                cookieContainer.Add(new Cookie("OpenIdConnect.nonce.b3Ccpijv%2F67GFS37gwl5rSPNSHVQ%2B8ZziQfKjG67eOo%35", openIdConnectNonce, "" ,"localhost"));

                using (var client = new HttpClient(handler))
                {
                    response = client.GetAsync(HttpClientAddress, HttpCompletionOption.ResponseContentRead).Result;
                }
            }

            
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.NotNull(response.Content.Headers.ContentLength);
            Assert.Equal(0, response.Content.Headers.ContentLength.Value);
            Assert.Equal("Nowin", response.Headers.Server.First().Product.Name);
            Assert.True(response.Headers.Date.HasValue);
        }

        [Fact]
        public void EmptyAppRespondOk()
        {
            var listener = CreateServer(env => Task.Delay(0));
            var response = SendGetRequest(listener, HttpClientAddress);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.NotNull(response.Content.Headers.ContentLength);
            Assert.Equal(0, response.Content.Headers.ContentLength.Value);
            Assert.Equal("Nowin", response.Headers.Server.First().Product.Name);
            Assert.True(response.Headers.Date.HasValue);
        }

        [Fact]
        public void EmptyAppAnd2Requests()
        {
            var listener = CreateServer(env => Task.Delay(0));
            using (listener)
            {
                var client = new HttpClient();
                string result = client.GetStringAsync(HttpClientAddress).Result;
                Assert.Equal("", result);
                result = client.GetStringAsync(HttpClientAddress).Result;
                Assert.Equal("", result);
            }
        }

        [Fact]
        public void ThrowAppRespond500()
        {
            var listener = CreateServer(_appThrow);
            var response = SendGetRequest(listener, HttpClientAddress);
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            Assert.NotNull(response.Content.Headers.ContentLength);
            Assert.Equal(0, response.Content.Headers.ContentLength.Value);
        }

        [Fact]
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
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            Assert.NotNull(response.Content.Headers.ContentLength);
            Assert.Equal(0, response.Content.Headers.ContentLength.Value);
            Assert.True(callCancelled);
        }

        [Fact]
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
                Assert.Equal(dataString.Length, response.Content.Headers.ContentLength.Value);
                Assert.Equal(dataString, response.Content.ReadAsStringAsync().Result);
                Assert.False(callCancelled);
            }
        }

        class BigHttpContent : HttpContent
        {
            protected override async Task SerializeToStreamAsync(Stream stream, TransportContext context)
            {
                var array = new byte[100000];
                for (int i = 0; i < 10; i++)
                {
                    await stream.WriteAsync(array, 0, array.Length);
                    await stream.FlushAsync();
                }
            }

            protected override bool TryComputeLength(out long length)
            {
                length = -1;
                return false;
            }
        }

        [Fact]
        public void PostEchoAppWithLongChunkedDataWorks()
        {
            var callCancelled = false;
            var listener = CreateServer(
                async env =>
                {
                    GetCallCancelled(env).Register(() => callCancelled = true);
                    var requestStream = env.Get<Stream>("owin.RequestBody");
                    var responseStream = env.Get<Stream>("owin.ResponseBody");
                    var buffer = new MemoryStream();
                    await requestStream.CopyToAsync(buffer, 4096);
                    buffer.Seek(0, SeekOrigin.Begin);
                    await buffer.CopyToAsync(responseStream, 4096);
                });

            using (listener)
            {
                var client = new HttpClient();
                var request = new HttpRequestMessage(HttpMethod.Put, HttpClientAddress)
                {
                    Content = new BigHttpContent()
                };
                request.Headers.TransferEncodingChunked = true;

                var response = client.SendAsync(request).Result;
                response.EnsureSuccessStatusCode();
                Assert.Equal(1000000, response.Content.ReadAsByteArrayAsync().Result.Length);
                Assert.False(callCancelled);
            }
        }

        [Fact]
        public void PostEchoAppWithLongChunkedDataTwiceWorks()
        {
            var callCancelled = false;
            var listener = CreateServer(
                async env =>
                {
                    GetCallCancelled(env).Register(() => callCancelled = true);
                    var requestStream = env.Get<Stream>("owin.RequestBody");
                    var responseStream = env.Get<Stream>("owin.ResponseBody");
                    var buffer = new MemoryStream();
                    await requestStream.CopyToAsync(buffer, 4096);
                    buffer.Seek(0, SeekOrigin.Begin);
                    await buffer.CopyToAsync(responseStream, 4096);
                });

            using (listener)
            {
                for (int repeat = 0; repeat < 2; repeat++)
                {
                    using (var client = new HttpClient())
                    {
                        var request = new HttpRequestMessage(HttpMethod.Put, HttpClientAddress)
                        {
                            Content = new BigHttpContent()
                        };
                        request.Headers.TransferEncodingChunked = true;

                        var response = client.SendAsync(request).Result;
                        response.EnsureSuccessStatusCode();
                        Assert.Equal(1000000, response.Content.ReadAsByteArrayAsync().Result.Length);
                        Assert.False(callCancelled);
                    }
                }
            }
        }

        [Fact]
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

        [Fact]
        public void Error500IsAllowedToFinishWhenThrowing()
        {
            var listener = CreateServer(
                env =>
                {
                    env["owin.ResponseStatusCode"] = 500;
                    var responseStream = env.Get<Stream>("owin.ResponseBody");
                    responseStream.WriteByte((byte)'A');
                    responseStream.Flush();

                    throw new InvalidOperationException();
                });
            var response = SendGetRequest(listener, HttpClientAddress);
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            Assert.Equal("A", response.Content.ReadAsStringAsync().Result);
        }

        [Fact]
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

        [Theory]
        [InlineData("", "/", "")]
        [InlineData("path?query", "/path", "query")]
        [InlineData("pathBase/path?query", "/pathBase/path", "query")]
        public void PathAndQueryParsing(string clientString, string expectedPath, string expectedQuery)
        {
            clientString = HttpClientAddress + clientString;
            var listener = CreateServerSync(env =>
                {
                    Assert.Equal("", env["owin.RequestPathBase"]);
                    Assert.Equal(expectedPath, env["owin.RequestPath"]);
                    Assert.Equal(expectedQuery, env["owin.RequestQueryString"]);
                });
            using (listener)
            {
                var client = new HttpClient();
                var result = client.GetAsync(clientString).Result;
                Assert.Equal(HttpStatusCode.OK, result.StatusCode);
            }
        }

        [Fact]
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

        [Fact]
        public void EnvironmentEmptyGetRequest()
        {
            var listener = CreateServerSync(
                env =>
                {
                    object ignored;
                    Assert.True(env.TryGetValue("owin.RequestMethod", out ignored));
                    Assert.Equal("GET", env["owin.RequestMethod"]);

                    Assert.True(env.TryGetValue("owin.RequestPath", out ignored));
                    Assert.Equal("/SubPath", env["owin.RequestPath"]);

                    Assert.True(env.TryGetValue("owin.RequestPathBase", out ignored));
                    Assert.Equal("", env["owin.RequestPathBase"]);

                    Assert.True(env.TryGetValue("owin.RequestProtocol", out ignored));
                    Assert.Equal("HTTP/1.1", env["owin.RequestProtocol"]);

                    Assert.True(env.TryGetValue("owin.RequestQueryString", out ignored));
                    Assert.Equal("QueryString", env["owin.RequestQueryString"]);

                    Assert.True(env.TryGetValue("owin.RequestScheme", out ignored));
                    Assert.Equal(ExpectedRequestScheme, env["owin.RequestScheme"]);

                    Assert.True(env.TryGetValue("owin.Version", out ignored));
                    Assert.Equal("1.0", env["owin.Version"]);

                    Assert.True(env.TryGetValue("server.IsLocal", out ignored));
                    Assert.Equal(true, env["server.IsLocal"]);

                    Assert.True(env.TryGetValue("server.RemoteIpAddress", out ignored));
                    Assert.Equal("127.0.0.1", env["server.RemoteIpAddress"]);

                    Assert.True(env.TryGetValue("server.LocalIpAddress", out ignored));
                    Assert.Equal("127.0.0.1", env["server.LocalIpAddress"]);

                    Assert.True(env.TryGetValue("server.RemotePort", out ignored));
                    Assert.True(env.TryGetValue("server.LocalPort", out ignored));

                    Assert.False(env.TryGetValue("websocket.Accept", out ignored));
                });

            Assert.Equal(HttpStatusCode.OK, SendGetRequest(listener, HttpClientAddress + "SubPath?QueryString").StatusCode);
        }

        [Fact]
        public void EnvironmentPost10Request()
        {
            var listener = CreateServerSync(
                env =>
                {
                    object ignored;
                    Assert.True(env.TryGetValue("owin.RequestMethod", out ignored));
                    Assert.Equal("POST", env["owin.RequestMethod"]);

                    Assert.True(env.TryGetValue("owin.RequestPath", out ignored));
                    Assert.Equal("/SubPath", env["owin.RequestPath"]);

                    Assert.True(env.TryGetValue("owin.RequestPathBase", out ignored));
                    Assert.Equal("", env["owin.RequestPathBase"]);

                    Assert.True(env.TryGetValue("owin.RequestProtocol", out ignored));
                    Assert.Equal("HTTP/1.0", env["owin.RequestProtocol"]);

                    Assert.True(env.TryGetValue("owin.RequestQueryString", out ignored));
                    Assert.Equal("QueryString", env["owin.RequestQueryString"]);

                    Assert.True(env.TryGetValue("owin.RequestScheme", out ignored));
                    Assert.Equal(ExpectedRequestScheme, env["owin.RequestScheme"]);

                    Assert.True(env.TryGetValue("owin.Version", out ignored));
                    Assert.Equal("1.0", env["owin.Version"]);
                });

            var request = new HttpRequestMessage(HttpMethod.Post, HttpClientAddress + "SubPath?QueryString")
            {
                Content = new StringContent(SampleContent),
                Version = new Version(1, 0)
            };
            SendRequest(listener, request);
        }

        [Fact]
        public void HeadersEmptyGetRequest()
        {
            var listener = CreateServerSync(
                env =>
                {
                    var requestHeaders = env.Get<IDictionary<string, string[]>>("owin.RequestHeaders");

                    string[] values;
                    Assert.True(requestHeaders.TryGetValue("host", out values));
                    Assert.Equal(1, values.Length);
                    Assert.Equal(HostValue, values[0]);
                });

            SendGetRequest(listener, HttpClientAddress);
        }

        [Fact]
        public void HeadersPostContentLengthRequest()
        {
            const string requestBody = SampleContent;

            var listener = CreateServerSync(
                env =>
                {
                    var requestHeaders = env.Get<IDictionary<string, string[]>>("owin.RequestHeaders");

                    string[] values;

                    Assert.True(requestHeaders.TryGetValue("host", out values));
                    Assert.Equal(1, values.Length);
                    Assert.Equal(HostValue, values[0]);

                    Assert.True(requestHeaders.TryGetValue("Content-length", out values));
                    Assert.Equal(1, values.Length);
                    Assert.Equal(requestBody.Length.ToString(CultureInfo.InvariantCulture), values[0]);

                    Assert.True(requestHeaders.TryGetValue("exPect", out values));
                    Assert.Equal(1, values.Length);
                    Assert.Equal("100-continue", values[0]);

                    Assert.True(requestHeaders.TryGetValue("Content-Type", out values));
                    Assert.Equal(1, values.Length);
                    Assert.Equal("text/plain; charset=utf-8", values[0]);
                });

            var request = new HttpRequestMessage(HttpMethod.Post, HttpClientAddress + "SubPath?QueryString")
            {
                Content = new StringContent(requestBody)
            };
            SendRequest(listener, request);
        }

        [Theory]
        [InlineData("GET")]
        [InlineData("POST")]
        [InlineData("PUT")]
        [InlineData("DELETE")]
        [InlineData("HEAD")]
        [InlineData("OPTIONS")]
        [InlineData("TRACE")]
        [InlineData("NOWIN")]
        public void HttpMethodWorks(string name)
        {
            var listener = CreateServerSync(
                env => Assert.Equal(name, env.Get<string>("owin.RequestMethod")));
            var request = new HttpRequestMessage(new HttpMethod(name), HttpClientAddress);
            SendRequest(listener, request);
        }

        [Fact]
        public void HeadersPostChunkedRequest()
        {
            const string requestBody = SampleContent;

            var listener = CreateServerSync(
                env =>
                {
                    var requestHeaders = env.Get<IDictionary<string, string[]>>("owin.RequestHeaders");

                    string[] values;

                    Assert.True(requestHeaders.TryGetValue("host", out values));
                    Assert.Equal(1, values.Length);
                    Assert.Equal(HostValue, values[0]);

                    Assert.True(requestHeaders.TryGetValue("Transfer-encoding", out values));
                    Assert.Equal(1, values.Length);
                    Assert.Equal("chunked", values[0]);

                    Assert.True(requestHeaders.TryGetValue("exPect", out values));
                    Assert.Equal(1, values.Length);
                    Assert.Equal("100-continue", values[0]);

                    Assert.True(requestHeaders.TryGetValue("Content-Type", out values));
                    Assert.Equal(1, values.Length);
                    Assert.Equal("text/plain; charset=utf-8", values[0]);
                });

            var request = new HttpRequestMessage(HttpMethod.Post, HttpClientAddress + "SubPath?QueryString");
            request.Headers.TransferEncodingChunked = true;
            request.Content = new StringContent(requestBody);
            SendRequest(listener, request);
        }

        [Fact]
        public void BodyPostContentLengthZero()
        {
            var listener = CreateServerSync(
                env =>
                {
                    string[] values;
                    var requestHeaders = env.Get<IDictionary<string, string[]>>("owin.RequestHeaders");

                    Assert.True(requestHeaders.TryGetValue("Content-length", out values));
                    Assert.Equal(1, values.Length);
                    Assert.Equal("0", values[0]);

                    Assert.NotNull(env.Get<Stream>("owin.RequestBody"));
                });

            var request = new HttpRequestMessage(HttpMethod.Post, HttpClientAddress)
            {
                Content = new StringContent("")
            };
            SendRequest(listener, request);
        }

        [Fact]
        public void BodyPostContentLengthX()
        {
            var listener = CreateServerSync(
                env =>
                {
                    string[] values;
                    var requestHeaders = env.Get<IDictionary<string, string[]>>("owin.RequestHeaders");

                    Assert.True(requestHeaders.TryGetValue("Content-length", out values));
                    Assert.Equal(1, values.Length);
                    Assert.Equal(SampleContent.Length.ToString(CultureInfo.InvariantCulture), values[0]);

                    var requestBody = env.Get<Stream>("owin.RequestBody");
                    Assert.NotNull(requestBody);

                    var buffer = new MemoryStream();
                    requestBody.CopyTo(buffer);
                    Assert.Equal(SampleContent.Length, buffer.Length);
                });

            var request = new HttpRequestMessage(HttpMethod.Post, HttpClientAddress)
            {
                Content = new StringContent(SampleContent)
            };
            SendRequest(listener, request);
        }

        [Fact]
        public void BodyPostChunkedEmpty()
        {
            var listener = CreateServerSync(
                env =>
                {
                    string[] values;
                    var requestHeaders = env.Get<IDictionary<string, string[]>>("owin.RequestHeaders");

                    Assert.True(requestHeaders.TryGetValue("Transfer-Encoding", out values));
                    Assert.Equal(1, values.Length);
                    Assert.Equal("chunked", values[0]);

                    var requestBody = env.Get<Stream>("owin.RequestBody");
                    Assert.NotNull(requestBody);

                    var buffer = new MemoryStream();
                    requestBody.CopyTo(buffer);
                    Assert.Equal(0, buffer.Length);
                });

            var request = new HttpRequestMessage(HttpMethod.Post, HttpClientAddress);
            request.Headers.TransferEncodingChunked = true;
            request.Content = new StringContent("");
            SendRequest(listener, request);
        }

        [Fact]
        public void BodyPostChunkedX()
        {
            var listener = CreateServerSync(
                env =>
                {
                    string[] values;
                    var requestHeaders = env.Get<IDictionary<string, string[]>>("owin.RequestHeaders");

                    Assert.True(requestHeaders.TryGetValue("Transfer-Encoding", out values));
                    Assert.Equal(1, values.Length);
                    Assert.Equal("chunked", values[0]);

                    var requestBody = env.Get<Stream>("owin.RequestBody");
                    Assert.NotNull(requestBody);

                    var buffer = new MemoryStream();
                    requestBody.CopyTo(buffer);
                    Assert.Equal(SampleContent.Length, buffer.Length);
                });

            var request = new HttpRequestMessage(HttpMethod.Post, HttpClientAddress);
            request.Headers.TransferEncodingChunked = true;
            request.Content = new StringContent(SampleContent);
            SendRequest(listener, request);
        }

        [Fact]
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

        [Fact]
        public void DefaultEmptyResponse()
        {
            var listener = CreateServer(call => Task.Delay(0));

            using (listener)
            {
                var client = new HttpClient();
                var response = client.GetAsync(HttpClientAddress).Result;
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.Equal("OK", response.ReasonPhrase);
                Assert.Equal(2, response.Headers.Count());
                Assert.False(response.Headers.TransferEncodingChunked.HasValue);
                Assert.True(response.Headers.Date.HasValue);
                Assert.Equal(1, response.Headers.Server.Count);
                Assert.Equal("", response.Content.ReadAsStringAsync().Result);
            }
        }

        [Fact]
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
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }
        }

        [Fact]
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
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.Equal(5, response.Headers.Count());

                Assert.Equal(2, response.Headers.GetValues("Custom1").Count());
                Assert.Equal("value1a", response.Headers.GetValues("Custom1").First());
                Assert.Equal("value1b", response.Headers.GetValues("Custom1").Skip(1).First());
                Assert.Equal(1, response.Headers.GetValues("Custom2").Count());
                Assert.Equal("value2a, value2b", response.Headers.GetValues("Custom2").First());
                Assert.Equal(2, response.Headers.GetValues("Custom3").Count());
                Assert.Equal("value3a, value3b", response.Headers.GetValues("Custom3").First());
                Assert.Equal("value3c", response.Headers.GetValues("Custom3").Skip(1).First());
            }
        }

        [Fact]
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
                    responseHeaders.Add("server", new[] { "cool" });

                    return Task.Delay(0);
                });

            using (listener)
            {
                var client = new HttpClient();
                var response = client.GetAsync(HttpClientAddress).Result;
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.Equal(4, response.Headers.Count());
                Assert.Equal(0, response.Content.Headers.ContentLength);
                Assert.Equal(2, response.Headers.WwwAuthenticate.Count());
                Assert.Equal("cool", response.Headers.Server.First().Product.Name);

                // The client does not expose KeepAlive
            }
        }

        [Fact]
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
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.Equal(3, response.Headers.Count());
                Assert.Equal("", response.Headers.TransferEncoding.ToString());
                Assert.False(response.Headers.TransferEncodingChunked.HasValue);
                Assert.Equal("close", response.Headers.Connection.First()); // Normalized by server
                Assert.NotNull(response.Headers.ConnectionClose);
                Assert.True(response.Headers.ConnectionClose.Value);
            }
        }

        [Fact]
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
                Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
                Assert.NotNull(response.Content.Headers.ContentLength);
                Assert.Equal(0, response.Content.Headers.ContentLength.Value);
            }
        }

        [Fact]
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
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.Equal(SampleContent, response.ReasonPhrase);
            }
        }

        [Fact]
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
                Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            }
        }

        [Fact]
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
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.Equal(new Version(1, 1), response.Version);
            }
        }

        [Fact]
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
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.Equal(10, response.Content.ReadAsByteArrayAsync().Result.Length);
            }
        }

        [Fact]
        public void TwiceSmallResponseBodyWorks()
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
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.Equal(10, response.Content.ReadAsByteArrayAsync().Result.Length);
                response.Dispose();
                client.Dispose();
                client = new HttpClient();
                response = client.GetAsync(HttpClientAddress).Result;
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.Equal(10, response.Content.ReadAsByteArrayAsync().Result.Length);
            }
        }

        [Fact]
        public void HeadMethodEmptyBodyWithContentLength()
        {
            var listener = CreateServer(
                env =>
                {
                    var responseHeaders = env.Get<IDictionary<string, string[]>>("owin.ResponseHeaders");
                    responseHeaders.Add("Content-Length", new[] { "10" });
                    return Task.Delay(0);
                });

            using (listener)
            {
                using (var client = new HttpClient())
                {
                    var request = new HttpRequestMessage(HttpMethod.Head, HttpClientAddress);
                    var response = client.SendAsync(request).Result;
                    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                    Assert.Equal(10, response.Content.Headers.ContentLength);
                    response.Dispose();
                }
            }
        }

        [Fact]
        public void HeadMethodWithLongBodyWillNotSendIt()
        {
            var listener = CreateServer(
                async env =>
                {
                    var responseHeaders = env.Get<IDictionary<string, string[]>>("owin.ResponseHeaders");
                    responseHeaders.Add("Content-Length", new[] { "10" });
                    var responseStream = env.Get<Stream>("owin.ResponseBody");
                    await responseStream.WriteAsync(new byte[10000], 0, 10000);
                });

            using (listener)
            {
                using (var client = new HttpClient())
                {
                    var request = new HttpRequestMessage(HttpMethod.Head, HttpClientAddress);
                    var response = client.SendAsync(request).Result;
                    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                    Assert.Equal(10, response.Content.Headers.ContentLength);
                    response.Dispose();
                }
            }
        }

        [Fact]
        public void HeadMethodWithLongBodyWillNotSendItUnknownLength()
        {
            var listener = CreateServer(
                async env =>
                {
                    var responseStream = env.Get<Stream>("owin.ResponseBody");
                    await responseStream.WriteAsync(new byte[10000], 0, 10000);
                });

            using (listener)
            {
                using (var client = new HttpClient())
                {
                    var request = new HttpRequestMessage(HttpMethod.Head, HttpClientAddress);
                    var response = client.SendAsync(request).Result;
                    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                    Assert.Equal(0, response.Content.Headers.ContentLength);
                    Assert.True(response.Headers.TransferEncodingChunked.HasValue);
                    response.Dispose();
                }
            }
        }

        [Fact]
        public void HeadMethodWithoutBodyWillBeChunkedBecauseOfUnknownLength()
        {
            var listener = CreateServer(
                env => Task.Delay(0));

            using (listener)
            {
                using (var client = new HttpClient())
                {
                    var request = new HttpRequestMessage(HttpMethod.Head, HttpClientAddress);
                    var response = client.SendAsync(request).Result;
                    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                    Assert.Equal(0, response.Content.Headers.ContentLength);
                    Assert.True(response.Headers.TransferEncodingChunked.HasValue);
                    response.Dispose();
                }
            }
        }

        [Fact]
        public void LargeResponseBodyWith100AsyncWritesWorks()
        {
            OwinApp app = async env =>
                {
                    var responseStream = env.Get<Stream>("owin.ResponseBody");
                    var pos = 0;
                    for (var i = 0; i < 100; i++)
                    {
                        var buffer = PrepareLargeResponseBuffer(ref pos, 1000);
                        await responseStream.WriteAsync(buffer, 0, buffer.Length);
                    }
                };
            CheckLargeBody(app);
        }

        [Fact]
        public void LargeResponseBodyWith1AsyncWriteWorks()
        {
            OwinApp app = async env =>
                {
                    var responseStream = env.Get<Stream>("owin.ResponseBody");
                    var pos = 0;
                    var buffer = PrepareLargeResponseBuffer(ref pos, 100000);
                    await responseStream.WriteAsync(buffer, 0, buffer.Length);
                };
            CheckLargeBody(app);
        }

        [Fact]
        public void LargeResponseBodyWith100WritesWorks()
        {
            OwinApp app = async env =>
            {
                await Task.Delay(1);
                var responseStream = env.Get<Stream>("owin.ResponseBody");
                var pos = 0;
                for (var i = 0; i < 100; i++)
                {
                    var buffer = PrepareLargeResponseBuffer(ref pos, 1000);
                    responseStream.Write(buffer, 0, buffer.Length);
                }
            };
            CheckLargeBody(app);
        }

        [Fact]
        public void LargeResponseBodyWith1WriteWorks()
        {
            OwinApp app = async env =>
            {
                await Task.Delay(1);
                var responseStream = env.Get<Stream>("owin.ResponseBody");
                var pos = 0;
                var buffer = PrepareLargeResponseBuffer(ref pos, 100000);
                responseStream.Write(buffer, 0, buffer.Length);
            };
            CheckLargeBody(app);
        }

        [Fact]
        public void LargeResponseBodyWithFlushAsyncAnd1WriteWorks()
        {
            OwinApp app = async env =>
            {
                var responseStream = env.Get<Stream>("owin.ResponseBody");
                await responseStream.FlushAsync();
                var pos = 0;
                var buffer = PrepareLargeResponseBuffer(ref pos, 100000);
                responseStream.Write(buffer, 0, buffer.Length);
            };
            CheckLargeBody(app);
        }

        [Fact]
        public void LargeResponseBodyWithFlushAnd1WriteWorks()
        {
            OwinApp app = env =>
            {
                var responseStream = env.Get<Stream>("owin.ResponseBody");
                responseStream.Flush();
                var pos = 0;
                var buffer = PrepareLargeResponseBuffer(ref pos, 100000);
                responseStream.Write(buffer, 0, buffer.Length);
                return Task.Delay(0);
            };
            CheckLargeBody(app);
        }

        [Fact]
        public void LargeResponseBodyWithFlushAnd1WriteAsyncWorks()
        {
            OwinApp app = async env =>
            {
                var responseStream = env.Get<Stream>("owin.ResponseBody");
                responseStream.Flush();
                var pos = 0;
                var buffer = PrepareLargeResponseBuffer(ref pos, 100000);
                await responseStream.WriteAsync(buffer, 0, buffer.Length);
            };
            CheckLargeBody(app);
        }

        static byte[] PrepareLargeResponseBuffer(ref int pos, int len)
        {
            var buffer = new byte[len];
            for (var i = 0; i < buffer.Length; i++)
            {
                buffer[i] = (byte)(pos & 0xff);
                pos++;
            }
            return buffer;
        }

        void CheckLargeBody(OwinApp app)
        {
            var listener = CreateServer(app);
            using (listener)
            {
                var client = new HttpClient();
                var response = client.GetAsync(HttpClientAddress).Result;
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                var result = response.Content.ReadAsByteArrayAsync().Result;
                Assert.Equal(100000, result.Length);
                for (var i = 0; i < result.Length; i++)
                {
                    if (result[i] != (i & 0xff)) Assert.True(false,string.Format("Response is wrong on {0} byte", i));
                }
            }
        }


        [Fact]
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

        [Fact]
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

        [Fact]
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
                Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            }
        }

        [Fact]
        public void BasicOnSendingHeadersWorks()
        {
            var listener = CreateServer(
                env =>
                {
                    env["owin.ResponseReasonPhrase"] = "Custom";
                    var responseStream = env.Get<Stream>("owin.ResponseBody");

                    var responseHeaders = env.Get<IDictionary<string, string[]>>("owin.ResponseHeaders");

                    env.Get<Action<Action<object>, object>>("server.OnSendingHeaders")(state => responseHeaders["custom-header"] = new[] { "customvalue" }, null);

                    responseHeaders["content-length"] = new[] { "10" };

                    responseStream.Write(new byte[10], 0, 10);

                    return Task.Delay(0);
                });

            using (listener)
            {
                var client = new HttpClient();
                var response = client.PostAsync(HttpClientAddress, new StringContent(SampleContent)).Result;
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.Equal("Custom", response.ReasonPhrase);
                Assert.Equal("customvalue", response.Headers.GetValues("custom-header").First());
                Assert.Equal(10, response.Content.ReadAsByteArrayAsync().Result.Length);
            }
        }

        [Fact]
        public void CommonDisconnectDoesNotSendHeaders()
        {
            var listener = CreateServer(
                env =>
                {
                    var disconnectAction = env.Get<Action>("common.Disconnect");
                    disconnectAction();
                    throw new Exception("disconnect");
                });

            using (listener)
            {
                var client = new HttpClient();
                Assert.Throws<AggregateException>(() => client.GetAsync(HttpClientAddress).Wait());
            }
        }

        [Fact]
        public void CommonDisconnectWaitsWithNextRequest()
        {
            var insideDelay = false;
            var listener = CreateServer(
                async env =>
                {
                    Assert.False(insideDelay);
                    var disconnectAction = env.Get<Action>("common.Disconnect");
                    disconnectAction();
                    insideDelay = true;
                    await Task.Delay(100);
                    insideDelay = false;
                    throw new Exception("disconnect");
                });

            using (listener)
            {
                var client = new HttpClient();
                Assert.Throws<AggregateException>(() => client.GetAsync(HttpClientAddress).Wait());
                Assert.True(insideDelay);
                Assert.Throws<AggregateException>(() => client.GetAsync(HttpClientAddress).Wait());
            }
        }

        [Fact]
        public void DoubleOnSendingHeadersWorks()
        {
            var listener = CreateServer(
                env =>
                {
                    var responseHeaders = env.Get<IDictionary<string, string[]>>("owin.ResponseHeaders");

                    env.Get<Action<Action<object>, object>>("server.OnSendingHeaders")(state => responseHeaders["custom-header"] = new[] { (string)state }, "customvalue");
                    env.Get<Action<Action<object>, object>>("server.OnSendingHeaders")(state =>
                    {
                        responseHeaders["custom-header"] = new[] { "badvalue" };
                        responseHeaders["custom-header2"] = new[] { "goodvalue" };
                    }, null);

                    return Task.Delay(0);
                });

            using (listener)
            {
                var client = new HttpClient();
                var response = client.PostAsync(HttpClientAddress, new StringContent(SampleContent)).Result;
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.Equal("customvalue", response.Headers.GetValues("custom-header").First());
                Assert.Equal("goodvalue", response.Headers.GetValues("custom-header2").First());
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

        protected abstract IDisposable CreateServer(Func<IDictionary<string, object>, Task> app);

        IDisposable CreateServerSync(Action<IDictionary<string, object>> appSync)
        {
            return CreateServer(env =>
                {
                    appSync(env);
                    return Task.Delay(0);
                });
        }
    }
}