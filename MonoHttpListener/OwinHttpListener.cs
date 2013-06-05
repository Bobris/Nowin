// <copyright file="OwinHttpListener.cs" company="Microsoft Open Technologies, Inc.">
// Copyright 2011-2013 Microsoft Open Technologies, Inc. All rights reserved.
// 
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
// 
//     http://www.apache.org/licenses/LICENSE-2.0
// 
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
// </copyright>

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Contracts;
using Mono.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Owin.Host.HttpListener.RequestProcessing;

namespace Microsoft.Owin.Host.HttpListener
{
    using AppFunc = Func<IDictionary<string, object>, Task>;

    /// <summary>
    /// This wraps HttpListener and exposes it as an OWIN compatible server.
    /// </summary>
    public sealed class OwinHttpListener : IDisposable
    {
        private Mono.Net.HttpListener _listener;
        private IList<string> _basePaths;
        private AppFunc _appFunc;
        private IDictionary<string, object> _capabilities;
        private int _currentOutstandingAccepts;
        private int _currentOutstandingRequests;

        /// <summary>
        /// Creates a listener wrapper that can be configured by the user before starting.
        /// </summary>
        internal OwinHttpListener()
        {
            _listener = new Mono.Net.HttpListener();
        }

        /// <summary>
        /// The HttpListener instance wrapped by this wrapper.
        /// </summary>
        public Mono.Net.HttpListener Listener
        {
            get { return _listener; }
        }

        private bool CanAcceptMoreRequests
        {
            get
            {
                return (_currentOutstandingAccepts < 40
                    && _currentOutstandingRequests < 400 - _currentOutstandingAccepts);
            }
        }

        /// <summary>
        /// Starts the listener and request processing threads.
        /// </summary>
        internal void Start(Mono.Net.HttpListener listener, AppFunc appFunc, IList<IDictionary<string, object>> addresses, IDictionary<string, object> capabilities)
        {
            Contract.Assert(_appFunc == null); // Start should only be called once
            Contract.Assert(listener != null);
            Contract.Assert(appFunc != null);
            Contract.Assert(addresses != null);

            _listener = listener;
            _appFunc = appFunc;

            _basePaths = new List<string>();

            foreach (var address in addresses)
            {
                // build url from parts
                string scheme = address.Get<string>("scheme") ?? Uri.UriSchemeHttp;
                string host = address.Get<string>("host") ?? "localhost";
                string port = address.Get<string>("port") ?? "5000";
                string path = address.Get<string>("path") ?? string.Empty;

                // if port is present, add delimiter to value before concatenation
                if (!string.IsNullOrWhiteSpace(port))
                {
                    port = ":" + port;
                }

                // Assume http(s)://+:9090/BasePath/, including the first path slash.  May be empty. Must end with a slash.
                if (!path.EndsWith("/", StringComparison.Ordinal))
                {
                    // Http.Sys requires that the URL end in a slash
                    path += "/";
                }
                _basePaths.Add(path);

                // add a server for each url
                string url = scheme + "://" + host + port + path;
                _listener.Prefixes.Add(url);
            }

            _capabilities = capabilities;

            if (!_listener.IsListening)
            {
                _listener.Start();
            }

            OffloadStartNextRequest();
        }

        private void OffloadStartNextRequest()
        {
            if (_listener.IsListening && CanAcceptMoreRequests)
            {
                Task.Factory.StartNew(StartNextRequestAsync);
            }
        }

        private async void StartNextRequestAsync()
        {
            if (!_listener.IsListening || !CanAcceptMoreRequests)
            {
                return;
            }

            Interlocked.Increment(ref _currentOutstandingAccepts);

            try
            {
                StartProcessingRequest(await _listener.GetContextAsync());
            }
            catch (Exception ae)
            {
                HandleAcceptError(ae);
            }
        }

        private void HandleAcceptError(Exception ex)
        {
            Interlocked.Decrement(ref _currentOutstandingAccepts);
            // TODO: Log?
            System.Diagnostics.Debug.Write(ex);
            // Listener is disposed, but HttpListener.IsListening is not updated until the end of HttpListener.Dispose().
        }

