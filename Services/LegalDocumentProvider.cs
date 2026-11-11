using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Coflnet.Sky.EventBroker.Models;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Coflnet.Sky.EventBroker.Services;

public sealed class LegalDocumentProvider : IHostedService
{
    private const string DefaultAgreementId = "skycofl";
    private static readonly string[] ConfirmationAgreementIds =
        [DefaultAgreementId, "expertMarketplace"];
    private static readonly Uri CoflnetOrigin = new("https://coflnet.com/");
    private readonly IHttpClientFactory clients;
    private readonly IConfiguration configuration;
    private readonly IHostEnvironment environment;
    private readonly ILogger<LegalDocumentProvider> logger;
    private readonly CancellationTokenSource stopping = new();
    private Task refreshTask;

    // Swapped in atomically once a full (en + de) load succeeds, so readers either see the
    // previous complete snapshot or the new one - never a half-populated one.
    private volatile Dictionary<string, LegalDocuments> localized;

    /// <summary>Upper bound of cached previous-version withdrawal documents.</summary>
    internal const int ArchiveCacheLimit = 32;
    private static readonly Regex SafeVersion = new(
        "^[0-9A-Za-z][0-9A-Za-z._-]{0,63}$",
        RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(100));
    private readonly Dictionary<string, LegalDocument> archiveCache = [];
    private readonly Queue<string> archiveOrder = new();
    private readonly object archiveLock = new();

    // Optional "archive" list of the manifest's withdrawal entry (previous versions). Absent in
    // today's manifest; the versioned file name pattern is used as the fallback.
    private volatile List<ArchiveEntry> withdrawalArchive = [];

    public LegalDocumentProvider(
        IHttpClientFactory clients,
        IConfiguration configuration,
        IHostEnvironment environment,
        ILogger<LegalDocumentProvider> logger = null)
    {
        this.clients = clients;
        this.configuration = configuration;
        this.environment = environment;
        this.logger = logger;
    }

