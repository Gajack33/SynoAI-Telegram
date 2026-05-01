using Microsoft.AspNetCore.Http;
using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace SynoAI.App
{
    public static class RequestAuthorization
    {
        public const string TokenQueryName = "token";
        public const string TokenHeaderName = "X-SynoAI-Token";

        public static bool IsAuthorized(HttpRequest request)
        {
            if (string.IsNullOrWhiteSpace(Config.AccessToken))
            {
                return true;
            }

            string suppliedToken = request.Query[TokenQueryName].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(suppliedToken))
            {
                suppliedToken = request.Headers[TokenHeaderName].FirstOrDefault();
            }

            if (string.IsNullOrWhiteSpace(suppliedToken))
            {
                string authorization = request.Headers["Authorization"].FirstOrDefault();
                const string bearerPrefix = "Bearer ";
                if (!string.IsNullOrWhiteSpace(authorization) && authorization.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    suppliedToken = authorization.Substring(bearerPrefix.Length).Trim();
                }
            }

            return TokenEquals(Config.AccessToken, suppliedToken);
        }

        public static string AppendToken(string url)
        {
            if (string.IsNullOrWhiteSpace(Config.AccessToken) || string.IsNullOrWhiteSpace(url))
            {
                return url;
            }

            UriBuilder builder = new(url);
            string separator = string.IsNullOrWhiteSpace(builder.Query) ? string.Empty : builder.Query.TrimStart('?') + "&";
            builder.Query = $"{separator}{TokenQueryName}={Uri.EscapeDataString(Config.AccessToken)}";
            return builder.Uri.ToString();
        }

        private static bool TokenEquals(string expected, string supplied)
        {
            if (string.IsNullOrWhiteSpace(expected) || string.IsNullOrWhiteSpace(supplied))
            {
                return false;
            }

            byte[] expectedBytes = Encoding.UTF8.GetBytes(expected);
            byte[] suppliedBytes = Encoding.UTF8.GetBytes(supplied);
            return expectedBytes.Length == suppliedBytes.Length &&
                   CryptographicOperations.FixedTimeEquals(expectedBytes, suppliedBytes);
        }
    }
}
