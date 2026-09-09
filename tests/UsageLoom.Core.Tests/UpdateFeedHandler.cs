using System.Net;
using System.Net.Http;
internal sealed class UpdateFeedHandler(Func<int,HttpRequestMessage,HttpResponseMessage> response):HttpMessageHandler
{
    public int Calls { get; private set; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken)
        =>Task.FromResult(response(++Calls,request));
    public static HttpResponseMessage Reply(HttpStatusCode status,string body="")=>new(status){Content=new StringContent(body)};
}
