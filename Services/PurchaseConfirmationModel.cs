using System.Collections.Generic;

namespace Coflnet.Sky.EventBroker.Services;

internal enum SectionStyle
{
    Paragraphs,
    Bullets,
    Preformatted
}

internal sealed record EmailSection(
    string Heading,
    IReadOnlyList<string> Lines,
    SectionStyle Style = SectionStyle.Paragraphs);

/// <summary>
/// Language-resolved content of a purchase confirmation. Both the plain text and the HTML
/// rendering are produced from it so the two versions cannot drift apart.
/// </summary>
internal sealed record PurchaseConfirmationModel(
    string Title,
    string Intro,
    string OverviewHeading,
    IReadOnlyList<(string Label, string Value)> Overview,
    IReadOnlyList<string> OverviewNotes,
    IReadOnlyList<EmailSection> Sections,
    string Footer);
