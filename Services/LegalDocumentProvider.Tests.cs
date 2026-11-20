using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using NUnit.Framework;

namespace Coflnet.Sky.EventBroker.Services;

public class LegalDocumentProviderTests
{
    private const string Origin = "https://coflnet.com";

    [TestCase("skycofl")]
    [TestCase("expertMarketplace")]
    [TestCase("bazaarPro")]
    public async Task Get_ReturnsEveryConfirmationAgreementWithItsHash(string agreementId)
    {
        var provider = await LoadedProvider();

        var documents = provider.Get("en", agreementId);

        Assert.That(documents.AgreementId, Is.EqualTo(agreementId));
        Assert.That(documents.AgreementHash, Is.EqualTo(HashOf(DescriptorFor(agreementId))));
        Assert.That(documents.AgreementDocuments, Has.Count.EqualTo(1));
        Assert.That(documents.Withdrawal.Key, Is.EqualTo("withdrawal"));
    }

    [Test]
    public async Task Get_UnknownAgreementStillThrows()
    {
        var provider = await LoadedProvider();

        Assert.Throws<InvalidOperationException>(() => provider.Get("en", "somethingElse"));
    }

    private static async Task<LegalDocumentProvider> LoadedProvider()
    {
        var provider = new LegalDocumentProvider(
            new StubFactory(new HttpClient(new ManifestHandler())),
            new ConfigurationBuilder().Build(),
            new DevelopmentEnvironment());
        await provider.StartAsync(CancellationToken.None);
        for (var i = 0; i < 200; i++)
        {
            try
            {
                provider.Get("en", "skycofl");
                return provider;
            }
            catch (InvalidOperationException)
            {
                await Task.Delay(25);
            }
        }
        Assert.Fail("The legal documents did not load");
        return provider;
    }

    private static byte[] DescriptorFor(string agreementId) =>
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            kind = "coflnet-legal-agreement-node",
            id = agreementId,
            type = "service"
        }));

    private static string HashOf(byte[] content) =>
        Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    private sealed class ManifestHandler : HttpMessageHandler
    {
        private static readonly byte[] Text = Encoding.UTF8.GetBytes("document");
        private static readonly string TextHash = HashOf(Text);
        private static readonly string AcceptanceHash = HashOf(Encoding.UTF8.GetBytes(
            $"version=v1\nen={TextHash}\nde={TextHash}\n"));

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri.AbsolutePath;
            var body = path == "/legal/manifest.json"
                ? Manifest()
                : path.StartsWith("/legal/agreements/")
                    ? DescriptorFor(System.IO.Path.GetFileNameWithoutExtension(path) is var hash
                        ? AgreementIdForHash(hash)
                        : null)
                    : Text;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(body)
            });
        }

        private static string AgreementIdForHash(string hash)
        {
            foreach (var id in new[] { "skycofl", "expertMarketplace", "bazaarPro" })
                if (HashOf(DescriptorFor(id)) == hash)
                    return id;
            return null;
        }

        private static byte[] Manifest()
        {
            var locales = new
            {
                en = new { url = $"{Origin}/legal/doc-en.md", sha256 = TextHash },
                de = new { url = $"{Origin}/legal/doc-de.md", sha256 = TextHash }
            };
            var agreements = new Dictionary<string, object>();
            foreach (var id in new[] { "skycofl", "expertMarketplace", "bazaarPro" })
            {
                var hash = HashOf(DescriptorFor(id));
                agreements[id] = new
                {
                    agreementHash = hash,
                    agreementUrl = $"{Origin}/legal/agreements/{hash}.json",
                    resolvedDocuments = new[] { new { key = "terms", version = "v1", acceptanceHash = AcceptanceHash } }
                };
            }
            return JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = 1,
                agreementTreeVersion = 1,
                source = Origin + "/",
                documents = new Dictionary<string, object>
                {
                    ["terms"] = new { title = "Terms", version = "v1", acceptanceHash = AcceptanceHash, locales },
                    ["withdrawal"] = new { title = "Withdrawal", version = "v1", acceptanceHash = (string)null, locales }
                },
                agreements
            });
        }
    }

    private sealed class StubFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class DevelopmentEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "test";
        public string ContentRootPath { get; set; } = ".";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
