using System.Diagnostics;
using WebScrapper.Core;

namespace WebScrapper.WinForms;

public sealed class MainForm : Form
{
    private readonly TextBox url = new() { PlaceholderText = "https://example.com/", Dock = DockStyle.Fill };
    private readonly TextBox output = new() { Text = Path.Combine(AppContext.BaseDirectory, "downloads"), Dock = DockStyle.Fill };
    private readonly ComboBox agent = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDown };
    private readonly TextBox headers = new() { Multiline = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, PlaceholderText = "Satu header per baris: Nama: nilai\r\nContoh: Accept-Language: id-ID,id;q=0.9,en;q=0.8", AcceptsReturn = true };
    private readonly TextBox cookie = new() { Dock = DockStyle.Fill, UseSystemPasswordChar = true, PlaceholderText = "name=value; name2=value2 (opsional, hanya di memori)" };
    private readonly NumericUpDown depth = Number(0, 30, 3);
    private readonly NumericUpDown maxFiles = Number(1, 100000, 500);
    private readonly NumericUpDown delay = Number(100, 60000, 1000);
    private readonly NumericUpDown retries = Number(0, 10, 4);
    private readonly NumericUpDown fileMb = Number(1, 500, 20);
    private readonly NumericUpDown totalMb = Number(1, 100000, 500);
    private readonly NumericUpDown timeout = Number(5, 600, 45);
    private readonly NumericUpDown jitter = Number(0, 100, 25);
    private readonly CheckBox fastMode = new() { Text = "Fast (paralel)", AutoSize = true };
    private readonly CheckBox resumeLast = new() { Text = "Lanjutkan dari state/URL terakhir", Checked = true, AutoSize = true };
    private readonly CheckBox autoCookie = new() { Text = "Auto cookie sebelum mulai", Checked = true, AutoSize = true };
    private readonly Button browserCookie = new() { Text = "Buka browser / ambil cookie", AutoSize = true };
    private BrowserSession? browserSession;
    private readonly NumericUpDown parallel = Number(2, 8, 4);
    private readonly TextBox excludedPaths = new() { Text = "/v8\r\n/v9", Multiline = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, AcceptsReturn = true };
    private readonly CheckBox external = new() { Text = "Unduh aset dari domain lain (CDN)", AutoSize = true };
    private readonly CheckBox robots = new() { Text = "Hormati robots.txt dan Crawl-delay", Checked = true, AutoSize = true };
    private readonly Button start = new() { Text = "Mulai salin", AutoSize = true };
    private readonly Button stop = new() { Text = "Berhenti", Enabled = false, AutoSize = true };
    private readonly Button open = new() { Text = "Buka hasil", Enabled = false, AutoSize = true };
    private readonly Button folder = new() { Text = "Folder hasil", AutoSize = true };
    private readonly Button retry = new() { Text = "Scrap ulang belum berhasil", Enabled = false, AutoSize = true };
    private readonly Button fixUrls = new() { Text = "Fix URL", Enabled = false, AutoSize = true };
    private readonly Button save = new() { Text = "Save project", AutoSize = true };
    private readonly Button load = new() { Text = "Load project", AutoSize = true };
    private readonly Button view = new() { Text = "Lihat halaman di memori", Enabled = false, AutoSize = true };
    private readonly CheckBox followResults = new() { Text = "Auto-scroll", Checked = true, AutoSize = true, Margin = new Padding(8, 7, 3, 3) };
    private readonly CheckBox switchAgent = new() { Text = "Ganti User-Agent otomatis sekali saat diminta verifikasi browser", Checked = true, AutoSize = true };
    private readonly Label summary = new() { Text = "Siap • File yang sudah ada diperiksa sebelum download", AutoSize = true, Margin = new Padding(12, 8, 0, 0) };
    private readonly DataGridView grid = new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
        RowHeadersVisible = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect, BackgroundColor = Color.White,
        BorderStyle = BorderStyle.None, AllowUserToResizeRows = false,
        Font = new Font("Segoe UI", 8.25f, FontStyle.Regular, GraphicsUnit.Point),
        RowTemplate = { Height = 20 }
    };
    private readonly TabControl settings = new() { Dock = DockStyle.Fill };
    private CancellationTokenSource? cancellation;
    private string? entryFile;
    private bool closeWhenStopped;
    private CrawlProject? project;
    private string? projectFile;
    private readonly Dictionary<string, DataGridViewRow> pageRows = new(StringComparer.Ordinal);
    private string? latestRowUrl;
    private readonly ContextMenuStrip gridMenu = new();
    private readonly ToolStripMenuItem copyText = new("Copy text");
    private DataGridViewCell? contextCell;

    public MainForm()
    {
        Text = "Web Scrapper • Portable .NET 10";
        Size = new Size(1180, 880); MinimumSize = new Size(1020, 760);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 10);
        BackColor = Color.FromArgb(245, 247, 250);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(16) };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 315));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 86));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.Controls.Add(new Label { Text = "Salin situs dan aset untuk dibuka secara lokal", Font = new Font(Font, FontStyle.Bold), Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        layout.Controls.Add(settings, 0, 1);
        var general = new TabPage("Situs & batas unduhan") { Padding = new Padding(12) };
        var table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 9 };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130)); table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 135)); table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        for (var i = 0; i < 9; i++) table.RowStyles.Add(new RowStyle(SizeType.Percent, 100f / 9));
        Put(table, "URL awal", url, 0); table.SetColumnSpan(url, 3);
        Put(table, "Folder hasil", output, 1); table.SetColumnSpan(output, 2);
        var browse = new Button { Text = "Pilih folder…", Dock = DockStyle.Fill };
        browse.Click += (_, _) => { using var dialog = new FolderBrowserDialog { InitialDirectory = output.Text }; if (dialog.ShowDialog(this) == DialogResult.OK) output.Text = dialog.SelectedPath; };
        table.Controls.Add(browse, 3, 1);
        Put(table, "Kedalaman", depth, 2); Put(table, "Maks. file/URL", maxFiles, 2, 2);
        Put(table, "Jeda (ms)", delay, 3); Put(table, "Maks. retry", retries, 3, 2);
        Put(table, "Maks. file (MB)", fileMb, 4); Put(table, "Total baru (MB)", totalMb, 4, 2);
        Put(table, "Timeout (detik)", timeout, 5); Put(table, "Variasi jeda (%)", jitter, 5, 2);
        table.Controls.Add(external, 0, 6); table.SetColumnSpan(external, 4);
        table.Controls.Add(robots, 0, 7); table.SetColumnSpan(robots, 2);
        table.Controls.Add(resumeLast, 2, 7); table.SetColumnSpan(resumeLast, 2);
        table.Controls.Add(fastMode, 0, 8); table.SetColumnSpan(fastMode, 2);
        Put(table, "Pekerja (2–8)", parallel, 8, 2);
        parallel.Enabled = false;
        fastMode.CheckedChanged += (_, _) => parallel.Enabled = fastMode.Checked;
        general.Controls.Add(table); settings.TabPages.Add(general);
        var request = new TabPage("User-Agent, header & cookie") { Padding = new Padding(12) };
        var requestTable = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 5 };
        requestTable.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130)); requestTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        requestTable.RowStyles.Add(new RowStyle(SizeType.Absolute, 40)); requestTable.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        requestTable.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); requestTable.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        requestTable.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        agent.Items.AddRange(["WebScrapper/1.0", BrowserAgents.Chrome, BrowserAgents.Firefox]);
        agent.SelectedIndex = 1;
        Put(requestTable, "User-Agent", agent, 0); Put(requestTable, "Cookie awal", cookie, 1); Put(requestTable, "Header kustom", headers, 2);
        var cookieActions = new FlowLayoutPanel { Dock = DockStyle.Fill };
        cookieActions.Controls.AddRange([autoCookie, browserCookie]);
        requestTable.Controls.Add(cookieActions, 0, 3); requestTable.SetColumnSpan(cookieActions, 2);
        browserCookie.Click += (_, _) =>
        {
            if (!Uri.TryCreate(url.Text.Trim(), UriKind.Absolute, out var target) || !CrawlOptions.IsHttp(target) || target.UserInfo.Length > 0)
            { MessageBox.Show(this, "Isi URL HTTP/HTTPS tanpa username/password terlebih dahulu.", "Web Scrapper"); return; }
            AcquireBrowserSession(target);
        };
        cookie.TextChanged += (_, _) => browserSession = null;
        requestTable.Controls.Add(switchAgent, 0, 4); requestTable.SetColumnSpan(switchAgent, 2);
        request.Controls.Add(requestTable); settings.TabPages.Add(request);
        var exclusions = new TabPage("Exclude path") { Padding = new Padding(12) };
        var exclusionLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        exclusionLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
        exclusionLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        exclusionLayout.Controls.Add(new Label { Text = "Satu path per baris. /v8 melewati /v8 dan /v8/..., tetapi tidak /v80. Berlaku pada seluruh host, redirect, dan retry. Kosongkan untuk tanpa exclude path.", Dock = DockStyle.Fill }, 0, 0);
        exclusionLayout.Controls.Add(excludedPaths, 0, 1);
        exclusions.Controls.Add(exclusionLayout); settings.TabPages.Add(exclusions);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = true };
        actions.Controls.AddRange([start, stop, retry, fixUrls, save, load, open, folder, view, followResults, summary]); layout.Controls.Add(actions, 0, 2);
        grid.Columns.Add("resource", "URL / halaman"); grid.Columns.Add("http", "HTTP"); grid.Columns.Add("state", "Hasil"); grid.Columns.Add("detail", "Keterangan");
        grid.Columns[0].FillWeight = 40; grid.Columns[1].FillWeight = 7; grid.Columns[2].FillWeight = 13; grid.Columns[3].FillWeight = 40;
        layout.Controls.Add(grid, 0, 3);
        layout.Controls.Add(new Label { Text = "429 / 503 → Retry-After + backoff • File lama dilindungi • HTML/CSS ditautkan ke file lokal yang tersedia", Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft }, 0, 4);
        Controls.Add(layout);
        gridMenu.Items.Add(copyText);
        grid.ContextMenuStrip = gridMenu;
        grid.CellMouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Right) return;
            contextCell = e.RowIndex >= 0 && e.ColumnIndex >= 0 ? grid.Rows[e.RowIndex].Cells[e.ColumnIndex] : null;
            if (contextCell is not null) grid.CurrentCell = contextCell;
        };
        gridMenu.Opening += (_, e) =>
        {
            // Keyboard context-menu key uses the current cell.
            var cell = contextCell ?? grid.CurrentCell;
            copyText.Enabled = cell is not null && !string.IsNullOrEmpty(Convert.ToString(cell.FormattedValue));
            e.Cancel = !copyText.Enabled;
        };
        copyText.Click += (_, _) =>
        {
            var text = Convert.ToString((contextCell ?? grid.CurrentCell)?.FormattedValue);
            if (string.IsNullOrEmpty(text)) return;
            try { Clipboard.SetText(text); }
            catch (System.Runtime.InteropServices.ExternalException)
            { MessageBox.Show(this, "Clipboard sedang digunakan aplikasi lain. Silakan coba lagi.", "Web Scrapper", MessageBoxButtons.OK, MessageBoxIcon.Information); }
        };
        gridMenu.Closed += (_, _) => contextCell = null;
        start.Click += async (_, _) => await StartAsync(false);
        retry.Click += async (_, _) => await StartAsync(true);
        fixUrls.Click += async (_, _) => await FixUrlsAsync();
        save.Click += async (_, _) => await SaveProjectAsync();
        load.Click += async (_, _) => await LoadProjectAsync();
        view.Click += (_, _) => ViewContent();
        followResults.CheckedChanged += (_, _) =>
        {
            if (project is not null) project.AutoScrollResults = followResults.Checked;
            FollowLatestResult();
        };
        stop.Click += (_, _) => { cancellation?.Cancel(); stop.Enabled = false; summary.Text = "Menghentikan…"; };
        open.Click += (_, _) => { if (entryFile is not null) OpenPath(entryFile); };
        folder.Click += (_, _) => { if (Directory.Exists(output.Text)) OpenPath(output.Text); };
        FormClosing += (_, e) =>
        {
            if (cancellation is null) return;
            e.Cancel = true; closeWhenStopped = true; cancellation.Cancel(); stop.Enabled = false;
        };
        FormClosed += (_, _) => gridMenu.Dispose();
    }

    private CrawlOptions ReadOptions()
    {
        if (!Uri.TryCreate(url.Text.Trim(), UriKind.Absolute, out var uri)) throw new ArgumentException("Isi URL HTTP/HTTPS lengkap.");
        var custom = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in headers.Lines.Where(l => !string.IsNullOrWhiteSpace(l)))
        {
            var colon = line.IndexOf(':');
            if (colon < 1) throw new ArgumentException("Header harus berformat Nama: nilai.");
            if (!custom.TryAdd(line[..colon].Trim(), line[(colon + 1)..].Trim())) throw new ArgumentException("Header tidak boleh duplikat.");
        }
        var options = new CrawlOptions
        {
            StartUri = uri, OutputDirectory = output.Text.Trim(), UserAgent = agent.Text.Trim(), Headers = custom,
            CookieHeader = cookie.Text.Trim(), MaxDepth = (int)depth.Value, MaxFiles = (int)maxFiles.Value,
            RequestDelay = TimeSpan.FromMilliseconds((double)delay.Value), MaxRetries = (int)retries.Value,
            MaxFileBytes = (long)fileMb.Value * 1024 * 1024, MaxTotalBytes = (long)totalMb.Value * 1024 * 1024,
            Timeout = TimeSpan.FromSeconds((double)timeout.Value), DownloadExternalAssets = external.Checked, RespectRobots = robots.Checked,
            JitterPercent = (int)jitter.Value, AutoSwitchUserAgent = switchAgent.Checked,
            FastMode = fastMode.Checked, ParallelRequests = (int)parallel.Value,
            ResumeLastState = resumeLast.Checked,
            AutoCookie = autoCookie.Checked,
            BrowserCookies = browserSession is { } session && CrawlOptions.SameOrigin(session.Origin, uri) ? session.Cookies : [],
            ExcludedPaths = excludedPaths.Lines.Select(p => p.Trim()).Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
        };
        options.Validate(); return options;
    }

    private bool AcquireBrowserSession(Uri origin)
    {
        var target = origin;
        if (grid.CurrentRow?.Tag is string selected && Uri.TryCreate(selected, UriKind.Absolute, out var selectedUri) && CrawlOptions.SameOrigin(origin, selectedUri)) target = selectedUri;
        using var dialog = new BrowserCookieForm(target);
        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Session is not { } captured) return false;
        cookie.Clear(); browserSession = captured;
        agent.Text = captured.UserAgent;
        // Keep the browser identity stable for cookies obtained in that browser session.
        switchAgent.Checked = false;
        summary.Text = $"Sesi browser siap • {captured.Cookies.Count} cookie • User-Agent disesuaikan";
        return true;
    }

    private async Task StartAsync(bool retryIncomplete)
    {
        try
        {
            var options = ReadOptions();
            if (options.AutoCookie && options.CookieHeader.Length == 0 && (browserSession is null || !CrawlOptions.SameOrigin(browserSession.Origin, options.StartUri)))
            {
                if (!AcquireBrowserSession(options.StartUri)) return;
                options = ReadOptions();
            }
            if (project is null || project.Settings.StartUrl != options.StartUri.AbsoluteUri || !string.Equals(project.Settings.OutputDirectory, Path.GetFullPath(options.OutputDirectory), StringComparison.OrdinalIgnoreCase))
            {
                if (retryIncomplete) throw new ArgumentException("URL/folder berbeda dari project. Kembalikan pengaturan atau mulai project baru.");
                project = new CrawlProject(options) { AutoScrollResults = followResults.Checked }; projectFile = null; latestRowUrl = null; Text = "Web Scrapper • Project baru";
            }
            project.Settings = ProjectSettings.From(options);
            cancellation = new CancellationTokenSource(); SetBusy(true);
            RefreshRows(); summary.Text = "Mengunduh…";
            var reporter = new Progress<CrawlProgress>(p =>
            {
                if (IsDisposed) return;
                if (p.Page is { } page) UpdateRow(page);
                summary.Text = $"{p.Saved} file • {p.Pending} antre • {p.Bytes / 1048576d:N1} MB";
            });
            var result = await Task.Run(() => new Crawler().RunAsync(options, reporter, cancellation.Token, project, retryIncomplete));
            entryFile = result.EntryFile; open.Enabled = entryFile is not null;
            summary.Text = $"Selesai: {result.Saved} file, {result.Failed} gagal, {result.Skipped} dilewati";
        }
        catch (OperationCanceledException) { summary.Text = "Dihentikan • File lengkap tetap tersedia untuk run berikutnya"; }
        catch (Exception ex)
        {
            summary.Text = "Gagal memulai/menyelesaikan";
            var message = ex is ArgumentException ? ex.Message : "Proses gagal. Periksa akses folder, cookie, koneksi, dan apakah folder dipakai aplikasi lain.";
            MessageBox.Show(this, message, "Web Scrapper", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        finally
        {
            cancellation?.Dispose(); cancellation = null;
            RefreshRows();
            if (projectFile is not null && project is not null)
            {
                try { await project.SaveAsync(projectFile); }
                catch { MessageBox.Show(this, "Autosave gagal. Gunakan Save project ke lokasi yang dapat ditulis.", "Web Scrapper"); }
            }
            SetBusy(false);
            if (closeWhenStopped) Close();
        }
    }

    private async Task FixUrlsAsync()
    {
        if (project is null) return;
        try
        {
            cancellation = new CancellationTokenSource(); SetBusy(true);
            summary.Text = "Fix URL • memeriksa file dan cache lokal…";
            var reporter = new Progress<CrawlProgress>(p => { if (!IsDisposed) summary.Text = p.State; });
            var result = await Task.Run(() => new Crawler().FixUrlsAsync(project, reporter, cancellation.Token));
            entryFile = result.EntryFile;
            summary.Text = $"Fix URL selesai • {result.Saved} HTML/CSS • {result.Failed} URL gagal/belum valid • tanpa download";
        }
        catch (OperationCanceledException) { summary.Text = "Fix URL dihentikan"; }
        catch { MessageBox.Show(this, "Fix URL gagal. Periksa folder hasil/cache dan pastikan tidak digunakan aplikasi lain.", "Web Scrapper"); }
        finally
        {
            cancellation?.Dispose(); cancellation = null;
            if (projectFile is not null)
            {
                try { await project.SaveAsync(projectFile); }
                catch { MessageBox.Show(this, "Autosave gagal. Gunakan Save project ke lokasi yang dapat ditulis.", "Web Scrapper"); }
            }
            RefreshRows(); SetBusy(false);
            if (closeWhenStopped) Close();
        }
    }

    private void SetBusy(bool busy)
    {
        settings.Enabled = !busy; start.Enabled = !busy; stop.Enabled = busy;
        save.Enabled = !busy; load.Enabled = !busy; view.Enabled = !busy && project?.ContentCount > 0;
        retry.Enabled = !busy && project?.Pages.Any(p => !p.Successful) == true;
        fixUrls.Enabled = !busy && project?.Pages.Count > 0;
        open.Enabled = !busy && entryFile is not null && File.Exists(entryFile);
    }

    private void RefreshRows()
    {
        var firstUrl = grid.FirstDisplayedScrollingRowIndex >= 0 ? grid.Rows[grid.FirstDisplayedScrollingRowIndex].Tag as string : null;
        var selectedUrl = grid.CurrentRow?.Tag as string;
        var horizontalOffset = grid.HorizontalScrollingOffset;
        grid.Rows.Clear(); pageRows.Clear();
        if (project is null) return;
        foreach (var page in project.Pages) UpdateRow(page, follow: false);
        if (followResults.Checked) FollowLatestResult();
        else
        {
            if (selectedUrl is not null && pageRows.TryGetValue(selectedUrl, out var selected)) grid.CurrentCell = selected.Cells[0];
            if (firstUrl is not null && pageRows.TryGetValue(firstUrl, out var first)) grid.FirstDisplayedScrollingRowIndex = first.Index;
            grid.HorizontalScrollingOffset = horizontalOffset;
        }
        if (project.EntryRelativePath is not null)
            entryFile = LocalPaths.UnderRoot(project.Settings.OutputDirectory, project.EntryRelativePath);
    }

    private void UpdateRow(PageRecord page, bool follow = true)
    {
        if (!pageRows.TryGetValue(page.Url, out var row))
        {
            row = grid.Rows[grid.Rows.Add()]; row.Tag = page.Url; pageRows.Add(page.Url, row);
        }
        var uri = new Uri(page.Url);
        // Preserve distinct query variants in the list without displaying potentially sensitive query values.
        var queryLabel = uri.Query.Length == 0 ? "" : " [q:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(uri.Query)))[..8] + "]";
        row.Cells[0].Value = uri.GetLeftPart(UriPartial.Path) + queryLabel;
        row.Cells[1].Value = page.HttpStatus?.ToString() ?? "—";
        row.Cells[2].Value = page.State switch
        {
            PageState.Saved => "Berhasil", PageState.Existing => "Sudah ada", PageState.Failed => "Gagal", PageState.Skipped => "Dilewati",
            PageState.Canceled => "Dihentikan", PageState.Downloading => "Mengunduh", _ => "Antre"
        };
        row.Cells[3].Value = page.Message;
        row.Cells[3].ToolTipText = page.Message;
        row.DefaultCellStyle.BackColor = page.Successful ? Color.FromArgb(232, 247, 237) : page.State == PageState.Failed ? Color.FromArgb(255, 235, 235) : Color.White;
        if (follow) { latestRowUrl = page.Url; FollowLatestResult(); }
    }

    private void FollowLatestResult()
    {
        if (!followResults.Checked || grid.Rows.Count == 0) return;
        var row = latestRowUrl is not null && pageRows.TryGetValue(latestRowUrl, out var latest) ? latest : grid.Rows[grid.Rows.Count - 1];
        if (row.Visible && !row.Displayed) grid.FirstDisplayedScrollingRowIndex = row.Index;
    }

    private async Task SaveProjectAsync()
    {
        CrawlOptions options;
        try { options = ReadOptions(); }
        catch (ArgumentException ex) { MessageBox.Show(this, ex.Message, "Web Scrapper"); return; }
        using var dialog = new SaveFileDialog { Filter = "Web Scrapper project (*.wsproject)|*.wsproject", DefaultExt = "wsproject", FileName = projectFile is null ? "website.wsproject" : Path.GetFileName(projectFile) };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        SetBusy(true); stop.Enabled = false;
        try
        {
            if (project is null || project.Settings.StartUrl != options.StartUri.AbsoluteUri || !string.Equals(project.Settings.OutputDirectory, Path.GetFullPath(options.OutputDirectory), StringComparison.OrdinalIgnoreCase))
            { project = new CrawlProject(options) { AutoScrollResults = followResults.Checked }; entryFile = null; latestRowUrl = null; }
            project.Settings = ProjectSettings.From(options);
            await project.SynchronizeFilesAsync();
            await project.SaveAsync(dialog.FileName); projectFile = dialog.FileName;
            Text = "Web Scrapper • " + Path.GetFileName(projectFile); summary.Text = "Project tersimpan • Cookie/header tidak ikut disimpan";
            RefreshRows();
        }
        catch { MessageBox.Show(this, "Project gagal disimpan. Periksa akses folder.", "Web Scrapper"); }
        finally { SetBusy(false); }
    }

    private async Task LoadProjectAsync()
    {
        using var dialog = new OpenFileDialog { Filter = "Web Scrapper project (*.wsproject)|*.wsproject" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        SetBusy(true); stop.Enabled = false;
        try
        {
            var loaded = await CrawlProject.LoadAsync(dialog.FileName);
            var o = loaded.Settings.ToOptions();
            if ((decimal)o.RequestDelay.TotalMilliseconds < delay.Minimum || (decimal)o.RequestDelay.TotalMilliseconds > delay.Maximum || (decimal)o.Timeout.TotalSeconds < timeout.Minimum || (decimal)o.Timeout.TotalSeconds > timeout.Maximum || o.MaxFileBytes / 1048576L < fileMb.Minimum || o.MaxFileBytes / 1048576L > fileMb.Maximum || o.MaxTotalBytes / 1048576L < totalMb.Minimum || o.MaxTotalBytes / 1048576L > totalMb.Maximum) throw new ArgumentException("Pengaturan project di luar batas UI.");
            project = loaded; projectFile = dialog.FileName; entryFile = null; latestRowUrl = null;
            followResults.Checked = loaded.AutoScrollResults;
            url.Text = o.StartUri.AbsoluteUri; output.Text = o.OutputDirectory; agent.Text = o.UserAgent;
            depth.Value = o.MaxDepth; maxFiles.Value = o.MaxFiles; retries.Value = o.MaxRetries;
            delay.Value = (decimal)o.RequestDelay.TotalMilliseconds; timeout.Value = (decimal)o.Timeout.TotalSeconds;
            fileMb.Value = o.MaxFileBytes / 1048576L; totalMb.Value = o.MaxTotalBytes / 1048576L;
            external.Checked = o.DownloadExternalAssets; robots.Checked = o.RespectRobots;
            jitter.Value = o.JitterPercent;
            switchAgent.Checked = o.AutoSwitchUserAgent;
            fastMode.Checked = o.FastMode; parallel.Value = o.ParallelRequests;
            resumeLast.Checked = o.ResumeLastState;
            autoCookie.Checked = o.AutoCookie;
            browserSession = null;
            excludedPaths.Lines = o.ExcludedPaths.ToArray();
            cookie.Clear(); headers.Clear(); RefreshRows(); SetBusy(false);
            Text = "Web Scrapper • " + Path.GetFileName(projectFile);
            summary.Text = $"Project dimuat • {project.Pages.Count} URL • {project.ContentCount} halaman di memori";
        }
        catch { MessageBox.Show(this, "Project gagal dimuat. Periksa format, versi, dan lokasi folder hasil.", "Web Scrapper"); }
        finally { SetBusy(false); }
    }

    private void ViewContent()
    {
        if (grid.CurrentRow?.Tag is not string selected || project?.ContentFor(selected) is not { } text)
        { MessageBox.Show(this, "Pilih HTML/CSS yang tersedia di memori (maks. 2 MB per halaman). File lengkap tersedia di folder hasil.", "Web Scrapper"); return; }
        using var dialog = new Form { Text = "Konten asli halaman • " + new Uri(selected).GetLeftPart(UriPartial.Path), Size = new Size(900, 640), StartPosition = FormStartPosition.CenterParent };
        dialog.Controls.Add(new TextBox { Multiline = true, ReadOnly = true, Text = text, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Both, WordWrap = false, Font = new Font("Consolas", 10) });
        dialog.ShowDialog(this);
    }

    private void OpenPath(string path)
    {
        try { Process.Start(new ProcessStartInfo(Path.GetFullPath(path)) { UseShellExecute = true }); }
        catch { MessageBox.Show(this, "Hasil tidak dapat dibuka. Periksa file dan aplikasi default Windows.", "Web Scrapper"); }
    }
    private static NumericUpDown Number(int min, int max, int value) => new() { Minimum = min, Maximum = max, Value = value, Dock = DockStyle.Fill, ThousandsSeparator = true };
    private static void Put(TableLayoutPanel table, string label, Control control, int row, int col = 0)
    { table.Controls.Add(new Label { Text = label, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, col, row); table.Controls.Add(control, col + 1, row); }
}