        [SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes", Justification = "Exception is logged")]
        private async void StartProcessingRequest(HttpListenerContext context)
        {
            Interlocked.Decrement(ref _currentOutstandingAccepts);
            Interlocked.Increment(ref _currentOutstandingRequests);
            OffloadStartNextRequest();
            OwinHttpListenerContext owinContext = null;

            try
            {
                string pathBase, path, query;
                GetPathAndQuery(context.Request.RawUrl, out pathBase, out path, out query);
                owinContext = new OwinHttpListenerContext(context, pathBase, path, query);
                PopulateServerKeys(owinContext.Environment);
                Contract.Assert(!owinContext.Environment.IsExtraDictionaryCreated,
                    "All keys set by the server should have reserved slots.");

                await _appFunc(owinContext.Environment);
                await owinContext.Response.CompleteResponseAsync();
                owinContext.Response.Close();
                EndRequest(owinContext, null);
            }
            catch (Exception ex)
            {
                // TODO: Katana#5 - Don't catch everything, only catch what we think we can handle?  Otherwise crash the process.
                EndRequest(owinContext, ex);
            }
        }

        private void EndRequest(OwinHttpListenerContext owinContext, Exception ex)
        {
            // TODO: Log the exception, if any
            Interlocked.Decrement(ref _currentOutstandingRequests);
            if (owinContext != null)
            {
                owinContext.End(ex);
                owinContext.Dispose();
            }
            // Make sure we start the next request on a new thread, need to prevent stack overflows.
            OffloadStartNextRequest();
        }

        // When the server is listening on multiple urls, we need to decide which one is the correct base path for this request.
        private void GetPathAndQuery(string rawUrl, out string pathBase, out string path, out string query)
        {
            // Starting with the full url or just a path, extract the path and query.  There must never be a fragment.
            // http://host:port/path?query or /path?query
            string rawPathAndQuery;
            if (rawUrl.StartsWith("/", StringComparison.Ordinal))
            {
                rawPathAndQuery = rawUrl;
            }
            else
            {
                rawPathAndQuery = rawUrl.Substring(rawUrl.IndexOf('/', "https://x".Length));
            }

            if (rawPathAndQuery.Equals("/", StringComparison.Ordinal))
            {
                pathBase = string.Empty;
                path = "/";
                query = string.Empty;
                return;
            }

            // Split off the query
            string unescapedPath;
            int queryIndex = rawPathAndQuery.IndexOf('?');
            if (queryIndex < 0)
            {
                unescapedPath = Uri.UnescapeDataString(rawPathAndQuery);
                query = string.Empty;
            }
            else
            {
                unescapedPath = Uri.UnescapeDataString(rawPathAndQuery.Substring(0, queryIndex));
                query = rawPathAndQuery.Substring(queryIndex + 1); // Leave off the '?'
            }

            // Find the split between path and pathBase.
            // This will only do full segment path matching because all _basePaths end in a '/'.
            string bestMatch = "/";
            for (int i = 0; i < _basePaths.Count; i++)
            {
                string pathTest = _basePaths[i];
                if (unescapedPath.StartsWith(pathTest, StringComparison.OrdinalIgnoreCase)
                    && pathTest.Length > bestMatch.Length)
                {
                    bestMatch = pathTest;
                }
            }

            // pathBase must be empty or start with a slash and not end with a slash (/pathBase)
            // path must start with a slash (/path)
            // Move the matched '/' from the end of the pathBase to the start of the path.
            pathBase = bestMatch.Substring(0, bestMatch.Length - 1);
            path = unescapedPath.Substring(bestMatch.Length - 1);
        }

        private void PopulateServerKeys(CallEnvironment env)
        {
            env.ServerCapabilities = _capabilities;
        }

        internal void Stop()
        {
            try
            {
                _listener.Stop();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        /// <summary>
        /// Shuts down the listener and disposes it.
        /// </summary>
        public void Dispose()
        {
            if (_listener.IsListening)
            {
                _listener.Stop();
            }

            ((IDisposable)_listener).Dispose();
        }
    }
}
