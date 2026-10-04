using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WebScrapper.Core;

public enum PageState { Pending, Downloading, Saved, Existing, Failed, Skipped, Canceled }

public sealed record PageRecord(string Url, int Depth, bool IsPage, PageState State = PageState.Pending,
    int? HttpStatus = null, string? RelativePath = null, string Message = "Antre", int Attempts = 0,
    DateTimeOffset? UpdatedAt = null, string? EffectiveUrl = null)
{
    [JsonIgnore] public bool Successful => State is PageState.Saved or PageState.Existing;
}

// Deliberately contains no headers or cookies. Authentication is session-only.
public sealed record ProjectSettings(string StartUrl, string OutputDirectory, string UserAgent,
    int MaxDepth, int MaxFiles, long MaxFileBytes, long MaxTotalBytes, double DelayMs,
    double TimeoutSeconds, int MaxRetries, bool DownloadExternalAssets, bool RespectRobots, int JitterPercent = 25, bool AutoSwitchUserAgent = true,
    bool FastMode = false, int ParallelRequests = 4, string[]? ExcludedPaths = null, bool ResumeLastState = true, bool AutoCookie = true)
{
    public static ProjectSettings From(CrawlOptions o) => new(o.StartUri.AbsoluteUri, Path.GetFullPath(o.OutputDirectory), o.UserAgent,
        o.MaxDepth, o.MaxFiles, o.MaxFileBytes, o.MaxTotalBytes, o.RequestDelay.TotalMilliseconds, o.Timeout.TotalSeconds,
        o.MaxRetries, o.DownloadExternalAssets, o.RespectRobots, o.JitterPercent, o.AutoSwitchUserAgent, o.FastMode, o.ParallelRequests, o.ExcludedPaths.ToArray(), o.ResumeLastState, o.AutoCookie);
    public CrawlOptions ToOptions() => new()
    {
        StartUri = new Uri(StartUrl), OutputDirectory = OutputDirectory, UserAgent = UserAgent,
        MaxDepth = MaxDepth, MaxFiles = MaxFiles, MaxFileBytes = MaxFileBytes, MaxTotalBytes = MaxTotalBytes,
        RequestDelay = TimeSpan.FromMilliseconds(DelayMs), Timeout = TimeSpan.FromSeconds(TimeoutSeconds),
        MaxRetries = MaxRetries, DownloadExternalAssets = DownloadExternalAssets, RespectRobots = RespectRobots, JitterPercent = JitterPercent, AutoSwitchUserAgent = AutoSwitchUserAgent,
        FastMode = FastMode, ParallelRequests = ParallelRequests, ExcludedPaths = ExcludedPaths ?? ["/v8", "/v9"], ResumeLastState = ResumeLastState, AutoCookie = AutoCookie
    };
}

public sealed class CrawlProject
{
    private sealed record ProjectFile(int Version, ProjectSettings Settings, List<PageRecord> Pages, string? EntryRelativePath, bool AutoScrollResults = true, string? LastDownloadedUrl = null);
    private readonly object gate = new();
    private readonly Dictionary<string, PageRecord> pages = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> content = new(StringComparer.Ordinal);
    private long memoryBytes;
    private const long MemoryLimit = 32 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    public ProjectSettings Settings { get; set; }
    public string? EntryRelativePath { get; set; }
    public bool AutoScrollResults { get; set; } = true;
    public string? LastDownloadedUrl { get; private set; }
    public CrawlProject(CrawlOptions options) => Settings = ProjectSettings.From(options);
    public List<PageRecord> Pages { get { lock (gate) return pages.Values.ToList(); } }
    public int ContentCount { get { lock (gate) return content.Count; } }
    internal PageRecord? PageFor(Uri uri) { lock (gate) return pages.GetValueOrDefault(LinkRewriter.Key(uri)); }
    public string? ContentFor(string url) { lock (gate) return content.GetValueOrDefault(url); }
    internal void Remember(string url, string text)
    {
        lock (gate)
        {
            if (content.Remove(url, out var previous)) memoryBytes -= previous.Length * 2L;
            if (text.Length * 2L > 2 * 1024 * 1024 || memoryBytes + text.Length * 2L > MemoryLimit) return;
            content[url] = text; memoryBytes += text.Length * 2L;
        }
    }
    internal PageRecord Ensure(Uri uri, int depth, bool isPage)
    {
        lock (gate)
        {
            var key = LinkRewriter.Key(uri);
            if (!pages.TryGetValue(key, out var page)) pages.Add(key, page = new PageRecord(key, depth, isPage));
            return page;
        }
    }
    internal PageRecord? Update(Uri uri, PageState? state, int? status, string message, string? absolutePath)
    {
        lock (gate)
        {
            var key = LinkRewriter.Key(uri);
            if (!pages.TryGetValue(key, out var page)) return null;
            page = page with
            {
                State = state ?? page.State, HttpStatus = status ?? page.HttpStatus, Message = message,
                RelativePath = absolutePath is null ? page.RelativePath : Path.GetRelativePath(Settings.OutputDirectory, absolutePath),
                Attempts = page.Attempts + (state == PageState.Downloading && page.State != PageState.Downloading ? 1 : 0),
                UpdatedAt = DateTimeOffset.UtcNow
            };
            if (state == PageState.Saved) LastDownloadedUrl = key;
            pages[key] = page; return page;
        }
    }
    internal void SetEffective(Uri uri, Uri effective)
    {
        lock (gate)
        {
            var key = LinkRewriter.Key(uri);
            if (pages.TryGetValue(key, out var page)) pages[key] = page with { EffectiveUrl = LinkRewriter.Key(effective) };
        }
    }
    internal void MarkInterrupted()
    {
        lock (gate)
            foreach (var page in pages.Values.Where(p => p.State is PageState.Downloading or PageState.Pending).ToList())
                pages[page.Url] = page with { State = PageState.Canceled, Message = "Belum selesai; dapat scrap ulang", UpdatedAt = DateTimeOffset.UtcNow };
    }

