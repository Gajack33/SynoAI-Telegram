using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace SynoAI.App
{
    public interface IHttpClient
    {
        // Responses complete after headers. Callers must bound and cancel body reads.
        TimeSpan Timeout { get; set; }
        Task<HttpResponseMessage> PostAsync(string requestUri, HttpContent content);
        Task<HttpResponseMessage> PostAsync(Uri requestUri, HttpContent content);
        Task<HttpResponseMessage> PostAsync(Uri requestUri, HttpContent content, CancellationToken cancellationToken);
    }
}
