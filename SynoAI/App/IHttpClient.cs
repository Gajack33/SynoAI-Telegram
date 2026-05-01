using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace SynoAI.App
{
    public interface IHttpClient
    {
        TimeSpan Timeout { get; set; }
        Task<HttpResponseMessage> PostAsync(string requestUri, HttpContent content);
        Task<HttpResponseMessage> PostAsync(Uri requestUri, HttpContent content);
        Task<HttpResponseMessage> PostAsync(Uri requestUri, HttpContent content, CancellationToken cancellationToken);
    }
}
