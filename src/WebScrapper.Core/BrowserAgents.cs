namespace WebScrapper.Core;

public static class BrowserAgents
{
    public const string Chrome = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";
    public const string Firefox = "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:133.0) Gecko/20100101 Firefox/133.0";
    public static string Alternative(string current) => current.Contains("Firefox/", StringComparison.OrdinalIgnoreCase) ? Chrome : Firefox;
}
