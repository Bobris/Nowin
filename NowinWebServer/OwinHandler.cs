using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace NowinWebServer
{
    using OwinApp = Func<IDictionary<string, object>, Task>;
    public class OwinHandler : IHttpLayerHandler
    {
        readonly OwinApp _app;

        readonly IDictionary<string, object> _environment;
        readonly Dictionary<string, string[]> _reqHeaders;
        readonly Dictionary<string, string[]> _respHeaders;

        public IHttpLayerCallback Callback { set; internal get; }
        public OwinHandler(OwinApp app)
        {
            _app = app;
            _environment = new Dictionary<string, object>();
            _reqHeaders = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            _respHeaders = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        }

        public void Dispose()
        {
        }

        public void PrepareForRequest()
        {
            _environment.Clear();
            _reqHeaders.Clear();
            _respHeaders.Clear();
            _environment.Add(OwinKeys.Version, "1.0");
            _environment.Add(OwinKeys.RequestPathBase, "");
            _environment.Add(OwinKeys.RequestHeaders, _reqHeaders);
            _environment.Add(OwinKeys.ResponseHeaders, _respHeaders);
        }

        public void AddRequestHeader(string name, string value)
        {
            string[] values;
            if (_reqHeaders.TryGetValue(name, out values))
            {
                Array.Resize(ref values, values.Length + 1);
                values[values.Length - 1] = value;
                _reqHeaders[name] = values;
            }
            else
            {
                _reqHeaders.Add(name, new[] { value });
            }
        }

        public void HandleRequest()
        {
            _environment.Add(OwinKeys.RequestMethod, Callback.RequestMethod);
            _environment.Add(OwinKeys.RequestPath, Callback.RequestPath);
            _environment.Add(OwinKeys.RequestScheme, Callback.RequestScheme);
            _environment.Add(OwinKeys.RequestQueryString, Callback.RequestQueryString);
            _environment.Add(OwinKeys.RequestProtocol, Callback.RequestProtocol);
            _environment.Add(OwinKeys.RequestBody, Callback.RequestBody);
            _environment.Add(OwinKeys.ResponseBody, Callback.ResponseBody);
            _environment.Add(OwinKeys.RemoteIpAddress, Callback.RemoteIpAddress);
            _environment.Add(OwinKeys.RemotePort, Callback.RemotePort);
            _environment.Add(OwinKeys.LocalIpAddress, Callback.LocalIpAddress);
            _environment.Add(OwinKeys.LocalPort, Callback.LocalPort);
            _environment.Add(OwinKeys.IsLocal, Callback.IsLocal);
            _environment.Add(OwinKeys.CallCancelled, Callback.CallCancelled);
            Callback.ResponseWriteIsFlushAndFlushIsNoOp = false;
            var task = _app(_environment);
            if (task.IsCompleted)
            {
                if (task.IsFaulted || task.IsCanceled)
                {
                    Callback.ResponseStatusCode = 500;
                    Callback.ResponseReasonPhase = null;
                }
                Callback.ResponseFinished();
                return;
            }
            Callback.ResponseWriteIsFlushAndFlushIsNoOp = true;
            task.ContinueWith((t, o) =>
                {
                    if (t.IsFaulted || t.IsCanceled)
                    {
                        Callback.ResponseStatusCode = 500;
                        Callback.ResponseReasonPhase = null;
                    }
                    Callback.ResponseFinished();
                }, this);
        }

        int GetStatusFromEnvironment()
        {
            object value;
            if (!_environment.TryGetValue(OwinKeys.ResponseStatusCode, out value))
                return 200;
            return (int)value;
        }

        public void PrepareResponseHeaders()
        {
            Callback.ResponseStatusCode = GetStatusFromEnvironment();
            string[] connectionValues;
            var headers = (IDictionary<string, string[]>)_environment[OwinKeys.ResponseHeaders];
            if (headers == null)
            {
                _respHeaders.Clear();
                headers = _respHeaders;
            }
            object responsePhase;
            if (_environment.TryGetValue(OwinKeys.ResponseReasonPhrase, out responsePhase))
            {
                if (!(responsePhase is String))
                {
                    Callback.ResponseStatusCode = 500;
                    return;
                }
                Callback.ResponseReasonPhase = (string)responsePhase;
            }
            if (headers.TryGetValue("Connection", out connectionValues))
            {
                headers.Remove("Connection");
                if (connectionValues.Length != 1)
                {
                    Callback.ResponseStatusCode = 500;
                    return;
                }
                var v = connectionValues[0];
                if (v.Equals("Close", StringComparison.InvariantCultureIgnoreCase))
                    Callback.KeepAlive = false;
                else if (v.Equals("Keep-alive", StringComparison.InvariantCultureIgnoreCase))
                    Callback.KeepAlive = true;
            }
            string[] contentLengthValues;
            if (headers.TryGetValue("Content-Length", out contentLengthValues))
            {
                headers.Remove("Content-Length");
                if (contentLengthValues.Length != 1)
                {
                    Callback.ResponseStatusCode = 500;
                    return;
                }
                ulong temp;
                if (!ulong.TryParse(contentLengthValues[0], out temp))
                {
                    Callback.ResponseStatusCode = 500;
                    return;
                }
                Callback.ResponseContentLength = temp;
            }
            headers.Remove("Transfer-Encoding");
            foreach (var header in headers)
            {
                Callback.AddResponseHeader(header.Key, header.Value);
            }
        }
    }
}