using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace SynoAI.App
{
    public class HttpClientWrapper : IHttpClient
    {
        public const string OutboundClientName = "synoai-outbound";

        private readonly IHttpClientFactory _httpClientFactory;
        private readonly HttpClient _fallbackClient;
        private TimeSpan? _timeout;

        public HttpClientWrapper(IHttpClientFactory httpClientFactory)
        {
            _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        }

        public HttpClientWrapper()
        {
            _fallbackClient = new HttpClient();
        }

        public TimeSpan Timeout
        {
            get => _timeout ?? CreateClient().Timeout;
            set
            {
                _timeout = value;
                if (_fallbackClient != null)
                {
                    _fallbackClient.Timeout = value;
                }
            }
        }

        public Task<HttpResponseMessage> PostAsync(string requestUri, HttpContent content)
        {
            return CreateClient().PostAsync(requestUri, content);
        }

        public Task<HttpResponseMessage> PostAsync(Uri requestUri, HttpContent content)
        {
            return CreateClient().PostAsync(requestUri, content);
        }

        public Task<HttpResponseMessage> PostAsync(Uri requestUri, HttpContent content, CancellationToken cancellationToken)
        {
            return CreateClient().PostAsync(requestUri, content, cancellationToken);
        }

        private HttpClient CreateClient()
        {
            HttpClient client = _httpClientFactory?.CreateClient(OutboundClientName) ?? _fallbackClient;
            if (_timeout.HasValue && client.Timeout != _timeout.Value)
            {
                client.Timeout = _timeout.Value;
            }

            return client;
        }
    }
}
