using Microsoft.Web.WebView2.WinForms;
using WebScrapper.Core;

namespace WebScrapper.WinForms;

public sealed record BrowserSession(Uri Origin, string UserAgent, IReadOnlyList<System.Net.Cookie> Cookies);

public sealed class BrowserCookieForm : Form
{
    private readonly Uri target;
    private readonly WebView2 browser = new() { Dock = DockStyle.Fill };
    private readonly Button use = new() { Text = "Gunakan sesi ini", AutoSize = true, Enabled = false };
    private readonly Label status = new() { AutoSize = true, Text = "Membuka browser…" };
    public BrowserSession? Session { get; private set; }

    public BrowserCookieForm(Uri target)
    {
        this.target = target;
        Text = "Auto cookie • " + target.IdnHost;
        Size = new Size(1100, 800); StartPosition = FormStartPosition.CenterParent;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1 };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 55));
        layout.Controls.Add(new Label { Text = "Selesaikan login/verifikasi bila diminta, lalu klik Gunakan sesi ini. Cookie diambil otomatis untuk situs awal; nilainya tidak ditampilkan atau disimpan ke project.", Dock = DockStyle.Fill, Padding = new Padding(10) }, 0, 0);
        layout.Controls.Add(browser, 0, 1);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(8) };
        actions.Controls.Add(use); actions.Controls.Add(status); layout.Controls.Add(actions, 0, 2); Controls.Add(layout);
        use.Click += async (_, _) =>
        {
            use.Enabled = false;
            if (await CaptureAsync()) { DialogResult = DialogResult.OK; Close(); }
        };
        Shown += async (_, _) => await InitializeAsync();
        FormClosed += (_, _) => browser.Dispose();
    }

    private async Task InitializeAsync()
    {
        try
        {
            browser.CreationProperties = new CoreWebView2CreationProperties
            {
                UserDataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WebScrapper", "Browser"),
                IsInPrivateModeEnabled = true
            };
            await browser.EnsureCoreWebView2Async();
            if (IsDisposed) return;
            browser.CoreWebView2.Settings.IsPasswordAutosaveEnabled = false;
            browser.CoreWebView2.Settings.IsGeneralAutofillEnabled = false;
            browser.CoreWebView2.NewWindowRequested += (_, e) => { e.Handled = true; };
            browser.CoreWebView2.DownloadStarting += (_, e) => e.Cancel = true;
            browser.CoreWebView2.NavigationStarting += (_, e) =>
            {
                if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var destination) || !CrawlOptions.IsHttp(destination) || destination.UserInfo.Length > 0 || (target.Scheme == "https" && destination.Scheme != "https")) e.Cancel = true;
                use.Enabled = false; Session = null;
            };
            browser.CoreWebView2.NavigationCompleted += async (_, _) => { if (!IsDisposed) await CaptureAsync(); };
            browser.CoreWebView2.Navigate(target.AbsoluteUri);
        }
        catch
        {
            if (IsDisposed) return;
            MessageBox.Show(this, "Browser tidak dapat dibuka. Pastikan Microsoft Edge WebView2 Runtime tersedia. Cookie manual tetap dapat digunakan.", "Web Scrapper");
            Close();
        }
    }

    private async Task<bool> CaptureAsync()
    {
        try
        {
            if (browser.CoreWebView2 is null || !Uri.TryCreate(browser.Source?.AbsoluteUri, UriKind.Absolute, out var current) || !CrawlOptions.SameOrigin(current, target))
            { status.Text = "Kembali ke situs awal setelah login untuk mengambil sesi."; return false; }
            var cookies = await browser.CoreWebView2.CookieManager.GetCookiesAsync(target.AbsoluteUri);
            if (IsDisposed) return false;
            Session = new BrowserSession(target, browser.CoreWebView2.Settings.UserAgent, cookies.Select(c => c.ToSystemNetCookie()).ToArray());
            status.Text = $"{Session.Cookies.Count} cookie tersedia • pastikan halaman berhasil terbuka.";
            use.Enabled = true; return true;
        }
        catch { if (!IsDisposed) { status.Text = "Cookie belum dapat diambil; coba muat ulang halaman."; use.Enabled = true; } return false; }
    }
}
