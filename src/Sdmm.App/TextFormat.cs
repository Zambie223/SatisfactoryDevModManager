using System.Text.RegularExpressions;

namespace Sdmm.App;

public static partial class TextFormat
{
    [GeneratedRegex(@"^\d+\. ")] private static partial Regex OrderedItem();
    [GeneratedRegex(@"\n{3,}")] private static partial Regex ManyBlankLines();
    [GeneratedRegex(@"!?\[([^\]]*)\]\(([^)]*)\)")] private static partial Regex Link();
    [GeneratedRegex(@"<[^>]+>")] private static partial Regex HtmlTag();

    /// <summary>
    /// Light Markdown cleanup for showing release notes and descriptions as plain text: joins soft-wrapped lines,
    /// drops heading marks, bold/italic markers, code backticks and HTML tags, keeps link text, and turns list
    /// dashes into bullets. Not a full Markdown renderer.
    /// </summary>
    public static string MarkdownToPlain(string md)
    {
        static bool StartsBlock(string trimmed) =>
            trimmed.StartsWith('#') || trimmed.StartsWith("- ") || trimmed.StartsWith("* ") || OrderedItem().IsMatch(trimmed);

        // Markdown treats a single line break inside a paragraph or list item as a space, so join those lines.
        var joined = new List<string>();
        foreach (var raw in md.Replace("\r\n", "\n").Split('\n'))
        {
            var trimmed = raw.Trim();
            if (trimmed.Length > 0 && joined.Count > 0 && joined[^1].Length > 0 && !StartsBlock(trimmed))
                joined[^1] += " " + trimmed;
            else
                joined.Add(trimmed);
        }

        var lines = joined.Select(body =>
        {
            if (body.StartsWith('#')) body = body.TrimStart('#').TrimStart();
            else if (body.StartsWith("- ") || body.StartsWith("* ")) body = "• " + body[2..];
            body = Link().Replace(body, m => m.Groups[1].Value);
            body = HtmlTag().Replace(body, "");
            return body.Replace("**", "").Replace("__", "").Replace("`", "");
        });
        return ManyBlankLines().Replace(string.Join("\n", lines), "\n\n").Trim();
    }
}
