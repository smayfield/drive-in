using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using Ganss.Xss;

namespace DriveIn.Web.Services;

// What the sanitizer needs to know about an image in the theater's library: its size, so pages reserve space for it,
// and its default description.
public sealed record LibraryImage(int Id, int Width, int Height, string? AltText);

// Cleans the HTML of theaters' pages and posts (from the editor, or pasted into it) down to an allowlist, so what a
// theater writes can never run script or pull in anything from elsewhere. Run when content is saved and again when it's
// shown, so tightening the rules here applies to everything already stored.
public sealed partial class HtmlContent(string? siteUrl = null)
{
    // This site's host (Notifications:SiteUrl, the site's public address), so links back to it stay in the same tab.
    private readonly string siteHost = Uri.TryCreate(siteUrl ?? "https://drive-in.online/", UriKind.Absolute, out var site) ? site.Host : "";

    // Image layout chosen in the editor: width, and where it sits (left and right let text wrap around it).
    public static readonly string[] ImageSizes = ["small", "medium", "full"];
    public static readonly string[] ImageAligns = ["left", "center", "right"];

    private static readonly string[] Tags =
        ["p", "br", "h2", "h3", "h4", "strong", "em", "u", "s", "a", "ul", "ol", "li", "blockquote", "hr", "img"];

    private static readonly HashSet<string> DropWithContent =
    [
        "script", "style", "noscript", "template", "iframe", "frame", "frameset", "object", "embed", "svg", "math",
        "textarea", "select", "option", "button", "title", "head", "audio", "video", "canvas",
    ];

    private static readonly string[] Classes =
    [
        "ql-align-center", "ql-align-right", "ql-align-justify",
        .. Enumerable.Range(1, 8).Select(i => $"ql-indent-{i}"),
        .. ImageSizes.Select(s => $"img-{s}"),
        .. ImageAligns.Select(a => $"img-{a}"),
    ];

    // The theater's content as safe HTML. Images must be from this theater's library (images); anything else is dropped.
    public string Sanitize(string? html, string theaterSlug, IReadOnlyDictionary<int, LibraryImage> images)
    {
        if (string.IsNullOrWhiteSpace(html))
            return "";
        var sanitizer = new HtmlSanitizer();
        sanitizer.AllowedTags.Clear();
        sanitizer.AllowedTags.UnionWith(Tags);
        sanitizer.AllowedAttributes.Clear();
        sanitizer.AllowedAttributes.UnionWith(["href", "src", "alt", "width", "height", "class"]);
        sanitizer.AllowedSchemes.Clear();
        sanitizer.AllowedSchemes.UnionWith(["http", "https", "mailto", "tel"]);
        sanitizer.AllowedCssProperties.Clear();
        sanitizer.AllowedAtRules.Clear();
        sanitizer.AllowedClasses.Clear();
        sanitizer.AllowedClasses.UnionWith(Classes);
        sanitizer.AllowDataAttributes = false;
        sanitizer.KeepChildNodes = true; // a disallowed wrapper (e.g. a span) keeps its text
        // ...but what's inside a script, style, frame or form control isn't text a reader should see.
        sanitizer.RemovingTag += (_, e) =>
        {
            if (DropWithContent.Contains(e.Tag.LocalName))
                e.Tag.InnerHtml = "";
        };

        var imagePath = $"theaters/{theaterSlug}/images/";
        sanitizer.PostProcessNode += (_, e) =>
        {
            switch (e.Node)
            {
                case IHtmlImageElement img:
                    FixImage(img, imagePath, images);
                    break;
                case IHtmlAnchorElement a:
                    FixLink(a);
                    break;
            }
        };
        return sanitizer.Sanitize(html).Trim();
    }