    public async Task SaveAsync(string path, CancellationToken token = default)
    {
        var data = new ProjectFile(1, Settings, Pages, EntryRelativePath, AutoScrollResults, LastDownloadedUrl);
        var full = Path.GetFullPath(path); Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var temp = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(data, JsonOptions), token);
            File.Move(temp, full, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public static async Task<CrawlProject> LoadAsync(string path, CancellationToken token = default)
    {
        if (new FileInfo(path).Length > 64 * 1024 * 1024) throw new ArgumentException("File project terlalu besar.");
        var data = JsonSerializer.Deserialize<ProjectFile>(await File.ReadAllTextAsync(path, token), JsonOptions)
            ?? throw new ArgumentException("Project tidak valid.");
        if (data.Version != 1 || data.Pages is null || data.Pages.Count > 100000 || data.Settings is null) throw new ArgumentException("Versi/isi project tidak didukung.");
        var options = data.Settings.ToOptions(); options.Validate();
        var project = new CrawlProject(options) { EntryRelativePath = data.EntryRelativePath, AutoScrollResults = data.AutoScrollResults };
        if (project.EntryRelativePath is not null) LocalPaths.UnderRoot(options.OutputDirectory, project.EntryRelativePath);
        foreach (var p in data.Pages)
        {
            if (!Uri.TryCreate(p.Url, UriKind.Absolute, out var uri) || !CrawlOptions.IsHttp(uri) || uri.UserInfo.Length > 0 || p.Depth < 0 || !Enum.IsDefined(p.State)) throw new ArgumentException("Data halaman project tidak valid.");
            if (p.RelativePath is not null) LocalPaths.UnderRoot(options.OutputDirectory, p.RelativePath);
            project.pages[LinkRewriter.Key(uri)] = p.State is PageState.Downloading or PageState.Pending ? p with { State = PageState.Canceled, Message = "Belum selesai; dapat scrap ulang" } : p;
        }
        if (data.LastDownloadedUrl is not null && project.pages.ContainsKey(data.LastDownloadedUrl)) project.LastDownloadedUrl = data.LastDownloadedUrl;
        await project.SynchronizeFilesAsync(token);
        return project;
    }

    public async Task SynchronizeFilesAsync(CancellationToken token = default)
    {
        var cachePath = LocalPaths.UnderRoot(Settings.OutputDirectory, ".webscrapper-cache.json");
        Dictionary<string, Crawler.CacheEntry>? entries = null;
        try
        {
            if (File.Exists(cachePath) && new FileInfo(cachePath).Length <= 64 * 1024 * 1024)
                entries = JsonSerializer.Deserialize<Dictionary<string, Crawler.CacheEntry>>(await File.ReadAllTextAsync(cachePath, token));
        }
        catch (Exception ex) when (ex is JsonException or IOException) { }
        lock (gate) { content.Clear(); memoryBytes = 0; }
        foreach (var page in Pages)
        {
            token.ThrowIfCancellationRequested();
            var effective = page.EffectiveUrl ?? page.Url;
            if (!Uri.TryCreate(effective, UriKind.Absolute, out var uri) || !CrawlOptions.IsHttp(uri) || uri.UserInfo.Length > 0) throw new ArgumentException("URL efektif project tidak valid.");
            var identity = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(LinkRewriter.Key(uri))));
            bool valid = false; string? target = null; string? originalText = null;
            if (entries is not null && entries.TryGetValue(identity, out var entry))
            {
                try
                {
                    target = LocalPaths.UnderRoot(Settings.OutputDirectory, entry.Path);
                    if (File.Exists(target) && new FileInfo(target).Length == entry.Length && entry.Length <= Settings.MaxFileBytes)
                    {
                        await using var downloaded = File.OpenRead(target);
                        valid = Convert.ToHexString(await SHA256.HashDataAsync(downloaded, token)) == entry.Hash;
                    }
                    if (valid && entry.SourceHash is not null)
                    {
                        var source = LocalPaths.UnderRoot(Settings.OutputDirectory, Path.Combine(".webscrapper-sources", identity + ".txt"));
                        valid = File.Exists(source) && new FileInfo(source).Length <= Settings.MaxFileBytes * 4;
                        if (valid)
                        {
                            await using var original = File.OpenRead(source);
                            valid = Convert.ToHexString(await SHA256.HashDataAsync(original, token)) == entry.SourceHash;
                            if (valid && original.Length <= 2 * 1024 * 1024) originalText = await File.ReadAllTextAsync(source, token);
                        }
                    }
                    else if (valid && entry.MediaType is "text/html" or "application/xhtml+xml" or "text/css") valid = false;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { valid = false; }
            }
            if (valid)
            {
                Update(new Uri(page.Url), PageState.Existing, null, "File dan cache valid", target);
                if (originalText is not null) Remember(page.Url, originalText);
            }
            else if (page.Successful)
                Update(new Uri(page.Url), PageState.Failed, null, "File/cache hilang atau berubah; perlu scrap ulang", null);
        }
    }
}
