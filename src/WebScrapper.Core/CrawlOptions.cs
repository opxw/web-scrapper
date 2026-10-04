using System.Net.Http.Headers;

namespace WebScrapper.Core;

public sealed record CrawlOptions
{
    public required Uri StartUri { get; init; }
    public required string OutputDirectory { get; init; }
    public string UserAgent { get; init; } = "WebScrapper/1.0";
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();
    public string CookieHeader { get; init; } = "";
    public int MaxDepth { get; init; } = 3;
    public int MaxFiles { get; init; } = 500;
    public long MaxFileBytes { get; init; } = 20 * 1024 * 1024;
    public long MaxTotalBytes { get; init; } = 500 * 1024 * 1024;
    public TimeSpan RequestDelay { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(45);
    public int MaxRetries { get; init; } = 4;
    public bool DownloadExternalAssets { get; init; }
    public bool RespectRobots { get; init; } = true;
    public int JitterPercent { get; init; } = 25;
    public bool AutoSwitchUserAgent { get; init; } = true;
    public bool FastMode { get; init; }
    public bool ResumeLastState { get; init; } = true;
    public bool AutoCookie { get; init; } = true;
    // Session-only: excluded from ProjectSettings and project serialization.
    public IReadOnlyList<System.Net.Cookie> BrowserCookies { get; init; } = [];
    public int ParallelRequests { get; init; } = 4;
    public IReadOnlyList<string> ExcludedPaths { get; init; } = ["/v8", "/v9"];

    public void Validate()
    {
        if (!IsHttp(StartUri) || !string.IsNullOrEmpty(StartUri.UserInfo))
            throw new ArgumentException("URL harus HTTP/HTTPS tanpa username/password di URL.");
        if (string.IsNullOrWhiteSpace(OutputDirectory)) throw new ArgumentException("Pilih folder hasil.");
        if (MaxDepth < 0 || MaxDepth > 30 || MaxFiles < 1 || MaxFiles > 100000 || MaxRetries < 0 || MaxRetries > 10)
            throw new ArgumentException("Batas kedalaman/file/retry tidak valid.");
        if (ParallelRequests < 2 || ParallelRequests > 8) throw new ArgumentException("Jumlah pekerja Fast harus 2 sampai 8.");
        if (ExcludedPaths.Count > 100 || ExcludedPaths.Any(p => string.IsNullOrWhiteSpace(p) || !p.StartsWith('/') || p.StartsWith("//") || p.IndexOfAny(['?', '#', '\r', '\n', '\\']) >= 0))
            throw new ArgumentException("Exclude path harus berupa path seperti /v8, tanpa domain, query, atau fragment; maksimal 100 path.");
        if (MaxFileBytes < 1 || MaxTotalBytes < MaxFileBytes || RequestDelay < TimeSpan.Zero || Timeout <= TimeSpan.Zero || JitterPercent < 0 || JitterPercent > 100)
            throw new ArgumentException("Batas ukuran, timeout, atau jeda tidak valid.");
        using var check = new HttpRequestMessage();
        if (string.IsNullOrWhiteSpace(UserAgent) || !check.Headers.TryAddWithoutValidation("User-Agent", UserAgent) || UserAgent.Contains('\r') || UserAgent.Contains('\n'))
            throw new ArgumentException("User-Agent tidak valid.");
        try { check.Headers.UserAgent.ParseAdd(UserAgent); } catch (FormatException) { throw new ArgumentException("User-Agent tidak valid."); }
        foreach (var (name, value) in Headers)
        {
            if (new[] { "Host", "Content-Length", "Connection", "Transfer-Encoding", "Cookie", "User-Agent" }.Contains(name, StringComparer.OrdinalIgnoreCase))
                throw new ArgumentException($"Header {name} dikelola aplikasi; gunakan kolom khusus bila tersedia.");
            if (value.Contains('\r') || value.Contains('\n') || !check.Headers.TryAddWithoutValidation(name, value))
                throw new ArgumentException("Nama/nilai header tidak valid.");
        }
        if (CookieHeader.Contains('\r') || CookieHeader.Contains('\n')) throw new ArgumentException("Cookie tidak valid.");
    }

    public static bool IsHttp(Uri uri) => uri.IsAbsoluteUri && (uri.Scheme == "http" || uri.Scheme == "https");
    public static bool SameOrigin(Uri a, Uri b) => a.Scheme == b.Scheme && a.IdnHost == b.IdnHost && a.Port == b.Port;
}

public sealed record CrawlProgress(string Resource, string State, int? StatusCode, int Saved, int Pending, long Bytes, string? LocalPath = null, PageRecord? Page = null);
public sealed record CrawlResult(string RunDirectory, string? EntryFile, int Saved, int Failed, int Skipped, long Bytes);
