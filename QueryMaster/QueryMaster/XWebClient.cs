using System;
using System.Net;

namespace QueryMaster
{
  class XWebClient : WebClient
  {
    private const int RequestTimeoutMs = 8000;

    public XWebClient()
    {
      this.Proxy = null;
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
