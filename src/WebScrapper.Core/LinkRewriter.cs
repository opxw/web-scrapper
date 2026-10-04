using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using System.Text.RegularExpressions;

namespace WebScrapper.Core;

internal static partial class LinkRewriter
{
    internal sealed record Link(Uri Uri, bool Page);
    private static readonly string[] UrlAttributes = ["href", "xlink:href", "src", "poster", "data", "background"];
    private static readonly string[] SrcSetAttributes = ["srcset", "imagesrcset"];

    public static List<Link> Discover(string text, string mediaType, Uri source)
    {
        var links = new List<Link>();
        void Add(string value, Uri basis, bool page)
        { if (Resolve(basis, value) is { } uri) links.Add(new Link(uri, page)); }
        if (mediaType == "text/css")
        { foreach (Match m in CssUrls().Matches(text)) Add(CssValue(m), source, false); return links; }
        var doc = new HtmlParser().ParseDocument(text);
        var basis = BaseUri(doc, source);
        foreach (var el in doc.All)
        {
            foreach (var attr in el.Attributes.Where(a => UrlAttributes.Contains(a.Name) || a.LocalName == "href"))
                if (IsDownload(el, attr.LocalName)) Add(attr.Value, basis, IsPage(el));
            foreach (var attr in SrcSetAttributes)
                if (el.GetAttribute(attr) is { } srcset)
                    foreach (var candidate in SrcSet(srcset)) Add(candidate.Url, basis, false);
            if (el.GetAttribute("style") is { } style)
                foreach (Match m in CssUrls().Matches(style)) Add(CssValue(m), basis, false);
            if (el.LocalName == "style") foreach (Match m in CssUrls().Matches(el.TextContent)) Add(CssValue(m), basis, false);
        }
        return links;
    }

    public static string Rewrite(string text, string mediaType, Uri source, string sourcePath, IReadOnlyDictionary<string, string> available)
    {
        string ReplaceUrl(string value, Uri basis)
        {
            // Preserve same-document anchors. No network schemes or data URIs are modified.
            if (value.TrimStart().StartsWith('#') && Resolve(basis, value) is { } anchor && Key(anchor) == Key(source)) return value;
            if (Resolve(basis, value) is not { } uri) return value;
            if (!available.TryGetValue(Key(uri), out var target)) return uri.AbsoluteUri;
            var relative = Path.GetRelativePath(Path.GetDirectoryName(sourcePath)!, target).Replace('\\', '/');
            return string.Join('/', relative.Split('/').Select(s => s is "." or ".." ? s : Uri.EscapeDataString(s))) + uri.Fragment;
        }
        string RewriteCss(string css, Uri basis) => CssUrls().Replace(css, m =>
        {
            var value = CssValue(m);
            var replaced = ReplaceUrl(value, basis).Replace("\"", "%22");
            return m.Groups["import"].Success ? "@import \"" + replaced + "\"" : "url(\"" + replaced + "\")";
        });
        if (mediaType == "text/css") return RewriteCss(text, source);
        var doc = new HtmlParser().ParseDocument(text);
        var basis = BaseUri(doc, source);
        foreach (var el in doc.All)
        {
            // Rewrite navigation even for links beyond the crawl limits; keep their absolute online URL.
            foreach (var attr in el.Attributes.Where(a => UrlAttributes.Contains(a.Name) || a.LocalName == "href").ToArray())
                if (el.LocalName != "base") attr.Value = ReplaceUrl(attr.Value, basis);
            foreach (var attr in new[] { "action", "formaction" })
                if (el.GetAttribute(attr) is { } formTarget && Resolve(basis, formTarget) is { } remoteForm) el.SetAttribute(attr, remoteForm.AbsoluteUri);
            foreach (var attr in SrcSetAttributes)
                if (el.GetAttribute(attr) is { } srcset)
                    el.SetAttribute(attr, string.Join(", ", SrcSet(srcset).Select(c => ReplaceUrl(c.Url, basis) + c.Descriptor)));
            if (el.GetAttribute("style") is { } style) el.SetAttribute("style", RewriteCss(style, basis));
            if (el.LocalName == "style") el.TextContent = RewriteCss(el.TextContent, basis);
            // A modified asset cannot retain its original SRI hash.
            if (el.LocalName is "script" or "link")
            {
                var attribute = el.LocalName == "script" ? "src" : "href";
                var rewritten = el.GetAttribute(attribute);
                if (rewritten is not null && !Uri.TryCreate(rewritten, UriKind.Absolute, out _)) el.RemoveAttribute("integrity");
            }
        }
        foreach (var el in doc.QuerySelectorAll("base")) el.Remove();
        foreach (var el in doc.QuerySelectorAll("meta[charset], meta[http-equiv='Content-Type']")) el.Remove();
        var meta = doc.CreateElement("meta"); meta.SetAttribute("charset", "utf-8"); doc.Head?.Prepend(meta);
        return "<!DOCTYPE html>\n" + doc.DocumentElement.OuterHtml;
    }

    public static string Key(Uri uri) => new UriBuilder(uri) { Fragment = "" }.Uri.AbsoluteUri;
    public static Uri? Resolve(Uri basis, string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Uri.TryCreate(basis, value.Trim(), out var uri) || !CrawlOptions.IsHttp(uri) || uri.UserInfo.Length != 0) return null;
        return uri;
    }
    private static Uri BaseUri(IDocument doc, Uri source) => Resolve(source, doc.QuerySelector("base[href]")?.GetAttribute("href") ?? "") ?? source;
    private static bool IsPage(IElement el) => el.LocalName is "a" or "area" or "iframe" or "frame";
    private static bool IsDownload(IElement el, string attr) => attr is not ("href" or "xlink:href") || IsPage(el)
        || el.LocalName is "image" or "use" or "script"
        || el.LocalName == "link" && (el.GetAttribute("rel") ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Any(r => new[] { "stylesheet", "icon", "preload", "modulepreload", "apple-touch-icon", "mask-icon" }.Contains(r, StringComparer.OrdinalIgnoreCase));
    private static string CssValue(Match m) => m.Groups["quoted"].Success ? m.Groups["quoted"].Value : m.Groups["plain"].Success ? m.Groups["plain"].Value.Trim() : m.Groups["import"].Value;
    // Tokenize srcset without splitting commas inside data URLs.
    private static IEnumerable<(string Url, string Descriptor)> SrcSet(string value)
    {
        var i = 0;
        while (i < value.Length)
        {
            while (i < value.Length && (char.IsWhiteSpace(value[i]) || value[i] == ',')) i++;
            var start = i;
            while (i < value.Length && !char.IsWhiteSpace(value[i])) i++;
            var url = value[start..i];
            if (url.Length == 0) yield break;
            if (url.EndsWith(',')) { yield return (url.TrimEnd(','), ""); continue; }
            start = i;
            while (i < value.Length && value[i] != ',') i++;
            yield return (url, value[start..i]);
            if (i < value.Length) i++;
        }
    }

    [GeneratedRegex("url\\(\\s*(?:[\"'](?<quoted>.*?)[\"']|(?<plain>[^)]*))\\s*\\)|@import\\s+[\"'](?<import>[^\"']+)[\"']", RegexOptions.IgnoreCase)]
    private static partial Regex CssUrls();
}