    // Keeps an image only if it's one of the theater's own, and writes its address, size and loading hints from the
    // library rather than trusting what was submitted.
    private static void FixImage(IHtmlImageElement img, string imagePath, IReadOnlyDictionary<int, LibraryImage> images)
    {
        var src = img.GetAttribute("src") ?? "";
        var match = ImageSrc().Match(src);
        if (!match.Success || !string.Equals(match.Groups["path"].Value, imagePath, StringComparison.Ordinal)
            || !int.TryParse(match.Groups["id"].Value, out var id) || !images.TryGetValue(id, out var image))
        {
            img.Remove();
            return;
        }
        img.SetAttribute("src", imagePath + id);
        img.SetAttribute("width", image.Width.ToString(System.Globalization.CultureInfo.InvariantCulture));
        img.SetAttribute("height", image.Height.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var alt = img.GetAttribute("alt");
        img.SetAttribute("alt", Truncate(alt ?? image.AltText ?? "", Data.TheaterImage.MaxAltLength));
        img.SetAttribute("loading", "lazy");
        img.SetAttribute("decoding", "async");
    }

    // Links can't act for the theater's page (no opener) or pass on where readers came from; links off the site open
    // in a new tab (absolute links to this site, like relative ones, don't).
    private void FixLink(IHtmlAnchorElement a)
    {
        var href = a.GetAttribute("href");
        if (string.IsNullOrWhiteSpace(href))
        {
            a.RemoveAttribute("href");
            return;
        }
        a.SetAttribute("rel", "noopener noreferrer nofollow");
        if (Uri.TryCreate(href, UriKind.Absolute, out var url) && url.Scheme is "http" or "https"
            && !string.Equals(url.Host, siteHost, StringComparison.OrdinalIgnoreCase))
            a.SetAttribute("target", "_blank");
    }

    // The text alone, with blocks separated by spaces: for summaries and descriptions.
    public static string ToPlainText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return "";
        var document = new HtmlParser().ParseDocument($"<body>{html}</body>");
        var text = new StringBuilder();
        Collect(document.Body!, text);
        return Whitespace().Replace(text.ToString(), " ").Trim();
    }

    private static void Collect(INode node, StringBuilder text)
    {
        foreach (var child in node.ChildNodes)
        {
            if (child is IText t)
                text.Append(t.Data);
            else if (child is IElement e)
            {
                var block = e.LocalName is "p" or "br" or "li" or "h2" or "h3" or "h4" or "blockquote" or "hr" or "div";
                if (block)
                    text.Append(' ');
                Collect(e, text);
                if (block)
                    text.Append(' ');
            }
        }
    }

    // Shortened to max characters at a word boundary, with an ellipsis.
    public static string Excerpt(string text, int max)
    {
        if (text.Length <= max)
            return text;
        var cut = text.LastIndexOf(' ', max - 1);
        return text[..(cut > max / 2 ? cut : max - 1)].TrimEnd(' ', ',', ';', ':', '.') + "…";
    }

    // No words and no images: nothing a reader would see.
    public static bool IsEffectivelyEmpty(string? html) =>
        ToPlainText(html).Length == 0 && html?.Contains("<img", StringComparison.OrdinalIgnoreCase) != true;

    // The first library image the HTML places, in document order (the share image when there's no cover).
    public static int? FirstImageId(string? html) =>
        html is not null && ImageRef().Match(html) is { Success: true } m
            ? int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : null;

    // The library image ids this HTML places: <img> sources only, not links or text that merely mention an image.
    public static IReadOnlySet<int> ImageIds(string? html) =>
        html is null ? new HashSet<int>() : ImageRef().Matches(html).Select(m => int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)).ToHashSet();

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max];

    // Relative ("theaters/x/images/3", "/theaters/...") or this site's absolute URL; the path is checked after.
    [GeneratedRegex(@"^(?:https?://[^/]+)?/?(?<path>theaters/[a-z0-9-]+/images/)(?<id>\d{1,9})(?:[?#].*)?$")]
    private static partial Regex ImageSrc();

    [GeneratedRegex(@"<img\b[^>]*?\ssrc=""(?:[^""]*/)?theaters/[a-z0-9-]+/images/(\d{1,9})(?:[?#][^""]*)?""", RegexOptions.IgnoreCase)]
    private static partial Regex ImageRef();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
