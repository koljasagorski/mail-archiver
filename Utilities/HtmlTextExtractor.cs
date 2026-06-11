using System.Net;
using System.Text.RegularExpressions;

namespace MailArchiver.Utilities
{
    /// <summary>
    /// Converts archived HTML email bodies to readable plain text.
    /// Used by the REST API when an email was archived without a plain-text part.
    /// </summary>
    public static class HtmlTextExtractor
    {
        private static readonly Regex ScriptStyleRegex = new(
            @"<(script|style|head)[^>]*>.*?</\1>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

        private static readonly Regex LineBreakTagRegex = new(
            @"<(br|/p|/div|/tr|/li|/h[1-6])[^>]*>",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex TagRegex = new(
            @"<[^>]+>",
            RegexOptions.Compiled);

        private static readonly Regex HorizontalWhitespaceRegex = new(
            @"[ \t]+",
            RegexOptions.Compiled);

        private static readonly Regex ExcessNewlinesRegex = new(
            @"(\r?\n\s*){3,}",
            RegexOptions.Compiled);

        public static string ToPlainText(string? html)
        {
            if (string.IsNullOrWhiteSpace(html))
                return string.Empty;

            var text = ScriptStyleRegex.Replace(html, " ");
            text = LineBreakTagRegex.Replace(text, "\n");
            text = TagRegex.Replace(text, " ");
            text = WebUtility.HtmlDecode(text);
            text = HorizontalWhitespaceRegex.Replace(text, " ");
            text = ExcessNewlinesRegex.Replace(text, "\n\n");

            return text.Trim();
        }
    }
}
