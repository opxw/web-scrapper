using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;

namespace WebScrapper.Core;

public sealed class Crawler
{
    private sealed class ExcludedUrlException(string reason) : Exception(reason);
    private const string SkippedHostMessage = "Dilewati: host pagead.syndication.com";
    private static bool IsSkippedHost(Uri uri) => uri.IsAbsoluteUri && uri.IdnHost.TrimEnd('.').Equals("pagead.syndication.com", StringComparison.OrdinalIgnoreCase);
    private string? ExclusionReason(Uri uri)
    {
        if (IsSkippedHost(uri)) return SkippedHostMessage;
        var path = Uri.UnescapeDataString(uri.AbsolutePath);
        if (uri.IdnHost.TrimEnd('.').Equals("pagead2.googlesyndication.com", StringComparison.OrdinalIgnoreCase)
            && path.Equals("/pagead/js/adsbygoogle.js", StringComparison.Ordinal))
            return "Dilewati: script iklan pagead2.googlesyndication.com/pagead/js/adsbygoogle.js";
        foreach (var configured in options.ExcludedPaths)
        {
            var excluded = Uri.UnescapeDataString(configured.Trim()).TrimEnd('/');
            if (excluded.Length == 0 || path.Equals(excluded, StringComparison.OrdinalIgnoreCase) || path.StartsWith(excluded + "/", StringComparison.OrdinalIgnoreCase))
                return "Dilewati: exclude path " + configured;
        }
        return null;
    }
    private sealed record Work(Uri Uri, int Depth, bool Page, Uri? Referrer);
    private sealed record Document(Uri Uri, string Path, string MediaType, string Charset);
    private sealed class FetchResult(Work work) : IDisposable
    {
        public Work Work { get; } = work;
        public Document? Cached { get; set; }
        public HttpResponseMessage? Response { get; set; }
        public string? TemporaryFile { get; set; }
        public string? SkipMessage { get; set; }
        public Exception? Error { get; set; }
        public void Dispose()
        {
            Response?.Dispose();
            if (TemporaryFile is not null && File.Exists(TemporaryFile)) File.Delete(TemporaryFile);
        }
    }
    public sealed record CacheEntry(string Path, string MediaType, string Charset, long Length, string Hash, string? SourceHash = null);
    private readonly ConcurrentDictionary<string, RobotsRules> robots = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> robotsSources = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim requestGate = new(1, 1);
    private readonly SemaphoreSlim robotsGate = new(1, 1);
    private readonly object stateGate = new();
    private readonly object reportGate = new();
    private string activeAgent = "";
    private bool agentSwitched;
    private readonly Dictionary<string, string> available = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CacheEntry> cache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Document> documents = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset nextRequest;
    private CrawlOptions options = null!;
    private HttpClient client = null!;
    private IProgress<CrawlProgress>? progress;
    private int saved, failed, skipped;
    private long bytes;
    private string root = "";
    private string cacheFile = "";
    private CrawlProject project = null!;
    private CookieContainer cookies = null!;

    public Task<CrawlResult> FixUrlsAsync(CrawlProject currentProject, IProgress<CrawlProgress>? reporter, CancellationToken token) =>
        RunAsync(currentProject.Settings.ToOptions(), reporter, token, currentProject, localOnly: true);

