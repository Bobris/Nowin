namespace Flux
{
    internal class RequestLine
    {
        private readonly string _method;
        private readonly string _uri;
        private readonly string _httpVersion;

        public RequestLine(string method, string uri, string httpVersion)
        {
            _method = method;
            _uri = uri;
            _httpVersion = httpVersion;
        }

        public string HttpVersion
        {
            get { return _httpVersion; }
        }

        public string Uri
        {
            get { return _uri; }
        }

        public string Method
        {
            get { return _method; }
        }
    }
}