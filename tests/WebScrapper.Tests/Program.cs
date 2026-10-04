using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;
using WebScrapper.Core;

if (args.Length == 2 && args[0] == "--check-project")
{
    var checkedProject = await CrawlProject.LoadAsync(args[1]);
    Console.WriteLine($"Project loaded and synchronized: pages={checkedProject.Pages.Count}, successful={checkedProject.Pages.Count(p => p.Successful)}, memory={checkedProject.ContentCount}");
    return;
}
if (args.Length == 3 && args[0] == "--probe")
{
    var probeOptions = new CrawlOptions
    {
        StartUri = new Uri(args[1]), OutputDirectory = Path.GetFullPath(args[2]), MaxFiles = 1, MaxDepth = 0,
        MaxRetries = 0, UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36"
    };
    var probeProject = new CrawlProject(probeOptions);
    var probeResult = await new Crawler().RunAsync(probeOptions, new ProbeRecorder(), CancellationToken.None, probeProject);
    Console.WriteLine($"Probe completed: saved={probeResult.Saved}, failed={probeResult.Failed}, skipped={probeResult.Skipped}");
    return;
}
var suite = new Suite();
await suite.RunAsync();

internal sealed class Suite
{
    private int checks;
    private void Check(bool condition, string name)
    { if (!condition) throw new Exception("FAIL: " + name); checks++; Console.WriteLine("PASS: " + name); }