    public async Task<CrawlResult> RunAsync(CrawlOptions settings, IProgress<CrawlProgress>? reporter, CancellationToken token, CrawlProject? currentProject = null, bool retryIncomplete = false, bool localOnly = false)
    {
        settings.Validate(); options = settings; progress = reporter;
        activeAgent = settings.UserAgent; agentSwitched = false; robotsSources.Clear();
        project = currentProject ?? new CrawlProject(settings); project.Settings = ProjectSettings.From(settings);
        robots.Clear(); available.Clear(); cache.Clear(); documents.Clear(); saved = failed = skipped = 0; bytes = 0; nextRequest = default;
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        root = Path.GetFullPath(options.OutputDirectory);
        Directory.CreateDirectory(root);
        cacheFile = LocalPaths.UnderRoot(root, ".webscrapper-cache.json");
        // Lock prevents two application instances writing the same output concurrently.
        var lockPath = LocalPaths.UnderRoot(root, ".webscrapper.lock");
        await using var outputLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
        if (File.Exists(cacheFile))
        {
            if (new FileInfo(cacheFile).Length > 64 * 1024 * 1024) throw new IOException("Cache terlalu besar.");
            try
            {
                var previous = JsonSerializer.Deserialize<Dictionary<string, CacheEntry>>(await File.ReadAllTextAsync(cacheFile, token));
                if (previous is not null) foreach (var item in previous) cache.Add(item.Key, item.Value);
            }
            catch (JsonException) { Report(options.StartUri, "Metadata lama tidak valid; file lama dilindungi.", null, 0); }
        }
        if (localOnly)
        {
            await project.SynchronizeFilesAsync(token);
            foreach (var page in project.Pages.Where(p => p.Successful))
                if (await TryReuseAsync(new Uri(page.EffectiveUrl ?? page.Url), token) is { } doc)
                {
                    available[page.Url] = doc.Path;
                    available[LinkRewriter.Key(doc.Uri)] = doc.Path;
                    if (doc.MediaType is "text/html" or "application/xhtml+xml" or "text/css") documents.TryAdd(doc.Path, doc);
                }
            var fixedCount = await RewriteDocumentsAsync(token);
            return new CrawlResult(root, project.EntryRelativePath is null ? null : LocalPaths.UnderRoot(root, project.EntryRelativePath), fixedCount,
                project.Pages.Count(p => p.State == PageState.Failed), documents.Count - fixedCount, 0);
        }
        cookies = new CookieContainer();
        foreach (var browserCookie in options.BrowserCookies) cookies.Add(options.StartUri, browserCookie);
        if (!string.IsNullOrWhiteSpace(options.CookieHeader)) cookies.SetCookies(options.StartUri, options.CookieHeader.Replace(';', ','));
        using var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false, UseCookies = false,
            AutomaticDecompression = DecompressionMethods.All
        };
        using var http = new HttpClient(handler) { Timeout = options.Timeout };
        client = http;
        await project.SynchronizeFilesAsync(token);
        var queue = new Queue<Work>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var resume = options.ResumeLastState && project.Pages.Count > 0;
        void Enqueue(Uri uri, int depth, bool page, Uri? referrer = null)
        {
            if (!CrawlOptions.IsHttp(uri) || depth > options.MaxDepth || seen.Count >= options.MaxFiles) return;
            var key = LinkRewriter.Key(uri);
            if ((resume || retryIncomplete) && project.PageFor(uri)?.Successful == true) return;
            if (ExclusionReason(uri) is { } exclusion)
            {
                if (seen.Add(key))
                {
                    project.Ensure(uri, depth, page); skipped++;
                    Report(uri, exclusion, null, queue.Count, state: PageState.Skipped);
                }
                return;
            }
            if (!Allowed(uri, page)) return;
            if (seen.Add(key))
            {
                project.Ensure(new Uri(key), depth, page);
                queue.Enqueue(new Work(new Uri(key), depth, page, referrer));
                Report(uri, "Antre", null, queue.Count, state: PageState.Pending);
            }
        }
        if (retryIncomplete || resume)
        {
            var recorded = project.Pages;
            var last = resume ? recorded.FindIndex(p => p.Url == project.LastDownloadedUrl) : -1;
            var ordered = recorded.Skip(last + 1).Concat(recorded.Take(last + 1));
            foreach (var page in ordered.Where(p => !p.Successful)) Enqueue(new Uri(page.Url), page.Depth, page.IsPage);
        }
        else Enqueue(options.StartUri, 0, true);
        string? entryFile = project.EntryRelativePath is null ? null : LocalPaths.UnderRoot(root, project.EntryRelativePath);
        foreach (var page in project.Pages.Where(p => p.Successful))
            if (await TryReuseAsync(new Uri(page.EffectiveUrl ?? page.Url), token) is { } existing)
            {
                available[page.Url] = existing.Path;
                available[LinkRewriter.Key(existing.Uri)] = existing.Path;
                // Recover previously undiscovered source assets from original cached markup.
                // Do not re-fetch successful documents or restart page navigation on resume.
                if ((resume || retryIncomplete) && existing.MediaType is "text/html" or "application/xhtml+xml" or "text/css")
                    foreach (var link in LinkRewriter.Discover(await ReadTextAsync(existing, token), existing.MediaType, existing.Uri).Where(l => !l.Page))
                        Enqueue(link.Uri, page.Depth, false, existing.Uri);
            }
        try
        {
            while (queue.Count > 0 && bytes < options.MaxTotalBytes)
            {
                token.ThrowIfCancellationRequested();
                var batch = new List<Work>();
                var workerCount = options.FastMode ? options.ParallelRequests : 1;
                while (batch.Count < workerCount && queue.TryDequeue(out var queued)) batch.Add(queued);
                // Only independent HTTP/temp streams run concurrently. Final files, cache,
                // queue discovery and project completion remain serial and deterministic.
                var fetched = await Task.WhenAll(batch.Select(work => FetchAsync(work, queue.Count, token)));
                try
                {
                  foreach (var fetch in fetched)
                  {
                    var work = fetch.Work;
                    token.ThrowIfCancellationRequested();
                    try
                    {
                    if (fetch.Error is { } error) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
                    if (fetch.SkipMessage is { } skipMessage) { skipped++; Report(work.Uri, skipMessage, null, queue.Count, state: PageState.Skipped); continue; }
                    Document? doc = fetch.Cached;
                    if (doc is not null)
                    {
                        saved++; available[LinkRewriter.Key(work.Uri)] = doc.Path;
                        Report(work.Uri, "Sudah ada: hash valid, tanpa download", null, queue.Count, doc.Path, PageState.Existing);
                    }
                    else
                    {
                        var response = fetch.Response!;
                        var effective = response.RequestMessage!.RequestUri!;
                        project.SetEffective(work.Uri, effective);
                        if (!response.IsSuccessStatusCode)
                        {
                            failed++; Report(work.Uri, HttpFailure.Describe(response), (int)response.StatusCode, queue.Count, state: PageState.Failed); continue;
                        }
                        var media = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant() ?? GuessMedia(effective);
                        var relative = LocalPaths.ForUri(effective, media);
                        var destination = LocalPaths.UnderRoot(root, relative);
                        if (File.Exists(destination))
                        {
                            doc = await TryReuseAsync(effective, token);
                            if (doc is null) { skipped++; Report(work.Uri, "File sudah ada; tidak ditimpa", (int)response.StatusCode, queue.Count, state: PageState.Skipped); continue; }
                            available[LinkRewriter.Key(work.Uri)] = destination; saved++;
                            Report(work.Uri, "Sudah ada: tujuan redirect valid", (int)response.StatusCode, queue.Count, destination, PageState.Existing);
                        }
                        else
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                            File.Move(fetch.TemporaryFile!, destination, false);
                            fetch.TemporaryFile = null;
                            var charset = response.Content.Headers.ContentType?.CharSet?.Trim('"') ?? "utf-8";
                            doc = new Document(effective, destination, media, charset);
                            saved++;
                            available[LinkRewriter.Key(work.Uri)] = destination;
                            available[LinkRewriter.Key(effective)] = destination;
                            await RecordCacheAsync(doc, token);
                            Report(work.Uri, "Tersimpan", (int)response.StatusCode, queue.Count, destination, PageState.Saved);
                        }
                    }
                    if (work.Depth == 0 && work.Page)
                    { entryFile ??= doc.Path; project.EntryRelativePath = Path.GetRelativePath(root, entryFile); }
                    available[LinkRewriter.Key(doc.Uri)] = doc.Path;
                    if (doc.MediaType is "text/html" or "application/xhtml+xml" or "text/css")
                    {
                        documents[doc.Path] = doc;
                        var text = await ReadTextAsync(doc, token);
                        project.Remember(LinkRewriter.Key(work.Uri), text);
                        foreach (var link in LinkRewriter.Discover(text, doc.MediaType, doc.Uri)) Enqueue(link.Uri, work.Depth + (link.Page ? 1 : 0), link.Page, doc.Uri);
                    }
                    }
                catch (ExcludedUrlException ex) { skipped++; Report(work.Uri, ex.Message, null, queue.Count, state: PageState.Skipped); }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { failed++; Report(work.Uri, "Timeout request", null, queue.Count, state: PageState.Failed); }
                catch (Exception ex) when (ex is HttpRequestException or IOException or ArgumentException or UnauthorizedAccessException)
                { failed++; Report(work.Uri, ex is IOException ? "Gagal file/batas ukuran (file lama dilindungi)" : "Gagal request atau akses file", null, queue.Count, state: PageState.Failed); }
                  }
                }
                finally { foreach (var fetch in fetched) fetch.Dispose(); }
            }
            Report(options.StartUri, "Menulis ulang tautan lokal…", null, 0, updatePage: false);
            // Rewrite previously saved documents too, so newly retried assets become local links.
            foreach (var page in project.Pages.Where(p => p.Successful))
                if (await TryReuseAsync(new Uri(page.EffectiveUrl ?? page.Url), token) is { } prior && prior.MediaType is "text/html" or "application/xhtml+xml" or "text/css") documents.TryAdd(prior.Path, prior);
            await RewriteDocumentsAsync(token);
            return new CrawlResult(root, entryFile, saved, failed, skipped, bytes);
        }
        finally { project.MarkInterrupted(); client = null!; }
    }

    private async Task<int> RewriteDocumentsAsync(CancellationToken token)
    {
        var rewrittenCount = 0;
        foreach (var doc in documents.Values)
            {
                token.ThrowIfCancellationRequested();
                // Verify ownership again before rewriting a file.
                if (await TryReuseAsync(doc.Uri, token) is null) continue;
                var rewritten = LinkRewriter.Rewrite(await ReadTextAsync(doc, token), doc.MediaType, doc.Uri, doc.Path, available);
                var temp = doc.Path + "." + Guid.NewGuid().ToString("N") + ".part";
                try
                {
                    await File.WriteAllTextAsync(temp, rewritten, new UTF8Encoding(false), token);
                    LocalPaths.UnderRoot(root, Path.GetRelativePath(root, doc.Path));
                    File.Move(temp, doc.Path, true);
                }
                finally { if (File.Exists(temp)) File.Delete(temp); }
                await RecordCacheAsync(doc with { Charset = "utf-8" }, token);
                rewrittenCount++;
                Report(doc.Uri, $"Fix URL: {rewrittenCount}/{documents.Count} dokumen", null, documents.Count - rewrittenCount, updatePage: false);
            }
        return rewrittenCount;
    }

    private bool Allowed(Uri uri, bool page) => CrawlOptions.IsHttp(uri) && uri.UserInfo.Length == 0 && (CrawlOptions.SameOrigin(uri, options.StartUri) || !page && options.DownloadExternalAssets);

    private async Task<FetchResult> FetchAsync(Work work, int pending, CancellationToken token)
    {
        var result = new FetchResult(work);
        try
        {
            Report(work.Uri, "Memeriksa file dan akses", null, pending, state: PageState.Downloading);
            if (!await RobotsAllowsAsync(work.Uri, token)) { result.SkipMessage = "Dilewati: robots.txt"; return result; }
            var known = project.PageFor(work.Uri);
            var effective = new Uri(known?.EffectiveUrl ?? work.Uri.AbsoluteUri);
            if (ExclusionReason(effective) is { } exclusion) throw new ExcludedUrlException(exclusion);
            result.Cached = await TryReuseAsync(effective, token);
            if (result.Cached is not null) return result;
            if (cache.TryGetValue(Identity(work.Uri), out var old) && File.Exists(LocalPaths.UnderRoot(root, old.Path)))
            { result.SkipMessage = "File lama berubah/tidak valid; tidak ditimpa"; return result; }
            var predicted = LocalPaths.UnderRoot(root, LocalPaths.ForUri(work.Uri, work.Page ? "text/html" : GuessMedia(work.Uri)));
            if (File.Exists(predicted)) { result.SkipMessage = "File sudah ada tanpa metadata cocok; tidak ditimpa"; return result; }
            var response = result.Response = await SendAsync(work.Uri, work.Page, token, work.Referrer);
            if (!response.IsSuccessStatusCode) return result;
            effective = response.RequestMessage!.RequestUri!;
            var media = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant() ?? GuessMedia(effective);
            var destination = LocalPaths.UnderRoot(root, LocalPaths.ForUri(effective, media));
            if (File.Exists(destination)) return result; // Serial commit verifies ownership before reuse.
            if (response.Content.Headers.ContentLength > options.MaxFileBytes) throw new IOException("File melebihi batas ukuran.");
            var temp = result.TemporaryFile = LocalPaths.UnderRoot(root, ".fetch-" + Guid.NewGuid().ToString("N") + ".part");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(options.Timeout);
            await using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
            await using var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
            var buffer = new byte[81920]; long written = 0; int count;
            while ((count = await input.ReadAsync(buffer, timeout.Token)) > 0)
            {
                lock (stateGate)
                {
                    if (written + count > options.MaxFileBytes || bytes + count > options.MaxTotalBytes) throw new IOException("Batas ukuran file/total tercapai.");
                    bytes += count;
                }
                await output.WriteAsync(buffer.AsMemory(0, count), timeout.Token); written += count;
            }
        }
        catch (Exception ex) { result.Error = ex; }
        return result;
    }

    private void ExtendCooldown(TimeSpan wait)
    {
        if (wait <= TimeSpan.Zero) return;
        lock (stateGate)
        {
            var until = DateTimeOffset.UtcNow + wait;
            if (until > nextRequest) nextRequest = until;
        }
    }

    private async Task WaitForRequestSlotAsync(Uri uri, CancellationToken token)
    {
        await requestGate.WaitAsync(token);
        try
        {
            while (true)
            {
                TimeSpan pause;
                lock (stateGate)
                {
                    pause = nextRequest - DateTimeOffset.UtcNow;
                    if (pause <= TimeSpan.Zero)
                    {
                        var delay = options.RequestDelay;
                        if (robots.TryGetValue(uri.GetLeftPart(UriPartial.Authority), out var rules) && rules.Delay > delay) delay = rules.Delay;
                        nextRequest = DateTimeOffset.UtcNow + delay + TimeSpan.FromMilliseconds(delay.TotalMilliseconds * options.JitterPercent / 100 * Random.Shared.NextDouble());
                        return;
                    }
                }
                await Task.Delay(pause > TimeSpan.FromMinutes(1) ? TimeSpan.FromMinutes(1) : pause, token);
            }
        }
        finally { requestGate.Release(); }
    }

    private async Task<HttpResponseMessage> SendAsync(Uri initial, bool page, CancellationToken token, Uri? referrer = null)
    {
        var uri = initial;
        var retrievingRobots = initial.AbsolutePath == "/robots.txt";
        for (var redirect = 0; redirect <= 10; redirect++)
        {
            if (ExclusionReason(uri) is { } excluded) throw new ExcludedUrlException(excluded);
            for (var retry = 0; ; retry++)
            {
                await WaitForRequestSlotAsync(uri, token);
                if (options.RespectRobots && !retrievingRobots && robots.TryGetValue(uri.GetLeftPart(UriPartial.Authority), out var applicableRules) && !applicableRules.Allows(uri))
                    throw new HttpRequestException("Request diblokir robots.txt untuk User-Agent aktif.");
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                lock (stateGate) request.Headers.TryAddWithoutValidation("User-Agent", activeAgent);
                request.Headers.TryAddWithoutValidation("Accept", page ? "text/html,application/xhtml+xml;q=0.9,*/*;q=0.8" : "*/*");
                request.Headers.TryAddWithoutValidation("Accept-Language", "id-ID,id;q=0.9,en;q=0.8");
                // All custom headers are origin-bound, including Authorization, Referer and API keys.
                if (CrawlOptions.SameOrigin(uri, options.StartUri))
                {
                    if (referrer is not null && CrawlOptions.SameOrigin(uri, referrer)) request.Headers.Referrer = new Uri(referrer.GetLeftPart(UriPartial.Path));
                    foreach (var (name, value) in options.Headers) { request.Headers.Remove(name); request.Headers.TryAddWithoutValidation(name, value); }
                    string cookieHeader;
                    lock (stateGate) cookieHeader = cookies.GetCookieHeader(uri);
                    if (cookieHeader.Length > 0) request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
                }
                HttpResponseMessage response;
                try { response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token); }
                catch (HttpRequestException) when (retry < options.MaxRetries)
                { await WaitRetryAsync(uri, retry, null, null, token); continue; }
                if (CrawlOptions.SameOrigin(uri, options.StartUri) && response.Headers.TryGetValues("Set-Cookie", out var setCookies))
                    foreach (var value in setCookies)
                        try { lock (stateGate) cookies.SetCookies(uri, value); } catch (CookieException) { /* Ignore malformed server cookies. */ }
                var status = (int)response.StatusCode;
                bool switchedNow = false;
                if (options.AutoSwitchUserAgent && !retrievingRobots && HttpFailure.RequiresBrowserVerification(response))
                {
                    lock (stateGate)
                    {
                        if (!agentSwitched)
                        {
                            agentSwitched = switchedNow = true;
                            activeAgent = BrowserAgents.Alternative(activeAgent);
                            foreach (var source in robotsSources) robots[source.Key] = RobotsRules.Parse(source.Value, activeAgent);
                        }
                    }
                }
                if (switchedNow)
                {
                    var serverWait = response.Headers.RetryAfter?.Delta ?? response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow ?? TimeSpan.Zero;
                    var switchWait = serverWait > TimeSpan.FromSeconds(2) ? serverWait : TimeSpan.FromSeconds(2);
                    ExtendCooldown(switchWait);
                    response.Dispose();
                    Report(uri, "Verifikasi browser: ganti User-Agent ke " + (activeAgent == BrowserAgents.Firefox ? "Firefox" : "Chrome") + "; coba sekali lagi setelah jeda", 403, 0);
                    retry--; // Separate, single-use budget; does not consume the transient retry budget.
                    continue;
                }
                if (status is 429 or 503 && retry >= options.MaxRetries)
                {
                    var minimumWait = response.Headers.RetryAfter?.Delta ?? response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow ?? TimeSpan.FromSeconds(30);
                    ExtendCooldown(minimumWait);
                }
                if (status is 429 or 503 or 502 or 504 && retry < options.MaxRetries)
                {
                    var retryAfter = response.Headers.RetryAfter;
                    var serverWait = retryAfter?.Delta ?? (retryAfter?.Date - DateTimeOffset.UtcNow);
                    response.Dispose();
                    await WaitRetryAsync(uri, retry, serverWait, status, token); continue;
                }
                if (status is 301 or 302 or 303 or 307 or 308 && response.Headers.Location is { } location)
                {
                    var target = new Uri(uri, location);
                    response.Dispose();
                    if (ExclusionReason(target) is { } targetExclusion) throw new ExcludedUrlException(targetExclusion);
                    if (!Allowed(target, page) || uri.Scheme == "https" && target.Scheme == "http") throw new HttpRequestException("Redirect di luar cakupan atau downgrade HTTPS.");
                    if (!retrievingRobots && !target.AbsolutePath.Equals("/robots.txt", StringComparison.Ordinal) && !await RobotsAllowsAsync(target, token)) throw new HttpRequestException("Redirect diblokir robots.txt.");
                    if (!retrievingRobots && !target.AbsolutePath.Equals("/robots.txt", StringComparison.Ordinal) && await TryReuseAsync(target, token) is { } cachedTarget)
                    {
                        // Redirect target already exists: do not GET it again.
                        var cachedResponse = new HttpResponseMessage(HttpStatusCode.OK)
                        { RequestMessage = new HttpRequestMessage(HttpMethod.Get, target), Content = new ByteArrayContent([]) };
                        cachedResponse.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(cachedTarget.MediaType);
                        return cachedResponse;
                    }
                    uri = target; break;
                }
                return response;
            }
        }
        throw new HttpRequestException("Terlalu banyak redirect.");
    }

    private async Task WaitRetryAsync(Uri uri, int retry, TimeSpan? serverWait, int? status, CancellationToken token)
    {
        var backoff = TimeSpan.FromSeconds(Math.Min(120, Math.Pow(2, retry + 1)) + Random.Shared.NextDouble());
        var wait = serverWait is { } requested && requested > backoff ? requested : backoff;
        if (wait < options.RequestDelay) wait = options.RequestDelay;
        ExtendCooldown(wait);
        Report(uri, $"Retry {retry + 1}/{options.MaxRetries}: tunggu {wait.TotalSeconds:N0} detik", status, 0);
        // Delay in chunks also supports very large valid Retry-After values and cancellation.
        while (wait > TimeSpan.Zero) { var chunk = wait > TimeSpan.FromMinutes(1) ? TimeSpan.FromMinutes(1) : wait; await Task.Delay(chunk, token); wait -= chunk; }
    }

    private async Task<bool> RobotsAllowsAsync(Uri uri, CancellationToken token)
    {
        if (!options.RespectRobots) return true;
        await robotsGate.WaitAsync(token);
        try
        {
        var origin = uri.GetLeftPart(UriPartial.Authority);
        if (!robots.TryGetValue(origin, out var rules))
        {
            // Seed first to avoid recursion when robots.txt redirects within the same origin.
            robots[origin] = RobotsRules.Parse("User-agent: *\nDisallow: /", activeAgent);
            string source;
            using var response = await SendAsync(new Uri(origin + "/robots.txt"), false, token);
            if (response.IsSuccessStatusCode)
            {
                if (response.Content.Headers.ContentLength > 512 * 1024) throw new HttpRequestException("robots.txt terlalu besar.");
                using var bodyTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                bodyTimeout.CancelAfter(options.Timeout);
                var bodyToken = bodyTimeout.Token;
                await using var stream = await response.Content.ReadAsStreamAsync(bodyToken);
                using var content = new MemoryStream();
                var buffer = new byte[8192]; int count;
                while ((count = await stream.ReadAsync(buffer, bodyToken)) > 0)
                { if (content.Length + count > 512 * 1024) throw new HttpRequestException("robots.txt terlalu besar."); await content.WriteAsync(buffer.AsMemory(0, count), bodyToken); }
                source = Encoding.UTF8.GetString(content.ToArray());
            }
            else if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone) source = "";
            else source = "User-agent: *\nDisallow: /";
            lock (stateGate)
            {
                robotsSources[origin] = source;
                rules = RobotsRules.Parse(source, activeAgent);
                robots[origin] = rules;
            }
        }
        return rules.Allows(uri);
        }
        finally { robotsGate.Release(); }
    }

    private async Task<Document?> TryReuseAsync(Uri uri, CancellationToken token)
    {
        if (!cache.TryGetValue(Identity(uri), out var entry)) return null;
        var path = LocalPaths.UnderRoot(root, entry.Path);
        if (!File.Exists(path) || new FileInfo(path).Length != entry.Length || entry.Length > options.MaxFileBytes) return null;
        await using var stream = File.OpenRead(path);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, token));
        if (hash != entry.Hash) return null;
        if (entry.MediaType is "text/html" or "application/xhtml+xml" or "text/css")
        {
            var sourcePath = SourcePath(uri);
            if (entry.SourceHash is null || !File.Exists(sourcePath) || new FileInfo(sourcePath).Length > options.MaxFileBytes * 4) return null;
            await using var original = File.OpenRead(sourcePath);
            if (Convert.ToHexString(await SHA256.HashDataAsync(original, token)) != entry.SourceHash) return null;
        }
        return new Document(uri, path, entry.MediaType, entry.Charset);
    }

    private async Task RecordCacheAsync(Document doc, CancellationToken token)
    {
        string? sourceHash = null;
        if (doc.MediaType is "text/html" or "application/xhtml+xml" or "text/css")
        {
            if (cache.TryGetValue(Identity(doc.Uri), out var previous)) sourceHash = previous.SourceHash;
            else
            {
                var sourcePath = SourcePath(doc.Uri);
                Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
                var originalText = await ReadTextAsync(doc, token);
                // Original markup is necessary for correct discovery/rewrite on later cached runs.
                if (!File.Exists(sourcePath)) await File.WriteAllTextAsync(sourcePath, originalText, new UTF8Encoding(false), token);
                else if (await File.ReadAllTextAsync(sourcePath, token) != originalText) throw new IOException("Cache source lama tidak cocok.");
                await using var original = File.OpenRead(sourcePath);
                sourceHash = Convert.ToHexString(await SHA256.HashDataAsync(original, token));
            }
        }
        await using (var stream = File.OpenRead(doc.Path))
        {
            cache[Identity(doc.Uri)] = new CacheEntry(Path.GetRelativePath(root, doc.Path), doc.MediaType, doc.Charset, stream.Length, Convert.ToHexString(await SHA256.HashDataAsync(stream, token)), sourceHash);
        }
        var temp = cacheFile + "." + Guid.NewGuid().ToString("N") + ".part";
        try
        {
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(cache, new JsonSerializerOptions { WriteIndented = true }), token);
            LocalPaths.UnderRoot(root, Path.GetRelativePath(root, cacheFile));
            File.Move(temp, cacheFile, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private string SourcePath(Uri uri) => LocalPaths.UnderRoot(root, Path.Combine(".webscrapper-sources", Identity(uri) + ".txt"));

    private async Task<string> ReadTextAsync(Document doc, CancellationToken token)
    {
        if (cache.TryGetValue(Identity(doc.Uri), out var entry) && entry.SourceHash is not null)
            return await File.ReadAllTextAsync(SourcePath(doc.Uri), Encoding.UTF8, token);
        Encoding encoding;
        try { encoding = Encoding.GetEncoding(doc.Charset); } catch (ArgumentException) { encoding = Encoding.UTF8; }
        return await File.ReadAllTextAsync(doc.Path, encoding, token);
    }
    private static string Identity(Uri uri) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(LinkRewriter.Key(uri))));
    private static string GuessMedia(Uri uri) => Path.GetExtension(uri.AbsolutePath).ToLowerInvariant() switch { ".css" => "text/css", ".js" or ".mjs" or ".cjs" => "text/javascript", ".html" or ".htm" or "" => "text/html", _ => "application/octet-stream" };
    private void Report(Uri uri, string message, int? status, int pending, string? path = null, PageState? state = null, bool updatePage = true)
    {
        lock (reportGate)
        {
        var page = updatePage ? project.Update(uri, state, status, message, path) : null;
        progress?.Report(new CrawlProgress(uri.GetLeftPart(UriPartial.Path), message, status, saved, pending, Interlocked.Read(ref bytes), path, page));
        }
    }
}
