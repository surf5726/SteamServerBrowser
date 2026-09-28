using System;
using System.Net;

namespace QueryMaster
{
    public static class XWebClientProxy
    {
        public static bool UseHTTPProxy { get; set; } = false;
        public static string ProxyAddress { get; set; } = "";
    }

    class XWebClient : WebClient
    {
        private const int RequestTimeoutMs = 8000;
        public XWebClient()
        {
            this.Proxy = XWebClientProxy.UseHTTPProxy ? new WebProxy($"http://{XWebClientProxy.ProxyAddress.Trim()}", true) : null;
        }

        protected override WebRequest GetWebRequest(Uri address)
        {
            var req = base.GetWebRequest(address);
            req.Timeout = RequestTimeoutMs;
            if (req is HttpWebRequest httpReq)
                httpReq.ReadWriteTimeout = RequestTimeoutMs;
            return req;
        }
    }
}