using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Coflnet.Sky.EventBroker.Services;

public sealed class LegalDocumentProvider : IHostedService
{
    private const string AgreementId = "skycofl";
    private static readonly Uri CoflnetOrigin = new("https://coflnet.com/");
    private readonly IHttpClientFactory clients;
    private readonly IConfiguration configuration;
    private readonly IHostEnvironment environment;
    private readonly Dictionary<string, LegalDocuments> localized = [];

    public LegalDocumentProvider(
        IHttpClientFactory clients,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        this.clients = clients;
        this.configuration = configuration;
        this.environment = environment;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var manifestUri = new Uri(
            configuration["LEGAL_MANIFEST_URL"]
            ?? "https://coflnet.com/legal/manifest.json");
        if (!environment.IsDevelopment() && !IsCoflnetUri(manifestUri))
            throw new InvalidOperationException(
                "LEGAL_MANIFEST_URL must use the Coflnet HTTPS origin.");

        var client = clients.CreateClient(nameof(LegalDocumentProvider));
        var manifest = Deserialize<Manifest>(
            await client.GetByteArrayAsync(manifestUri, cancellationToken),
            "The legal manifest is empty.");
        if (manifest.SchemaVersion != 1
            || manifest.AgreementTreeVersion != 1
            || !Uri.TryCreate(manifest.Source, UriKind.Absolute, out var source)
            || (!environment.IsDevelopment() && source != CoflnetOrigin)
            || !manifest.Agreements.TryGetValue(AgreementId, out var agreement)
            || !IsSha256(agreement.AgreementHash)
            || !TryUri(source, agreement.AgreementUrl, out var agreementUri))
            throw new InvalidOperationException(
                "The SkyCofl agreement root is invalid.");

        var descriptor = await client.GetByteArrayAsync(
            agreementUri,
            cancellationToken);
        if (!Sha256(descriptor).Equals(
                agreement.AgreementHash,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "The SkyCofl agreement descriptor hash is invalid.");
        var descriptorIdentity = Deserialize<AgreementDescriptor>(
            descriptor,
            "The SkyCofl agreement descriptor is invalid.");
        if (descriptorIdentity.SchemaVersion != 1
            || descriptorIdentity.Kind != "coflnet-legal-agreement-node"
            || descriptorIdentity.Id != AgreementId
            || descriptorIdentity.Type != "service")
            throw new InvalidOperationException(
                "The SkyCofl agreement descriptor identity is invalid.");

        if (!manifest.Documents.TryGetValue("withdrawal", out var withdrawal)
            || agreement.ResolvedDocuments.Count == 0
            || agreement.ResolvedDocuments.Select(item => item.Key).Distinct().Count()
                != agreement.ResolvedDocuments.Count)
            throw new InvalidOperationException(
                "The legal manifest lacks confirmation documents.");

        foreach (var language in new[] { "en", "de" })
        {
            var documents = new List<LegalDocument>();
            foreach (var summary in agreement.ResolvedDocuments)
            {
                if (!manifest.Documents.TryGetValue(summary.Key, out var document)
                    || document.Version != summary.Version
                    || !string.Equals(
                        document.AcceptanceHash,
                        summary.AcceptanceHash,
                        StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        "A resolved SkyCofl document does not match the agreement root.");
                documents.Add(await Download(
                    client,
                    source,
                    summary.Key,
                    language,
                    document,
                    true,
                    cancellationToken));
            }

            localized[language] = new(
                AgreementId,
                agreement.AgreementHash.ToLowerInvariant(),
                agreement.AgreementUrl,
                descriptor,
                documents,
                await Download(
                    client,
                    source,
                    "withdrawal",
                    language,
                    withdrawal,
                    false,
                    cancellationToken));
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public LegalDocuments Get(string locale) =>
        localized[PurchaseConfirmationEmailService.IsGerman(locale) ? "de" : "en"];

    private static async Task<LegalDocument> Download(
        HttpClient client,
        Uri source,
        string key,
        string language,
        Document document,
        bool acceptanceRequired,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(document.Title)
            || string.IsNullOrWhiteSpace(document.Version)
            || !document.Locales.TryGetValue("en", out var english)
            || !document.Locales.TryGetValue("de", out var german)
            || !document.Locales.TryGetValue(language, out var locale)
            || !TryUri(source, locale.Url, out var uri)
            || !IsSha256(locale.Sha256))
            throw new InvalidOperationException(
                $"The legal document {key} is incomplete.");

        if (acceptanceRequired)
        {
            var canonical = Encoding.UTF8.GetBytes(
                $"version={document.Version}\nen={english.Sha256}\nde={german.Sha256}\n");
            if (!Sha256(canonical).Equals(
                    document.AcceptanceHash,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"The legal document acceptance hash for {key} is invalid.");
        }

        var content = await client.GetByteArrayAsync(uri, cancellationToken);
        var hash = Sha256(content);
        if (!hash.Equals(locale.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Legal document hash mismatch for {uri}.");
        return new(
            key,
            document.Title,
            Path.GetFileName(uri.AbsolutePath),
            document.Version,
            locale.Url,
            content,
            hash);
    }

    private static bool TryUri(Uri source, string value, out Uri uri)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out uri)
            || uri.Scheme != source.Scheme
            || !uri.IdnHost.Equals(source.IdnHost, StringComparison.OrdinalIgnoreCase)
            || !uri.IsDefaultPort
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            uri = null;
            return false;
        }
        return true;
    }

    private static bool IsCoflnetUri(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps
        && uri.IdnHost.Equals("coflnet.com", StringComparison.OrdinalIgnoreCase)
        && uri.IsDefaultPort
        && string.IsNullOrEmpty(uri.UserInfo);

    private static bool IsSha256(string value) =>
        value?.Length == 64 && value.All(Uri.IsHexDigit);

    private static string Sha256(byte[] value) =>
        Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();

    private static T Deserialize<T>(byte[] bytes, string error) =>
        JsonSerializer.Deserialize<T>(
            bytes,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
        ?? throw new InvalidOperationException(error);

    private sealed class Manifest
    {
        public int SchemaVersion { get; set; }
        public int AgreementTreeVersion { get; set; }
        public string Source { get; set; }
        public Dictionary<string, Document> Documents { get; set; } = [];
        public Dictionary<string, AgreementSummary> Agreements { get; set; } = [];
    }

    private sealed class AgreementSummary
    {
        public string AgreementHash { get; set; }
        public string AgreementUrl { get; set; }
        public List<DocumentSummary> ResolvedDocuments { get; set; } = [];
    }

    private sealed class DocumentSummary
    {
        public string Key { get; set; }
        public string Version { get; set; }
        public string AcceptanceHash { get; set; }
    }

    private sealed class AgreementDescriptor
    {
        public int SchemaVersion { get; set; }
        public string Kind { get; set; }
        public string Id { get; set; }
        public string Type { get; set; }
    }

    private sealed class Document
    {
        public string Title { get; set; }
        public string Version { get; set; }
        public string AcceptanceHash { get; set; }
        public Dictionary<string, Locale> Locales { get; set; } = [];
    }

    private sealed class Locale
    {
        public string Url { get; set; }
        public string Sha256 { get; set; }
    }
}

public sealed record LegalDocuments(
    string AgreementId,
    string AgreementHash,
    string AgreementUrl,
    byte[] AgreementDescriptor,
    IReadOnlyList<LegalDocument> AgreementDocuments,
    LegalDocument Withdrawal);

public sealed record LegalDocument(
    string Key,
    string Title,
    string FileName,
    string Version,
    string Url,
    byte[] Content,
    string Hash)
{
    internal Attachment CreateAttachment() =>
        new(new MemoryStream(Content), FileName, "text/markdown");
}
