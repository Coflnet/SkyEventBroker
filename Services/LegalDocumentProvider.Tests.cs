using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Coflnet.Sky.EventBroker.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Moq;
using NUnit.Framework;

namespace Coflnet.Sky.EventBroker.Services;

public class LegalDocumentProviderTests
{
    private const string Base = "https://coflnet.com/legal/archive/";

    private static string Sha(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private sealed class FakeHandler : HttpMessageHandler
    {
        public Dictionary<string, byte[]> Files { get; } = [];
        public Exception Failure { get; set; }
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri.AbsoluteUri);
            if (Failure != null)
                throw Failure;
            return Task.FromResult(Files.TryGetValue(request.RequestUri.AbsoluteUri, out var bytes)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private static (LegalDocumentProvider Provider, FakeHandler Handler, byte[] Current) Create(
        List<LegalDocumentProvider.ArchiveEntry> archive = null)
    {
        var handler = new FakeHandler();
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(item => item.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient(handler, false));
        var provider = new LegalDocumentProvider(
            factory.Object,
            new ConfigurationBuilder().Build(),
            Mock.Of<IHostEnvironment>());
        var content = Encoding.UTF8.GetBytes("current withdrawal");
        var current = new LegalDocument(
            "withdrawal",
            "Withdrawal",
            "withdrawal-en-2026-09-30.md",
            "2026-09-30",
            Base + "withdrawal-en-2026-09-30.md",
            content,
            Sha(content),
            null);
        provider.SetLoadedForTests(
            new()
            {
                ["skycofl:en"] = new(
                    "skycofl", new string('a', 64), "https://coflnet.com/x.json",
                    [], [], current)
            },
            archive);
        return (provider, handler, content);
    }

    private static PaymentEvent Payment(string version, string sha) =>
        new() { WithdrawalVersion = version, WithdrawalSha256 = sha };

    [Test]
    public async Task CurrentVersionIsUsedWithoutAnyRequest()
    {
        var (provider, handler, current) = Create();

        var documents = await provider.GetForPurchase(
            "en", "skycofl", Payment("2026-09-30", Sha(current)));

        Assert.That(documents.Withdrawal.Version, Is.EqualTo("2026-09-30"));
        Assert.That(handler.Requests, Is.Empty);
    }

    [Test]
    public async Task ArchivedVersionWithMatchingHashIsAttachedAndCached()
    {
        var (provider, handler, _) = Create();
        var old = Encoding.UTF8.GetBytes("old withdrawal");
        handler.Files[Base + "withdrawal-en-2026-09-04.md"] = old;
        var payment = Payment("2026-09-04", Sha(old));

        var documents = await provider.GetForPurchase("en", "skycofl", payment);
        await provider.GetForPurchase("en", "skycofl", payment);

        Assert.That(documents.Withdrawal.Version, Is.EqualTo("2026-09-04"));
        Assert.That(documents.Withdrawal.Content, Is.EqualTo(old));
        Assert.That(documents.Withdrawal.Url, Is.EqualTo(Base + "withdrawal-en-2026-09-04.md"));
        Assert.That(handler.Requests, Has.Count.EqualTo(1));
        Assert.DoesNotThrow(() => PurchaseConfirmationEmailService.ValidateAgreement(
            new PaymentEvent
            {
                WithdrawalVersion = "2026-09-04",
                WithdrawalSha256 = Sha(old)
            },
            documents));
    }

    [Test]
    public async Task ArchivedVersionWhoseBytesDoNotMatchTheHashStaysAMismatch()
    {
        var (provider, handler, _) = Create();
        handler.Files[Base + "withdrawal-en-2026-09-04.md"] = Encoding.UTF8.GetBytes("tampered");
        var payment = Payment("2026-09-04", Sha(Encoding.UTF8.GetBytes("old withdrawal")));

        var documents = await provider.GetForPurchase("en", "skycofl", payment);

        Assert.That(documents.Withdrawal.Version, Is.EqualTo("2026-09-30"));
        var failure = Assert.Throws<PurchaseConfirmationException>(() =>
            PurchaseConfirmationEmailService.ValidateAgreement(payment, documents));
        Assert.That(failure.Reason, Is.EqualTo("withdrawal_mismatch"));
        Assert.That(provider.ArchiveCacheCount, Is.EqualTo(0));
    }

    [Test]
    public async Task UnknownVersionIsAMismatch()
    {
        var (provider, _, _) = Create();
        var payment = Payment("2020-01-01", Sha(Encoding.UTF8.GetBytes("x")));

        var documents = await provider.GetForPurchase("en", "skycofl", payment);

        Assert.That(documents.Withdrawal.Version, Is.EqualTo("2026-09-30"));
        Assert.Throws<PurchaseConfirmationException>(() =>
            PurchaseConfirmationEmailService.ValidateAgreement(payment, documents));
    }

    [TestCase("../../evil")]
    [TestCase("2026-09-04/../x")]
    public async Task UnsafeVersionIsNeverFetched(string version)
    {
        var (provider, handler, _) = Create();

        await provider.GetForPurchase("en", "skycofl", Payment(version, new string('b', 64)));

        Assert.That(handler.Requests, Is.Empty);
    }

    [Test]
    public void FetchFailureIsTransientNotAMismatch()
    {
        var (provider, handler, _) = Create();
        handler.Failure = new HttpRequestException("down");

        var failure = Assert.ThrowsAsync<PurchaseConfirmationException>(() =>
            provider.GetForPurchase("en", "skycofl", Payment("2026-09-04", new string('b', 64))));

        Assert.That(PurchaseConfirmationFailure.Classify(failure).Kind,
            Is.EqualTo(PurchaseConfirmationFailureKind.Transient));
    }

    [Test]
    public async Task ManifestArchiveEntryIsUsedWhenListed()
    {
        var old = Encoding.UTF8.GetBytes("old withdrawal");
        var (provider, handler, _) = Create(
        [
            new()
            {
                Version = "2026-09-04",
                Locales = new() { ["en"] = new() { Url = Base + "custom-name.md", Sha256 = Sha(old) } }
            }
        ]);
        handler.Files[Base + "custom-name.md"] = old;

        var documents = await provider.GetForPurchase("en", "skycofl", Payment("2026-09-04", Sha(old)));

        Assert.That(documents.Withdrawal.Content, Is.EqualTo(old));
    }

    [Test]
    public async Task ArchiveCacheIsBounded()
    {
        var (provider, handler, _) = Create();
        for (var i = 0; i < LegalDocumentProvider.ArchiveCacheLimit + 5; i++)
        {
            var bytes = Encoding.UTF8.GetBytes($"old {i}");
            var version = $"2026-01-{i:000}";
            handler.Files[Base + $"withdrawal-en-{version}.md"] = bytes;
            await provider.GetForPurchase("en", "skycofl", Payment(version, Sha(bytes)));
        }

        Assert.That(provider.ArchiveCacheCount, Is.EqualTo(LegalDocumentProvider.ArchiveCacheLimit));
    }
}
