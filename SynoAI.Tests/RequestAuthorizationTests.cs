using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using SynoAI.App;
using System.Collections.Generic;

namespace SynoAI.Tests
{
    public class RequestAuthorizationTests
    {
        [SetUp]
        public void Setup()
        {
            ConfigureToken("secret-token");
        }

        [Test]
        public void IsAuthorized_AllowsMatchingQueryToken()
        {
            DefaultHttpContext context = new();
            context.Request.QueryString = new QueryString("?token=secret-token");

            Assert.That(RequestAuthorization.IsAuthorized(context.Request), Is.True);
        }

        [Test]
        public void IsAuthorized_AllowsMatchingBearerToken()
        {
            DefaultHttpContext context = new();
            context.Request.Headers.Authorization = "Bearer secret-token";

            Assert.That(RequestAuthorization.IsAuthorized(context.Request), Is.True);
        }

        [Test]
        public void IsAuthorized_RejectsMissingTokenWhenConfigured()
        {
            DefaultHttpContext context = new();

            Assert.That(RequestAuthorization.IsAuthorized(context.Request), Is.False);
        }

        [Test]
        public void IsAuthorized_RejectsRequestsWhenTokenIsNotConfigured()
        {
            ConfigureToken(null);
            DefaultHttpContext context = new();

            Assert.That(RequestAuthorization.IsAuthorized(context.Request), Is.False);
        }

        [Test]
        public void AppendToken_AddsConfiguredTokenToUrl()
        {
            string url = RequestAuthorization.AppendToken("http://synoai.local/Image/Cam/capture.jpeg?width=300");

            Assert.That(url, Is.EqualTo("http://synoai.local/Image/Cam/capture.jpeg?width=300&token=secret-token"));
        }

        [Test]
        public void IsImageAuthorized_UsesImageTokenWhenConfigured()
        {
            ConfigureToken("action-token", "image-token");
            DefaultHttpContext context = new();
            context.Request.QueryString = new QueryString("?token=image-token");

            Assert.That(RequestAuthorization.IsImageAuthorized(context.Request), Is.True);
            Assert.That(RequestAuthorization.IsAuthorized(context.Request), Is.False);
        }

        [Test]
        public void IsImageAuthorized_RejectsActionTokenWhenImageTokenIsNotConfigured()
        {
            ConfigureToken("action-token");
            DefaultHttpContext context = new();
            context.Request.QueryString = new QueryString("?token=action-token");

            Assert.That(RequestAuthorization.IsImageAuthorized(context.Request), Is.False);
        }

        [Test]
        public void AppendImageToken_AddsConfiguredImageTokenToUrl()
        {
            ConfigureToken("action-token", "image-token");

            string url = RequestAuthorization.AppendImageToken("http://synoai.local/Image/Cam/capture.jpeg?width=300");

            Assert.That(url, Is.EqualTo("http://synoai.local/Image/Cam/capture.jpeg?width=300&token=image-token"));
        }

        private static void ConfigureToken(string token, string imageToken = null)
        {
            Dictionary<string, string> values = new()
            {
                ["AccessToken"] = token,
                ["ImageAccessToken"] = imageToken,
                ["AI:Url"] = "http://deepstack:5000"
            };

            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(values)
                .Build();

            using ILoggerFactory loggerFactory = LoggerFactory.Create(builder => { });
            Config.Generate(loggerFactory.CreateLogger("test"), configuration);
        }
    }
}