    /// <summary>
    /// Kicks off loading in the background and returns immediately, so a slow or unreachable
    /// coflnet.com does not prevent the rest of the host (flips, notifications, verifications)
    /// from starting. <see cref="Get"/> throws until the first load completes.
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        refreshTask = RefreshUntilLoaded(stopping.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        stopping.Cancel();
        if (refreshTask == null)
            return;
        try
        {
            await refreshTask.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// Returns the loaded legal documents for the given locale.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The documents have not finished loading yet (e.g. coflnet.com is unreachable). Callers
    /// (the purchase confirmation outbox) should simply retry later.
    /// </exception>
    public LegalDocuments Get(string locale, string agreementId)
    {
        var current = localized;
        if (current == null)
            throw new PurchaseConfirmationException(
                PurchaseConfirmationFailure.DocumentsNotLoaded,
                "The SkyCofl legal documents are not loaded yet; try again shortly.");
        var key = $"{agreementId}:{(LocaleHelper.IsGerman(locale) ? "de" : "en")}";
        return current.TryGetValue(key, out var documents)
            ? documents
            : throw new PurchaseConfirmationException(
                PurchaseConfirmationFailure.DocumentsNotLoaded,
                $"Agreement {agreementId} is not loaded for purchase confirmation.");
    }

    /// <summary>
    /// Like <see cref="Get"/>, but attaches the withdrawal instructions the customer actually
    /// accepted. SkyApi stamps the purchase with the withdrawal version and sha256 it held at the
    /// time, which can be older than the version loaded here. A previous version is accepted only
    /// when its bytes hash to the purchase's sha256; otherwise the current documents are returned
    /// unchanged and <c>ValidateAgreement</c> reports the mismatch.
    /// </summary>
    internal async Task<LegalDocuments> GetForPurchase(
        string locale,
        string agreementId,
        PaymentEvent payment,
        CancellationToken cancellationToken = default)
    {
        var current = Get(locale, agreementId);
        if ((string.IsNullOrWhiteSpace(payment.WithdrawalVersion)
                && string.IsNullOrWhiteSpace(payment.WithdrawalSha256))
            || WithdrawalMatches(current.Withdrawal, payment))
            return current;

        var archived = await ResolveArchivedWithdrawal(
            locale, current.Withdrawal, payment, cancellationToken);
        if (archived == null)
            return current;
        logger?.LogInformation(
            "Attaching archived withdrawal instructions {Version} that the purchase accepted (current is {CurrentVersion})",
            archived.Version,
            current.Withdrawal.Version);
        return current with { Withdrawal = archived };
    }

    private static bool WithdrawalMatches(LegalDocument document, PaymentEvent payment) =>
        (string.IsNullOrWhiteSpace(payment.WithdrawalVersion)
            || string.Equals(payment.WithdrawalVersion, document.Version, StringComparison.OrdinalIgnoreCase))
        && (string.IsNullOrWhiteSpace(payment.WithdrawalSha256)
            || string.Equals(payment.WithdrawalSha256, document.Hash, StringComparison.OrdinalIgnoreCase));

    private async Task<LegalDocument> ResolveArchivedWithdrawal(
        string locale,
        LegalDocument current,
        PaymentEvent payment,
        CancellationToken cancellationToken)
    {
        var version = payment.WithdrawalVersion?.Trim();
        var expectedHash = payment.WithdrawalSha256?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(version) || !SafeVersion.IsMatch(version) || !IsSha256(expectedHash))
            return null;
        var language = LocaleHelper.IsGerman(locale) ? "de" : "en";
        var cacheKey = $"{language}:{version}:{expectedHash}".ToLowerInvariant();
        lock (archiveLock)
        {
            if (archiveCache.TryGetValue(cacheKey, out var cached))
                return cached;
        }

        var source = new Uri(current.Url);
        string url = null;
        var entry = withdrawalArchive.FirstOrDefault(item => string.Equals(
            item.Version, version, StringComparison.OrdinalIgnoreCase));
        if (entry?.Locales != null && entry.Locales.TryGetValue(language, out var listed))
        {
            if (!string.Equals(listed.Sha256, expectedHash, StringComparison.OrdinalIgnoreCase))
                return null;
            url = listed.Url;
        }
        else
        {
            // .../withdrawal-en-2026-09-30.md -> .../withdrawal-en-<purchase version>.md
            var name = current.FileName;
            var index = name.LastIndexOf(current.Version, StringComparison.Ordinal);
            if (index < 0)
                return null;
            url = new Uri(source, name[..index] + version + name[(index + current.Version.Length)..]).AbsoluteUri;
        }
        if (!TryUri(source, url, out var uri))
            return null;

        byte[] content;
        try
        {
            using var response = await clients.CreateClient(nameof(LegalDocumentProvider))
                .GetAsync(uri, cancellationToken);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                return null;
            response.EnsureSuccessStatusCode();
            content = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        }
        catch (Exception exception) when (
            exception is HttpRequestException
            || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            // not "no such version": retry later instead of a permanent mismatch
            throw new PurchaseConfirmationException(
                PurchaseConfirmationFailure.DocumentsNotLoaded,
                $"The archived withdrawal instructions {version} could not be fetched.",
                exception);
        }
        var hash = Sha256(content);
        if (!hash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            return null;

        var archived = new LegalDocument(
            "withdrawal",
            current.Title,
            Path.GetFileName(uri.AbsolutePath),
            version,
            uri.AbsoluteUri,
            content,
            hash,
            null);
        lock (archiveLock)
        {
            if (archiveCache.TryAdd(cacheKey, archived))
            {
                archiveOrder.Enqueue(cacheKey);
                while (archiveOrder.Count > ArchiveCacheLimit)
                    archiveCache.Remove(archiveOrder.Dequeue());
            }
        }
        return archived;
    }

    /// <summary>Test hook: publishes a snapshot as a completed load would.</summary>
    internal void SetLoadedForTests(
        Dictionary<string, LegalDocuments> snapshot,
        List<ArchiveEntry> archive = null)
    {
        localized = snapshot;
        withdrawalArchive = archive ?? [];
    }

    internal int ArchiveCacheCount
    {
        get { lock (archiveLock) return archiveCache.Count; }
    }

    private async Task RefreshUntilLoaded(CancellationToken cancellationToken)
    {
        var seconds = Math.Clamp(
            configuration.GetValue("LEGAL_MANIFEST_RETRY_SECONDS", 300),
            1,
            3600);
        while (!cancellationToken.IsCancellationRequested)
        {
            await TryRefresh(cancellationToken);
            if (!cancellationToken.IsCancellationRequested)
                await Task.Delay(TimeSpan.FromSeconds(seconds), cancellationToken);
        }
    }

    private async Task TryRefresh(CancellationToken cancellationToken)
    {
        try
        {
            await LoadCurrent(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger?.LogWarning(
                exception,
                "The SkyCofl legal manifest is unavailable; purchase confirmations remain delayed until it loads");
        }
    }

    private async Task LoadCurrent(CancellationToken cancellationToken)
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
            || (!environment.IsDevelopment() && source != CoflnetOrigin))
            throw new InvalidOperationException(
                "The legal manifest root is invalid.");
        if (!manifest.Documents.TryGetValue("withdrawal", out var withdrawal))
            throw new InvalidOperationException(
                "The legal manifest lacks confirmation documents.");

        var loaded = new Dictionary<string, LegalDocuments>();
        foreach (var agreementId in ConfirmationAgreementIds)
        {
            if (!manifest.Agreements.TryGetValue(agreementId, out var agreement)
                || !IsSha256(agreement.AgreementHash)
                || !TryUri(source, agreement.AgreementUrl, out var agreementUri)
                || agreement.ResolvedDocuments.Count == 0
                || agreement.ResolvedDocuments.Select(item => item.Key).Distinct().Count()
                    != agreement.ResolvedDocuments.Count)
                throw new InvalidOperationException(
                    $"The {agreementId} agreement root is invalid.");
            var descriptor = await client.GetByteArrayAsync(
                agreementUri,
                cancellationToken);
            var identity = Deserialize<AgreementDescriptor>(
                descriptor,
                $"The {agreementId} agreement descriptor is invalid.");
            if (!Sha256(descriptor).Equals(
                    agreement.AgreementHash,
                    StringComparison.OrdinalIgnoreCase)
                || identity.SchemaVersion != 1
                || identity.Kind != "coflnet-legal-agreement-node"
                || identity.Id != agreementId
                || identity.Type != "service")
                throw new InvalidOperationException(
                    $"The {agreementId} agreement descriptor is invalid.");

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
                            $"A resolved {agreementId} document does not match the agreement root.");
                    documents.Add(await Download(
                        client,
                        source,
                        summary.Key,
                        language,
                        document,
                        true,
                        cancellationToken));
                }

                loaded[$"{agreementId}:{language}"] = new(
                    agreementId,
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

        // publish atomically: readers never observe a partially populated set of locales
        withdrawalArchive = withdrawal.Archive ?? [];
        localized = loaded;
    }

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
            hash,
            document.AcceptanceHash);
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
        public List<ArchiveEntry> Archive { get; set; }
    }

    internal sealed class ArchiveEntry
    {
        public string Version { get; set; }
        public Dictionary<string, Locale> Locales { get; set; } = [];
    }

    internal sealed class Locale
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
    string Hash,
    string AcceptanceHash)
{
    internal Attachment CreateAttachment() =>
        new(new MemoryStream(Content), FileName, "text/markdown");
}
