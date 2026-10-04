using System.Net;
using System.Net.Sockets;
using System.Text;
using WebScrapper.Core;
using WebScrapper.WinForms;

internal static class BrowserCookieSmoke
{
    public static void Run()
    {
        var portFinder = new TcpListener(IPAddress.Loopback, 0); portFinder.Start();
        var port = ((IPEndPoint)portFinder.LocalEndpoint).Port; portFinder.Stop();
        var origin = new Uri($"http://127.0.0.1:{port}/auth/page");
        using var server = new HttpListener(); server.Prefixes.Add($"http://127.0.0.1:{port}/"); server.Start();
        var servedWithCookie = false;
        var serving = Task.Run(async () =>
        {
            try
            {
                while (server.IsListening)
                {
                    var ctx = await server.GetContextAsync();
                    if (ctx.Request.Headers["Cookie"]?.Contains("fixtureSession=local-test-only") == true) servedWithCookie = true;
                    ctx.Response.Headers.Add("Set-Cookie", "fixtureSession=local-test-only; Path=/auth; HttpOnly; SameSite=Lax");
                    var bytes = Encoding.UTF8.GetBytes("<html><body>Local cookie fixture</body></html>");
                    ctx.Response.ContentType = "text/html"; ctx.Response.ContentLength64 = bytes.Length;
                    await ctx.Response.OutputStream.WriteAsync(bytes); ctx.Response.Close();
                }
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException) { }
        });
        using var dialog = new BrowserCookieForm(origin) { StartPosition = FormStartPosition.Manual, Location = new Point(-10000, -10000) };
        using var timer = new System.Windows.Forms.Timer { Interval = 200 };
        var deadline = DateTime.UtcNow.AddSeconds(35); Exception? failure = null; bool completed = false;
        timer.Tick += async (_, _) =>
        {
            if (dialog.Session is null)
            {
                if (DateTime.UtcNow > deadline) { failure = new Exception("Browser cookie initialization timed out"); timer.Stop(); dialog.Close(); }
                return;
            }
            timer.Stop();
            try
            {
                var session = dialog.Session;
                if (!session.Cookies.Any(c => c.Name == "fixtureSession" && c.HttpOnly && c.Path == "/auth")) throw new Exception("HttpOnly cookie/path not captured");
                var options = new CrawlOptions { StartUri = origin, OutputDirectory = Path.Combine(Path.GetTempPath(), "webscrapper-browser-test-" + Guid.NewGuid().ToString("N")), RespectRobots = false, MaxFiles = 1, BrowserCookies = session.Cookies, UserAgent = session.UserAgent };
                var project = new CrawlProject(options);
                var result = await new Crawler().RunAsync(options, null, CancellationToken.None, project);
                if (result.Saved != 1 || !servedWithCookie) throw new Exception("Crawler did not use browser cookie");
                var path = Path.Combine(options.OutputDirectory, "cookie.wsproject"); await project.SaveAsync(path);
                var json = await File.ReadAllTextAsync(path);
                if (json.Contains("local-test-only") || json.Contains("fixtureSession")) throw new Exception("Cookie leaked into project");
                if (!(await CrawlProject.LoadAsync(path)).Settings.AutoCookie) throw new Exception("Auto cookie setting did not persist");
                completed = true;
            }
            catch (Exception ex) { failure = ex; }
            finally { dialog.Close(); }
        };
        timer.Start(); Application.Run(dialog); server.Close(); serving.GetAwaiter().GetResult();
        if (failure is not null) throw failure;
        if (!completed) throw new Exception("Browser closed before fixture completed");
        Console.WriteLine("PASS: WebView2 InPrivate startup, HttpOnly/path cookie capture, crawler cookie transfer, project excludes secrets and restores AutoCookie.");
    }
}