    public async Task RunAsync()
    {
        using (var denied = new HttpResponseMessage(HttpStatusCode.Forbidden) { RequestMessage = new HttpRequestMessage() })
        {
            var message = HttpFailure.Describe(denied);
            Check(message.Contains("403") && message.Contains("Cookie tidak dikirim") && message.Contains("Penyebab belum pasti"), "403 diagnosis does not assume incorrect cookies");
            denied.RequestMessage.Headers.TryAddWithoutValidation("Cookie", "session=fixture-secret");
            denied.RequestMessage.Headers.TryAddWithoutValidation("Authorization", "Bearer fixture-secret");
            message = HttpFailure.Describe(denied);
            Check(message.Contains("Cookie terkirim") && message.Contains("Authorization terkirim") && !message.Contains("fixture-secret"), "Access diagnosis shows header presence without secret values");
            denied.Headers.TryAddWithoutValidation("cf-mitigated", "challenge");
            Check(HttpFailure.Describe(denied).Contains("verifikasi browser"), "Explicit challenge header distinguished from ordinary 403");
            denied.StatusCode = HttpStatusCode.Unauthorized;
            Check(HttpFailure.Describe(denied).Contains("401: server meminta autentikasi"), "401 distinguished from forbidden access");
        }
        await using var cdn = new Fixture(_ => new Reply("external", "text/javascript"));
        var requestCounts = new ConcurrentDictionary<string, int>();
        var timestamps = new ConcurrentQueue<DateTimeOffset>();
        var authSeen = false; var sessionSeen = false; var customSeen = false;
        var recovered = false;
        await using var site = new Fixture(ctx =>
        {
            var path = ctx.Request.Url!.AbsolutePath;
            var n = requestCounts.AddOrUpdate(path, 1, (_, old) => old + 1);
            if (path == "/robots.txt") return new Reply("User-agent: *\nDisallow: /private\nAllow: /private/public", "text/plain");
            if (path == "/denied-root") return new Reply("", Status: 403);
            if (path == "/ad-redirect") return new Reply("", Status: 302, Headers: new Dictionary<string, string> { ["Location"] = "https://pagead.syndication.com/ad.js" });
            if (path == "/")
            {
                authSeen = ctx.Request.Headers["Authorization"] == "Bearer fixture";
                customSeen = ctx.Request.Headers["X-Fixture"] == "yes" && ctx.Request.UserAgent == "FixtureBot/1.0";
                return new Reply($"""
                    <!doctype html><html><head><base href="/base/"><link rel="stylesheet" href="../css/main.css" integrity="old"></head>
                    <body><a id="page" href="nested/page?lang=id#section">next</a><a href="/other/page?x=1">other</a>
                    <a href="/private/secret">deny</a><a id="missing" href="/not-found">missing</a><a href="/rate">rate</a>
                    <a href="/redirect">redirect</a><a href="/unauthorized">auth</a><a href="/private/public">allow</a>
                    <img src="../images/pic.png" srcset="../images/pic.png 1x, ../images/pic.png?size=2 2x">
                    <script src="{cdn.Origin}script.js"></script><div style="background:url('../images/pic.png')"></div>
                    </body></html>
                    """, Headers: new Dictionary<string, string> { ["Set-Cookie"] = "session=fixture; Path=/; HttpOnly" });
            }
            if (path == "/base/nested/page") { sessionSeen = ctx.Request.Cookies["session"]?.Value == "fixture" && ctx.Request.Cookies["initial"]?.Value == "seed"; return new Reply("<a id='back' href='/?home=1'>home</a><img src='../../images/pic.png'><h1 id='section'>hello</h1>"); }
            if (path == "/css/main.css") return new Reply("@import 'extra.css'; body{background:url('../images/pic.png')} .a{background:url(../images/pic.png?size=2)}", "text/css");
            if (path == "/css/extra.css") return new Reply(".b{background:url('../images/pic.png')}", "text/css");
            if (path == "/images/pic.png") return new Reply("fixture-image", "image/png");
            if (path == "/rate") { timestamps.Enqueue(DateTimeOffset.UtcNow); if (n == 1) return new Reply("wait", Status: 429, Headers: new Dictionary<string, string> { ["Retry-After"] = "3" }); }
            if (path == "/redirect") return new Reply("", Status: 302, Headers: new Dictionary<string, string> { ["Location"] = "/base/nested/page?lang=id" });
            if (path == "/unauthorized" && !recovered) return new Reply("", Status: 401);
            if (path == "/not-found" && !recovered) return new Reply("", Status: 404);
            return new Reply("<p>okay</p>");
        });
        var root = Path.Combine(Path.GetTempPath(), "webscrapper-tests-" + Guid.NewGuid().ToString("N"));
        var options = new CrawlOptions
        {
            StartUri = new Uri(site.Origin), OutputDirectory = root, RequestDelay = TimeSpan.FromMilliseconds(10),
            MaxFiles = 40, MaxDepth = 2, MaxRetries = 1, UserAgent = "FixtureBot/1.0", CookieHeader = "initial=seed",
            Headers = new Dictionary<string, string> { ["Authorization"] = "Bearer fixture", ["X-Fixture"] = "yes" },
            DownloadExternalAssets = true
        };
        var events = new Recorder();
        var project = new CrawlProject(options);
        var result = await new Crawler().RunAsync(options, events, CancellationToken.None, project);
        Check(result.Saved >= 10 && result.EntryFile is not null, "HTML, nested pages, CSS imports and assets downloaded");
        Check(result.Failed == 2, "401 and 404 reported without retry");
        Check(requestCounts["/unauthorized"] == 1, "401 never retried");
        Check(!requestCounts.ContainsKey("/private/secret") && requestCounts.ContainsKey("/private/public"), "robots longest allow/disallow rule");
        Check(requestCounts["/rate"] == 2 && (timestamps.Last() - timestamps.First()).TotalSeconds >= 2.9, "429 waits for Retry-After then retries");
        Check(authSeen && customSeen && sessionSeen, "User-Agent, custom header, initial cookie and Set-Cookie roundtrip");
        Check(cdn.Requests.All(r => r.Auth is null && r.Cookie is null && r.Custom is null), "No origin headers/cookies leaked to other origin");
        Check(events.Items.All(p => !p.Resource.Contains('?') && !p.State.Contains("fixture")), "Progress omits query and credentials");
        var parser = new HtmlParser();
        var html = parser.ParseDocument(await File.ReadAllTextAsync(result.EntryFile!));
        Check(html.QuerySelector("base") is null, "Base removed after resolving original URLs");
        var pageLink = html.QuerySelector("#page")!.GetAttribute("href")!;
        Check(pageLink.EndsWith("#section") && !Uri.TryCreate(pageLink, UriKind.Absolute, out _), "Page href relative and fragment preserved");
        Check(html.QuerySelector("#missing")!.GetAttribute("href") == site.Origin + "not-found", "Failed target remains an absolute online link");
        var validated = 0;
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Where(f => !f.Contains(".webscrapper") && Path.GetExtension(f) is ".html" or ".css"))
        {
            var text = await File.ReadAllTextAsync(file);
            var values = new List<string>();
            if (file.EndsWith(".html"))
            {
                var doc = parser.ParseDocument(text);
                foreach (var el in doc.All)
                {
                    foreach (var attr in new[] { "href", "src", "poster" }) if (el.GetAttribute(attr) is { } v) values.Add(v);
                    if (el.GetAttribute("srcset") is { } ss) values.AddRange(ss.Split(',').Select(p => p.Trim().Split(' ')[0]));
                    if (el.GetAttribute("style") is { } style) values.AddRange(CssLinks(style));
                }
            }
            else values.AddRange(CssLinks(text));
            foreach (var value in values.Where(v => !v.StartsWith('#') && !Uri.TryCreate(v, UriKind.Absolute, out _)))
            {
                var path = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, Uri.UnescapeDataString(value.Split('#')[0])));
                Check(File.Exists(path), "Relative link resolves: " + Path.GetFileName(file) + " -> " + Path.GetFileName(path)); validated++;
            }
        }
        Check(validated >= 12, "All saved HTML/CSS references checked on disk");
        var countBefore = requestCounts.Where(p => p.Key != "/robots.txt" && p.Key != "/unauthorized" && p.Key != "/not-found" && p.Key != "/redirect").Sum(p => p.Value);
        var firstHash = SHA256File(result.EntryFile!);
        var repeated = await new Crawler().RunAsync(options with { ResumeLastState = false }, new Recorder(), CancellationToken.None, project);
        var countAfter = requestCounts.Where(p => p.Key != "/robots.txt" && p.Key != "/unauthorized" && p.Key != "/not-found" && p.Key != "/redirect").Sum(p => p.Value);
        Check(countBefore == countAfter, "Repeated crawl uses existing validated files without GET");
        Check(firstHash == SHA256File(result.EntryFile!) && repeated.Saved == result.Saved, "Cached crawl retains correct stable rewritten links");
        var projectPath = Path.Combine(root, "fixture.wsproject");
        await project.SaveAsync(projectPath);
        var projectJson = await File.ReadAllTextAsync(projectPath);
        Check(!projectJson.Contains("Bearer fixture") && !projectJson.Contains("initial=seed") && !projectJson.Contains("X-Fixture"), "Saved project excludes session cookies and custom headers");
        var loaded = await CrawlProject.LoadAsync(projectPath);
        Check(loaded.Pages.Count == project.Pages.Count && loaded.Pages.Count(p => p.State == PageState.Failed) == 2 && loaded.ContentCount > 0, "Save/load restores all URL states and HTML/CSS in memory");
        Check(loaded.Pages.Where(p => p.Successful).All(p => p.RelativePath is not null && File.Exists(Path.Combine(root, p.RelativePath))), "Successful project states match real disk files");
        var deletedAsset = loaded.Pages.First(p => p.Url.Contains("/images/pic.png?")).RelativePath!;
        File.Delete(Path.Combine(root, deletedAsset));
        await loaded.SynchronizeFilesAsync();
        Check(loaded.Pages.Any(p => p.RelativePath == deletedAsset && p.State == PageState.Failed), "Missing downloaded file is marked failed for retry");
        var rootRequests = requestCounts["/"];
        recovered = true;
        var retried = await new Crawler().RunAsync(options, null, CancellationToken.None, loaded, retryIncomplete: true);
        Check(retried.Failed == 0 && loaded.Pages.All(p => p.Successful || p.State == PageState.Skipped), "Retry incomplete recovers failed URLs and deleted files");
        Check(requestCounts["/"] == rootRequests, "Retry does not GET previously successful entry pages");
        var recoveredHtml = parser.ParseDocument(await File.ReadAllTextAsync(result.EntryFile!));
        var recoveredLink = recoveredHtml.QuerySelector("#missing")!.GetAttribute("href")!;
        Check(!Uri.TryCreate(recoveredLink, UriKind.Absolute, out _) && File.Exists(Path.Combine(Path.GetDirectoryName(result.EntryFile!)!, recoveredLink)), "Old page links rewritten after failed target is recovered");
        await loaded.SaveAsync(projectPath);
        var loadedAgain = await CrawlProject.LoadAsync(projectPath);
        Check(loadedAgain.Pages.All(p => p.Successful || p.State == PageState.Skipped), "Recovered status survives another save/load");
        await File.WriteAllTextAsync(result.EntryFile!, "user modified content");
        var protectedResult = await new Crawler().RunAsync(options, null, CancellationToken.None);
        Check(await File.ReadAllTextAsync(result.EntryFile!) == "user modified content" && protectedResult.Skipped == 1, "User-modified existing file is never overwritten");

        var query1 = LocalPaths.ForUri(new Uri(site.Origin + "search?q=1"), "text/html");
        var query2 = LocalPaths.ForUri(new Uri(site.Origin + "search?q=2"), "text/html");
        Check(query1 != query2, "Query variants have distinct filenames");
        Check(LocalPaths.ForUri(new Uri(site.Origin + "Case"), "text/html") != LocalPaths.ForUri(new Uri(site.Origin + "case"), "text/html"), "Case-sensitive URLs do not collide on Windows");
        Check(LocalPaths.ForUri(new Uri(site.Origin + "a/"), "text/html") != LocalPaths.ForUri(new Uri(site.Origin + "a/index.html"), "text/html"), "Directory and explicit index do not collide");
        Check(LocalPaths.ForUri(new Uri(site.Origin + "index.php"), "text/html").EndsWith(".html"), "Dynamic HTML endpoint saves with browser-openable HTML extension");
        Check(LocalPaths.UnderRoot(root, LocalPaths.ForUri(new Uri(site.Origin + "CON/%2F/a?x=y"), "text/html")).StartsWith(root), "Reserved names and encoded slash stay inside output");
        bool traversalBlocked = false; try { LocalPaths.UnderRoot(root, "../escape"); } catch (IOException) { traversalBlocked = true; }
        Check(traversalBlocked, "Path traversal blocked");

        await using var limited = new Fixture(ctx => ctx.Request.Url!.AbsolutePath == "/robots.txt" ? new Reply("", Status: 404) : new Reply("wait", Status: 429, Headers: new Dictionary<string, string> { ["Retry-After"] = "120" }));
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var watch = Stopwatch.StartNew(); bool canceled = false;
        try { await new Crawler().RunAsync(options with { StartUri = new Uri(limited.Origin), OutputDirectory = root + "-cancel" }, null, cancel.Token); }
        catch (OperationCanceledException) { canceled = true; }
        Check(canceled && watch.Elapsed.TotalSeconds < 2, "Cancellation interrupts long Retry-After promptly");
        Check(!Directory.EnumerateFiles(root, "*.part", SearchOption.AllDirectories).Any(), "No partial files left after completed crawl");
        var unscripted = new CrawlProject(options);
        await unscripted.SaveAsync(Path.Combine(root, "new.wsproject"));
        Check((await CrawlProject.LoadAsync(Path.Combine(root, "new.wsproject"))).Pages.Count == 0, "New project settings can be saved before first crawl");
        var deniedOptions = options with { StartUri = new Uri(site.Origin + "denied-root"), OutputDirectory = root + "-denied", RespectRobots = false };
        var deniedProject = new CrawlProject(deniedOptions);
        await new Crawler().RunAsync(deniedOptions, null, CancellationToken.None, deniedProject);
        Check(deniedProject.Pages.Single().Message.StartsWith("403:") && deniedProject.Pages.Single().State == PageState.Failed, "Final rewrite progress preserves original root access failure diagnosis");
        var skipOptions = options with { StartUri = new Uri("https://PAGEAD.SYNDICATION.COM./ad.js"), OutputDirectory = root + "-skip" };
        var skippedProject = new CrawlProject(skipOptions);
        var skipResult = await new Crawler().RunAsync(skipOptions, null, CancellationToken.None, skippedProject);
        Check(skipResult.Skipped == 1 && skipResult.Failed == 0 && skippedProject.Pages.Single().State == PageState.Skipped, "Excluded host is skipped before robots/request, case and trailing dot normalized");
        skipResult = await new Crawler().RunAsync(skipOptions, null, CancellationToken.None, skippedProject, retryIncomplete: true);
        Check(skipResult.Skipped == 1 && skipResult.Saved == 0, "Retry incomplete still skips excluded host");
        var redirectOptions = options with { StartUri = new Uri(site.Origin + "ad-redirect"), OutputDirectory = root + "-skip-redirect", RespectRobots = false };
        var redirectProject = new CrawlProject(redirectOptions);
        var redirectResult = await new Crawler().RunAsync(redirectOptions, null, CancellationToken.None, redirectProject);
        Check(redirectResult.Skipped == 1 && redirectResult.Failed == 0 && redirectProject.Pages.Single().Message.Contains("pagead.syndication.com"), "Redirect to excluded host is marked skipped before destination request");
        var challengeAgents = new ConcurrentDictionary<string, List<string>>();
        var adScriptOptions = options with { StartUri = new Uri("https://PAGEAD2.GOOGLESYNDICATION.COM./pagead/js/adsbygoogle.js?client=fixture"), OutputDirectory = root + "-ad-script" };
        var adScriptProject = new CrawlProject(adScriptOptions);
        var adScriptResult = await new Crawler().RunAsync(adScriptOptions, null, CancellationToken.None, adScriptProject);
        Check(adScriptResult.Skipped == 1 && adScriptResult.Failed == 0 && adScriptProject.Pages.Single().Message.Contains("script iklan"), "Adsbygoogle script with query is skipped before robots/request");
        await adScriptProject.SaveAsync(Path.Combine(root, "ad-script.wsproject"));
        var adScriptLoaded = await CrawlProject.LoadAsync(Path.Combine(root, "ad-script.wsproject"));
        adScriptResult = await new Crawler().RunAsync(adScriptOptions, null, CancellationToken.None, adScriptLoaded, retryIncomplete: true);
        Check(adScriptResult.Skipped == 1 && adScriptResult.Failed == 0, "Adsbygoogle exclusion still applies after project load and retry");
        await using var adScriptRedirect = new Fixture(_ => new Reply("", Status: 302, Headers: new Dictionary<string, string> { ["Location"] = "https://pagead2.googlesyndication.com/pagead/js/adsbygoogle.js?other=fixture" }));
        var adRedirectResult = await new Crawler().RunAsync(options with { StartUri = new Uri(adScriptRedirect.Origin), OutputDirectory = root + "-ad-script-redirect", RespectRobots = false }, null, CancellationToken.None);
        Check(adRedirectResult.Skipped == 1 && adRedirectResult.Failed == 0, "Redirect to adsbygoogle script with another query is skipped before destination request");
        var challengeTimes = new List<DateTimeOffset>();
        await using var challengeSite = new Fixture(ctx =>
        {
            var path = ctx.Request.Url!.AbsolutePath;
            var agents = challengeAgents.GetOrAdd(path, _ => []);
            agents.Add(ctx.Request.UserAgent ?? "");
            if (path == "/plain") return new Reply("", Status: 403);
            if (path == "/switch")
            {
                challengeTimes.Add(DateTimeOffset.UtcNow);
                if (agents.Count > 1) return new Reply("<a href='/persistent'>another challenge</a>");
            }
            return new Reply("verify", Status: 403, Headers: new Dictionary<string, string> { ["cf-mitigated"] = "challenge", ["Retry-After"] = path == "/switch" ? "3" : "0" });
        });
        var challengeOptions = options with { StartUri = new Uri(challengeSite.Origin + "switch"), OutputDirectory = root + "-agent", UserAgent = BrowserAgents.Chrome, RespectRobots = false, MaxRetries = 0 };
        var challengeProgress = new Recorder();
        var challengeResult = await new Crawler().RunAsync(challengeOptions, challengeProgress, CancellationToken.None);
        Check(challengeAgents["/switch"].SequenceEqual(new[] { BrowserAgents.Chrome, BrowserAgents.Firefox }) && challengeResult.Saved == 1, "Explicit browser challenge switches agent and retries once even with no transient retries");
        Check((challengeTimes[1] - challengeTimes[0]).TotalSeconds >= 2.9, "Agent fallback respects Retry-After cooldown");
        Check(challengeAgents["/persistent"].Count == 1 && challengeResult.Failed == 1, "Agent can switch only once across the entire run");
        Check(challengeProgress.Items.Any(p => p.State.Contains("ganti User-Agent ke Firefox")), "Agent switch is reported in progress");
        await new Crawler().RunAsync(challengeOptions with { StartUri = new Uri(challengeSite.Origin + "off"), OutputDirectory = root + "-agent-off", AutoSwitchUserAgent = false }, null, CancellationToken.None);
        Check(challengeAgents["/off"].Count == 1, "Disabled fallback performs no extra request");
        await new Crawler().RunAsync(challengeOptions with { StartUri = new Uri(challengeSite.Origin + "plain"), OutputDirectory = root + "-agent-plain" }, null, CancellationToken.None);
        Check(challengeAgents["/plain"].Count == 1, "Ordinary 403 does not trigger agent fallback");
        var agentProjectPath = Path.Combine(root, "agent-off.wsproject");
        await new CrawlProject(challengeOptions with { AutoSwitchUserAgent = false }).SaveAsync(agentProjectPath);
        Check(!(await CrawlProject.LoadAsync(agentProjectPath)).Settings.ToOptions().AutoSwitchUserAgent, "Agent fallback preference persists through project save/load");
        int activeDownloads = 0, peakDownloads = 0;
        var fastRequestTimes = new ConcurrentQueue<(string Path, DateTimeOffset At)>();
        await using var fastSite = new Fixture(async ctx =>
        {
            var path = ctx.Request.Url!.AbsolutePath;
            fastRequestTimes.Enqueue((path, DateTimeOffset.UtcNow));
            if (path == "/") return new Reply(string.Concat(Enumerable.Range(0, 8).Select(i => $"<img src='/asset/{i}.bin'>")) + "<a href='/alias-a'>A</a><a href='/alias-b'>B</a>");
            if (path == "/alias-a" || path == "/alias-b") return new Reply("", Status: 302, Headers: new Dictionary<string, string> { ["Location"] = "/asset/0.bin" });
            if (path == "/cooldown") return new Reply("<img src='/rate-limit'><img src='/after-limit'><img src='/last'>");
            if (path == "/rate-limit") return new Reply("wait", Status: 429, Headers: new Dictionary<string, string> { ["Retry-After"] = "1" });
            var active = Interlocked.Increment(ref activeDownloads);
            int observed;
            do { observed = Volatile.Read(ref peakDownloads); if (observed >= active) break; } while (Interlocked.CompareExchange(ref peakDownloads, active, observed) != observed);
            try { await Task.Delay(180); return new Reply(new string('x', 1024), "application/octet-stream"); }
            finally { Interlocked.Decrement(ref activeDownloads); }
        });
        var fastOptions = options with
        {
            StartUri = new Uri(fastSite.Origin), OutputDirectory = root + "-fast", FastMode = true, ParallelRequests = 4,
            RespectRobots = false, RequestDelay = TimeSpan.FromMilliseconds(10), JitterPercent = 0, MaxRetries = 0
        };
        var fastProject = new CrawlProject(fastOptions);
        var fastResult = await new Crawler().RunAsync(fastOptions, null, CancellationToken.None, fastProject);
        Check(peakDownloads > 1 && peakDownloads <= 4, "Fast mode overlaps HTTP requests within configured worker limit");
        Check(fastResult.Failed == 0 && fastResult.Saved == 11, "Fast batch saves all URLs including redirect aliases without file collisions");
        var fastProjectPath = Path.Combine(root, "fast.wsproject");
        await fastProject.SaveAsync(fastProjectPath);
        var requestCountBeforeLoad = fastRequestTimes.Count;
        var fastLoaded = await CrawlProject.LoadAsync(fastProjectPath);
        Check(fastRequestTimes.Count == requestCountBeforeLoad && fastLoaded.Pages.Count == fastProject.Pages.Count && fastLoaded.Pages.All(p => p.Successful), "Project restores full URL list and status without crawling or HTTP requests");
        Check(fastLoaded.Settings.FastMode && fastLoaded.Settings.ParallelRequests == 4, "Fast mode and worker count persist in project");
        var cachedFast = await new Crawler().RunAsync(fastOptions, null, CancellationToken.None, fastLoaded, retryIncomplete: true);
        Check(fastRequestTimes.Count == requestCountBeforeLoad && cachedFast.Failed == 0, "Resume uses loaded URL list without recrawling successful URLs");
        var limitedFast = await new Crawler().RunAsync(fastOptions with { OutputDirectory = root + "-fast-budget", MaxFileBytes = 2048, MaxTotalBytes = 2048 }, null, CancellationToken.None);
        Check(limitedFast.Bytes <= 2048 && limitedFast.Failed > 0 && !Directory.EnumerateFiles(root + "-fast-budget", "*.part", SearchOption.AllDirectories).Any(), "Parallel byte budget is atomic and partial downloads are cleaned");
        var cooldownOptions = fastOptions with { StartUri = new Uri(fastSite.Origin + "cooldown"), OutputDirectory = root + "-fast-cooldown", ParallelRequests = 2, RequestDelay = TimeSpan.FromMilliseconds(100) };
        await new Crawler().RunAsync(cooldownOptions, null, CancellationToken.None);
        var rateTime = fastRequestTimes.First(p => p.Path == "/rate-limit").At;
        var afterTime = fastRequestTimes.First(p => p.Path == "/after-limit").At;
        Check((afterTime - rateTime).TotalSeconds >= 0.9, "429 Retry-After pauses other Fast workers sharing the scheduler");
        using (var fastCancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(120)))
        {
            bool fastCanceled = false;
            var canceledProject = new CrawlProject(fastOptions with { OutputDirectory = root + "-fast-cancel" });
            try { await new Crawler().RunAsync(canceledProject.Settings.ToOptions(), null, fastCancel.Token, canceledProject); }
            catch (OperationCanceledException) { fastCanceled = true; }
            await canceledProject.SaveAsync(Path.Combine(root, "fast-canceled.wsproject"));
            var canceledLoaded = await CrawlProject.LoadAsync(Path.Combine(root, "fast-canceled.wsproject"));
            Check(fastCanceled && canceledLoaded.Pages.Any(p => p.State == PageState.Canceled) && !Directory.EnumerateFiles(root + "-fast-cancel", "*.part", SearchOption.AllDirectories).Any(), "Fast cancellation preserves pending URL list for offline load and removes partials");
        }
        var excludeRequests = new ConcurrentQueue<string>();
        await using var excludeSite = new Fixture(ctx =>
        {
            var path = ctx.Request.Url!.AbsolutePath;
            excludeRequests.Enqueue(path);
            if (path == "/") return new Reply("<a href='/v8'>8</a><a href='/v8/child'>child</a><a href='/v9?q=1'>9</a><a href='/V9/upper'>upper</a><a href='/%76%38/encoded'>encoded</a><a href='/v80'>80</a><a href='/redirect'>redirect</a>");
            if (path == "/redirect") return new Reply("", Status: 302, Headers: new Dictionary<string, string> { ["Location"] = "/v9/redirected" });
            return new Reply("<p>allowed</p>");
        });
        var excludeOptions = fastOptions with { StartUri = new Uri(excludeSite.Origin), OutputDirectory = root + "-exclude", ExcludedPaths = ["/v8", "/v9", "/legacy"] };
        var excludeProject = new CrawlProject(excludeOptions);
        var excludeResult = await new Crawler().RunAsync(excludeOptions, null, CancellationToken.None, excludeProject);
        Check(excludeResult.Skipped == 6 && excludeResult.Failed == 0 && excludeRequests.Contains("/v80"), "Exclude matches exact path, descendants, query, case and encoded path without excluding v80");
        Check(excludeRequests.All(p => p is "/" or "/v80" or "/redirect"), "Excluded links and redirect targets receive no HTTP requests in Fast mode");
        await new Crawler().RunAsync(excludeOptions, null, CancellationToken.None, excludeProject, retryIncomplete: true);
        Check(excludeRequests.All(p => p is "/" or "/v80" or "/redirect"), "Retry incomplete continues to respect path exclusions");
        var excludeProjectPath = Path.Combine(root, "excluded.wsproject");
        await excludeProject.SaveAsync(excludeProjectPath);
        var beforeExcludeLoad = excludeRequests.Count;
        var excludeLoaded = await CrawlProject.LoadAsync(excludeProjectPath);
        Check(excludeLoaded.Settings.ToOptions().ExcludedPaths.SequenceEqual(excludeOptions.ExcludedPaths) && excludeLoaded.Pages.Count == excludeProject.Pages.Count && excludeRequests.Count == beforeExcludeLoad, "Project restores exclude settings and full URL list without network access");
        await new Crawler().RunAsync(excludeOptions with { StartUri = new Uri(excludeSite.Origin + "v8"), RespectRobots = true, OutputDirectory = root + "-excluded-start" }, null, CancellationToken.None);
        Check(excludeRequests.Count == beforeExcludeLoad, "Excluded start URL is skipped before robots and HTTP requests");
        var emptyExcludeProject = new CrawlProject(excludeOptions with { ExcludedPaths = [] });
        await emptyExcludeProject.SaveAsync(excludeProjectPath);
        Check((await CrawlProject.LoadAsync(excludeProjectPath)).Settings.ToOptions().ExcludedPaths.Count == 0, "Explicit empty exclude list persists without restoring defaults");
        bool resumeRecovery = false;
        var resumeRequests = new ConcurrentQueue<string>();
        await using var resumeSite = new Fixture(ctx =>
        {
            var path = ctx.Request.Url!.AbsolutePath; resumeRequests.Enqueue(path);
            if (path == "/") return new Reply("<a href='/early'>early</a><a href='/last'>last</a><a href='/tail'>tail</a>");
            return new Reply("<p>page</p>", Status: !resumeRecovery && path != "/last" ? 403 : 200);
        });
        var resumeOptions = fastOptions with { StartUri = new Uri(resumeSite.Origin), OutputDirectory = root + "-resume", FastMode = false };
        var resumeProject = new CrawlProject(resumeOptions);
        await new Crawler().RunAsync(resumeOptions, null, CancellationToken.None, resumeProject);
        Check(resumeOptions.ResumeLastState && resumeProject.LastDownloadedUrl == resumeSite.Origin + "last", "Resume defaults on and checkpoint records last saved URL, not last failed URL");
        var resumeFile = Path.Combine(root, "resume.wsproject");
        await resumeProject.SaveAsync(resumeFile);
        var resumed = await CrawlProject.LoadAsync(resumeFile);
        Check(resumed.Settings.ToOptions().ResumeLastState && resumed.LastDownloadedUrl == resumeProject.LastDownloadedUrl, "Resume option and checkpoint survive project save/load and disk synchronization");
        resumeRecovery = true; resumeRequests.Clear();
        await new Crawler().RunAsync(resumed.Settings.ToOptions(), null, CancellationToken.None, resumed);
        Check(resumeRequests.SequenceEqual(new[] { "/tail", "/early" }) && resumed.Pages.All(p => p.Successful), "Start resumes after checkpoint then recovers earlier failures without recrawling successful pages");
        resumeRequests.Clear();
        await new Crawler().RunAsync(resumed.Settings.ToOptions(), null, CancellationToken.None, resumed);
        Check(resumeRequests.IsEmpty, "Starting completed project in resume mode performs no HTTP requests");
        resumed.Settings = ProjectSettings.From(resumeOptions with { ResumeLastState = false });
        await resumed.SaveAsync(resumeFile);
        var resumeDisabled = await CrawlProject.LoadAsync(resumeFile);
        Check(!resumeDisabled.Settings.ToOptions().ResumeLastState, "Disabled resume preference persists");
        var fromRoot = await new Crawler().RunAsync(resumeDisabled.Settings.ToOptions(), null, CancellationToken.None, resumeDisabled);
        Check(fromRoot.Saved == 4 && resumeRequests.IsEmpty, "Disabling resume discovers from root using valid local cache");
        string? foreignBrowserCookie = null, scopedBrowserCookie = null, outsidePathCookie = null;
        await using var cookieForeign = new Fixture(ctx => { foreignBrowserCookie = ctx.Request.Headers["Cookie"]; return new Reply("asset", "text/plain"); });
        await using var cookieOrigin = new Fixture(ctx =>
        {
            if (ctx.Request.Url!.AbsolutePath == "/auth/page")
            {
                scopedBrowserCookie = ctx.Request.Headers["Cookie"];
                return new Reply($"<img src='{cookieForeign.Origin}asset'><img src='/public'>");
            }
            outsidePathCookie = ctx.Request.Headers["Cookie"]; return new Reply("asset", "text/plain");
        });
        var importedOptions = fastOptions with { StartUri = new Uri(cookieOrigin.Origin + "auth/page"), OutputDirectory = root + "-browser-scope", DownloadExternalAssets = true,
            BrowserCookies = [new Cookie("imported", "fixture-secret", "/auth", "127.0.0.1") { HttpOnly = true }, new Cookie("secureOnly", "fixture-secure", "/", "127.0.0.1") { Secure = true }] };
        await new Crawler().RunAsync(importedOptions, null, CancellationToken.None);
        Check(scopedBrowserCookie?.Contains("imported=fixture-secret") == true && !scopedBrowserCookie.Contains("secureOnly"), "Imported HttpOnly cookies sent with path matching; Secure cookie withheld on HTTP");
        Check(string.IsNullOrEmpty(foreignBrowserCookie) && string.IsNullOrEmpty(outsidePathCookie), "Imported browser cookies withheld from other origin and outside cookie path");
        bool assetRecovery = false;
        var assetRequests = new ConcurrentQueue<string>();
        await using var assetSite = new Fixture(ctx =>
        {
            var path = ctx.Request.Url!.AbsolutePath; assetRequests.Enqueue(path);
            if (path == "/nested/page") return new Reply("""
                <base href='/assets/'><script id='dynamic' src='bundle.php?v=1'></script>
                <script id='alias' src='/alias'></script><script id='late' src='late.js'></script>
                <link rel='STYLESHEET' href='style.css'><link rel='PRELOAD' as='image' imagesrcset='photo.png 1x, photo2.png 2x'>
                <svg><image id='svgImage' href='photo.png'/><use id='svgUse' xlink:href='icons.svg#mark'/></svg>
                <video poster='photo.png'><source src='movie.mp4'></video><audio><source src='sound.ogg'></audio>
                <picture><source srcset='photo.png 1x, photo2.png 2x'></picture>
                """);
            if (path == "/alias") return new Reply("", Status: 302, Headers: new Dictionary<string, string> { ["Location"] = "/assets/runtime.js" });
            if (path == "/assets/bundle.php") return new Reply("window.fixture = 'unchanged';", "text/javascript");
            if (path == "/assets/runtime.js") return new Reply("window.runtime = true;", "text/javascript");
            if (path == "/assets/late.js" && !assetRecovery) return new Reply("missing", Status: 404);
            if (path == "/assets/style.css") return new Reply("body { background:url(photo.png); }", "text/css");
            return new Reply("fixture", "application/octet-stream");
        });
        var assetOptions = fastOptions with { StartUri = new Uri(assetSite.Origin + "nested/page"), OutputDirectory = root + "-source-assets" };
        var assetProject = new CrawlProject(assetOptions);
        var assetResult = await new Crawler().RunAsync(assetOptions, null, CancellationToken.None, assetProject);
        var assetHtml = new HtmlParser().ParseDocument(await File.ReadAllTextAsync(assetResult.EntryFile!));
        foreach (var selector in new[] { "script#dynamic", "script#alias", "link[href]", "source[src]", "video[poster]", "#svgImage", "#svgUse" })
            foreach (var element in assetHtml.QuerySelectorAll(selector))
            {
                var value = new[] { "src", "href", "xlink:href", "poster" }.Select(element.GetAttribute).First(v => v is not null)!;
                var local = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(assetResult.EntryFile!)!, Uri.UnescapeDataString(value.Split('#')[0])));
                Check(!value.StartsWith("http") && File.Exists(local), "Source asset points to valid relative file: " + element.LocalName);
            }
        Check(assetRequests.Contains("/assets/photo2.png") && assetHtml.QuerySelector("link[imagesrcset]")!.GetAttribute("imagesrcset")!.Contains("__u_"), "Preload imagesrcset assets discovered and rewritten");
        var dynamicPath = assetProject.Pages.Single(p => p.Url.Contains("bundle.php")).RelativePath!;
        Check(dynamicPath.EndsWith(".js") && await File.ReadAllTextAsync(Path.Combine(assetOptions.OutputDirectory, dynamicPath)) == "window.fixture = 'unchanged';", "Dynamic script receives JS extension and source bytes remain intact");
        var sourceProjectPath = Path.Combine(root, "source-assets.wsproject"); await assetProject.SaveAsync(sourceProjectPath);
        var assetLoaded = await CrawlProject.LoadAsync(sourceProjectPath); assetRequests.Clear(); assetRecovery = true;
        await new Crawler().RunAsync(assetOptions, null, CancellationToken.None, assetLoaded, retryIncomplete: true);
        var recoveredAssetsHtml = new HtmlParser().ParseDocument(await File.ReadAllTextAsync(assetResult.EntryFile!));
        Check(assetRequests.SequenceEqual(new[] { "/assets/late.js" }) && !recoveredAssetsHtml.QuerySelector("#late")!.GetAttribute("src")!.StartsWith("http"), "Retry rewrites failed script to local path without downloading successful source assets again");
        // Simulate an older project's truncated URL list: only root and redirect alias were recorded.
        var aliasOptions = assetOptions with { OutputDirectory = root + "-alias-resume", MaxFiles = 2 };
        await using var aliasSite = new Fixture(ctx => ctx.Request.Url!.AbsolutePath == "/" ? new Reply("<script src='/redirect'></script><script src='/final.js'></script>") : ctx.Request.Url.AbsolutePath == "/redirect" ? new Reply("", Status: 302, Headers: new Dictionary<string, string> { ["Location"] = "/final.js" }) : new Reply("window.alias=true;", "text/javascript"));
        aliasOptions = aliasOptions with { StartUri = new Uri(aliasSite.Origin) };
        var aliasProject = new CrawlProject(aliasOptions); var aliasResult = await new Crawler().RunAsync(aliasOptions, null, CancellationToken.None, aliasProject);
        await aliasProject.SaveAsync(sourceProjectPath); var aliasLoaded = await CrawlProject.LoadAsync(sourceProjectPath);
        await new Crawler().RunAsync(aliasOptions, null, CancellationToken.None, aliasLoaded);
        var aliasHtml = new HtmlParser().ParseDocument(await File.ReadAllTextAsync(aliasResult.EntryFile!));
        Check(aliasHtml.QuerySelectorAll("script").All(s => !s.GetAttribute("src")!.StartsWith("http")), "Redirect destination remains locally mapped after load/resume");
        assetRequests.Clear();
        var checkpointBeforeFix = assetLoaded.LastDownloadedUrl;
        var missingAssetPath = Path.Combine(assetOptions.OutputDirectory, assetLoaded.Pages.Single(p => p.Url.EndsWith("/assets/photo.png")).RelativePath!);
        File.Delete(missingAssetPath);
        var fixResult = await new Crawler().FixUrlsAsync(assetLoaded, null, CancellationToken.None);
        var fixedHtml = new HtmlParser().ParseDocument(await File.ReadAllTextAsync(assetResult.EntryFile!));
        Check(fixResult.Saved == 2 && fixResult.Bytes == 0 && assetRequests.IsEmpty, "Fix URL rewrites HTML/CSS locally without any HTTP requests");
        Check(fixedHtml.QuerySelector("video")!.GetAttribute("poster")!.StartsWith("http") && !fixedHtml.QuerySelector("#dynamic")!.GetAttribute("src")!.StartsWith("http"), "Fix URL points only valid assets locally and missing assets to original online URL");
        Check(assetLoaded.LastDownloadedUrl == checkpointBeforeFix && assetLoaded.Pages.Single(p => p.Url.EndsWith("/assets/photo.png")).State == PageState.Failed, "Fix URL reconciles missing files without changing download checkpoint");
        await assetLoaded.SaveAsync(sourceProjectPath);
        Check((await CrawlProject.LoadAsync(sourceProjectPath)).Pages.Single(p => p.Url.EndsWith("/assets/photo.png")).State == PageState.Failed, "Fix URL status and rewritten cache remain valid after save/load");
        await File.WriteAllTextAsync(assetResult.EntryFile!, "user-modified-content");
        await new Crawler().FixUrlsAsync(assetLoaded, null, CancellationToken.None);
        Check(await File.ReadAllTextAsync(assetResult.EntryFile!) == "user-modified-content" && assetRequests.IsEmpty, "Fix URL preserves user-modified files and remains offline");
        Console.WriteLine($"SUCCESS: {checks} checks. Fixture output: {root}");
    }

    private static IEnumerable<string> CssLinks(string text) => Regex.Matches(text, "(?:url\\(|@import\\s+)[\"']([^\"']+)[\"']").Select(m => m.Groups[1].Value);
    private static string SHA256File(string path) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));
}

