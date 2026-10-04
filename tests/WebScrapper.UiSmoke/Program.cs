using WebScrapper.WinForms;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if (args.Contains("--browser-cookie-smoke")) { BrowserCookieSmoke.Run(); return; }
        using var form = new MainForm();
        // Render the application-owned form offscreen, without capturing the desktop.
        form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-10000, -10000);
        form.Show(); Application.DoEvents();
        var controls = Descendants(form).ToList();
        var buttons = controls.OfType<Button>().Select(b => b.Text).ToHashSet();
        foreach (var text in new[] { "Mulai salin", "Berhenti", "Scrap ulang belum berhasil", "Fix URL", "Save project", "Load project", "Buka hasil", "Lihat halaman di memori" })
            if (!buttons.Contains(text)) throw new Exception("Missing UI action: " + text);
        var tabs = controls.OfType<TabControl>().Single();
        var folder = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.GetTempPath(); Directory.CreateDirectory(folder);
        using (var bitmap = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size)); bitmap.Save(Path.Combine(folder, "main-form.png")); }
        tabs.SelectedIndex = 1; Application.DoEvents();
        using (var bitmap = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size)); bitmap.Save(Path.Combine(folder, "request-tab.png")); }
        if (controls.OfType<TextBox>().Single(t => t.PlaceholderText.StartsWith("name=value")).UseSystemPasswordChar != true) throw new Exception("Cookie must be masked.");
        tabs.SelectedIndex = 2; Application.DoEvents();
        using (var bitmap = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size)); bitmap.Save(Path.Combine(folder, "exclude-tab.png")); }
        form.Size = form.MinimumSize; Application.DoEvents();
        using (var bitmap = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size)); bitmap.Save(Path.Combine(folder, "minimum-size.png")); }
        form.Close();
        Console.WriteLine("PASS: Form construction, all actions, three tabs, masked cookie, minimum size, offscreen rendering.");
        Console.WriteLine("Preview: " + folder);
    }
    private static IEnumerable<Control> Descendants(Control parent)
    { foreach (Control child in parent.Controls) { yield return child; foreach (var nested in Descendants(child)) yield return nested; } }
}
