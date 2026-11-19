using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Coflnet.Sky.EventBroker.Services;

/// <summary>
/// Renders a <see cref="PurchaseConfirmationModel"/> as plain text or email-safe HTML
/// (tables and inline styles only, no external resources).
/// </summary>
internal static partial class PurchaseConfirmationRenderer
{
    private const string Font = "font-family:Arial,Helvetica,sans-serif;color:#222222;";

    [GeneratedRegex(@"https://[^\s<]+")]
    private static partial Regex UrlPattern();

    public static string ToText(PurchaseConfirmationModel model)
    {
        var text = new StringBuilder();
        text.Append(model.Title).Append("\n===================================================\n\n");
        text.Append(model.Intro).Append("\n\n");
        text.Append(Heading(model.OverviewHeading)).Append('\n');
        foreach (var (label, value) in model.Overview)
            text.Append(label).Append(": ").Append(value).Append('\n');
        foreach (var note in model.OverviewNotes)
            text.Append(note).Append('\n');
        foreach (var section in model.Sections)
            AppendTextSection(text, section);
        text.Append('\n').Append(model.Footer);
        return text.ToString();
    }

    private static string Heading(string heading) =>
        heading.ToUpperInvariant() + "\n" + new string('-', heading.Length);

    private static void AppendTextSection(StringBuilder text, EmailSection section)
    {
        text.Append('\n').Append(Heading(section.Heading)).Append('\n');
        foreach (var line in section.Lines)
            text.Append(section.Style == SectionStyle.Bullets ? "- " : "").Append(line).Append('\n');
    }

    public static string ToHtml(PurchaseConfirmationModel model, string language)
    {
        var html = new StringBuilder();
        html.Append($"<!DOCTYPE html><html lang=\"{language}\"><head><meta charset=\"utf-8\">")
            .Append($"<title>{Encode(model.Title)}</title></head>");
        html.Append("<body style=\"margin:0;padding:0;background:#ffffff;\">");
        html.Append("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" style=\"background:#ffffff;\"><tr><td align=\"center\" style=\"padding:16px;\">");
        html.Append($"<table role=\"presentation\" width=\"600\" cellpadding=\"0\" cellspacing=\"0\" style=\"max-width:600px;width:100%;{Font}font-size:14px;line-height:1.5;\">");
        html.Append($"<tr><td style=\"font-size:22px;font-weight:bold;padding-bottom:8px;{Font}\">{Encode(model.Title)}</td></tr>");
        html.Append($"<tr><td style=\"padding-bottom:16px;{Font}\">{Linkify(model.Intro)}</td></tr>");
        html.Append("<tr><td>").Append(OverviewBox(model)).Append("</td></tr>");
        foreach (var section in model.Sections)
            html.Append("<tr><td style=\"padding-top:18px;\">").Append(HtmlSection(section)).Append("</td></tr>");
        html.Append($"<tr><td style=\"{Font}padding-top:24px;border-top:1px solid #dddddd;font-size:12px;color:#555555;\">{Encode(model.Footer)}</td></tr>");
        html.Append("</table></td></tr></table></body></html>");
        return html.ToString();
    }

    private static string OverviewBox(PurchaseConfirmationModel model)
    {
        var box = new StringBuilder();
        box.Append("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" style=\"background:#f4f5f7;border:1px solid #d9dce1;border-radius:6px;\"><tr><td style=\"padding:14px 16px;\">");
        box.Append($"<div style=\"font-weight:bold;padding-bottom:6px;{Font}\">{Encode(model.OverviewHeading)}</div>");
        box.Append("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\">");
        foreach (var (label, value) in model.Overview)
            box.Append($"<tr><td valign=\"top\" style=\"{Font}padding:2px 12px 2px 0;color:#555555;width:38%;\">{Encode(label)}</td><td valign=\"top\" style=\"padding:2px 0;{Font}\">{Linkify(value)}</td></tr>");
        box.Append("</table>");
        foreach (var note in model.OverviewNotes)
            box.Append($"<div style=\"padding-top:8px;{Font}\">{Linkify(note)}</div>");
        return box.Append("</td></tr></table>").ToString();
    }

    private static string HtmlSection(EmailSection section)
    {
        var html = new StringBuilder();
        html.Append($"<div style=\"font-weight:bold;padding-bottom:4px;{Font}\">{Encode(section.Heading)}</div>");
        switch (section.Style)
        {
            case SectionStyle.Preformatted:
                html.Append($"<pre style=\"margin:0;white-space:pre-wrap;font-size:12px;{Font}\">{Encode(string.Join("\n", section.Lines))}</pre>");
                break;
            case SectionStyle.Bullets:
                html.Append("<ul style=\"margin:0;padding-left:20px;\">");
                html.Append(string.Concat(section.Lines.Select(line => $"<li style=\"{Font}\">{Linkify(line)}</li>")));
                html.Append("</ul>");
                break;
            default:
                html.Append(string.Concat(section.Lines.Select(line => $"<p style=\"margin:0 0 6px 0;{Font}\">{Linkify(line)}</p>")));
                break;
        }
        return html.ToString();
    }

    /// <summary>Escapes markup characters only; the message is UTF-8 so umlauts stay readable.</summary>
    private static string Encode(string value) =>
        (value ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    private static string Linkify(string value) =>
        UrlPattern().Replace(Encode(value), match =>
            $"<a href=\"{match.Value}\" style=\"color:#1a56c4;\">{match.Value}</a>");
}