internal sealed class Recorder : IProgress<CrawlProgress>
{
    public List<CrawlProgress> Items { get; } = [];
    public void Report(CrawlProgress value) => Items.Add(value);
}
internal sealed class ProbeRecorder : IProgress<CrawlProgress>
{
    public void Report(CrawlProgress value) => Console.WriteLine($"{value.StatusCode?.ToString() ?? "-"} {value.Resource} | {value.State}");
}
internal sealed record Reply(string Body, string Type = "text/html", int Status = 200, Dictionary<string, string>? Headers = null);
internal sealed record RequestInfo(string? Auth, string? Cookie, string? Custom);
internal sealed class Fixture : IAsyncDisposable
{
    private readonly HttpListener listener = new();
    private readonly Task loop;
    private readonly ConcurrentBag<Task> responses = [];
    public string Origin { get; }
    public ConcurrentQueue<RequestInfo> Requests { get; } = new();
    public Fixture(Func<HttpListenerContext, Reply> respond) : this(ctx => Task.FromResult(respond(ctx))) { }
    public Fixture(Func<HttpListenerContext, Task<Reply>> respond)
    {
        using var reserve = new TcpListener(IPAddress.Loopback, 0); reserve.Start();
        var port = ((IPEndPoint)reserve.LocalEndpoint).Port; reserve.Stop();
        Origin = $"http://127.0.0.1:{port}/"; listener.Prefixes.Add(Origin); listener.Start();
        loop = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await listener.GetContextAsync(); } catch (HttpListenerException) { break; } catch (ObjectDisposedException) { break; }
                responses.Add(HandleAsync(ctx));
            }
        });
        async Task HandleAsync(HttpListenerContext ctx)
        {
            try
            {
                Requests.Enqueue(new RequestInfo(ctx.Request.Headers["Authorization"], ctx.Request.Headers["Cookie"], ctx.Request.Headers["X-Fixture"]));
                var reply = await respond(ctx); ctx.Response.StatusCode = reply.Status; ctx.Response.ContentType = reply.Type + "; charset=utf-8";
                if (reply.Headers is not null) foreach (var h in reply.Headers) ctx.Response.Headers[h.Key] = h.Value;
                var body = Encoding.UTF8.GetBytes(reply.Body); ctx.Response.ContentLength64 = body.Length;
                await ctx.Response.OutputStream.WriteAsync(body); ctx.Response.Close();
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or IOException) { }
        }
    }
    public async ValueTask DisposeAsync() { listener.Close(); await loop; await Task.WhenAll(responses); }
}
