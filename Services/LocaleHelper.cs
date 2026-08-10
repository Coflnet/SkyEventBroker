using System;

namespace Coflnet.Sky.EventBroker.Services;

/// <summary>
/// Small locale helper shared between <see cref="PurchaseConfirmationEmailService"/> (its consumer)
/// and <see cref="LegalDocumentProvider"/>, so the provider does not need to reach into its own
/// consumer for a static helper.
/// </summary>
internal static class LocaleHelper
{
    internal static bool IsGerman(string locale) =>
        string.Equals(locale, "DE", StringComparison.OrdinalIgnoreCase)
        || string.Equals(locale, "de", StringComparison.OrdinalIgnoreCase)
        || locale?.StartsWith("de-", StringComparison.OrdinalIgnoreCase) == true
        || locale?.StartsWith("de_", StringComparison.OrdinalIgnoreCase) == true;
}
